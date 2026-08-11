using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.GoalWorktreeCleanupHooks)]
public sealed class AcceptanceCohortWorkflowTests : GoalWorktreeTestBase
{
    [Fact]
    public void DisposableWorkspace_MaterializesOrderedCombinedTree_WithoutMutatingGoalBranches()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "config"));
            File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), "{}");
            RunGit(repo, "add", "config/acceptance-manifest.json");
            RunGit(repo, "commit", "-m", "Add manifest");
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "src/First.cs", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "tests/Second.cs", "second");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
            };

            string workspacePath;
            string combinedTree;
            using (var workspace = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings))
            {
                workspacePath = workspace.Path;
                combinedTree = workspace.TreeRevision;
                Assert.True(File.Exists(Path.Combine(workspace.Path, "src", "First.cs")));
                Assert.True(File.Exists(Path.Combine(workspace.Path, "tests", "Second.cs")));
                Assert.StartsWith("manifest-sha256-", GoalWorktrees.ComputeAcceptanceManifestIdentity(workspace.Path), StringComparison.Ordinal);
                Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(first.GoalId)}").Trim());
                Assert.Equal(second.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(second.GoalId)}").Trim());
                Assert.Equal(combinedTree, RunGitOutput(workspace.Path, "rev-parse", "HEAD^{tree}").Trim());
            }

            Assert.False(Directory.Exists(workspacePath));
            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(first.GoalId)}").Trim());
            Assert.Equal(second.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(second.GoalId)}").Trim());
            Assert.Equal(string.Empty, RunGitOutput(repo, "status", "--short").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void DisposableWorkspace_CleanupFailurePersistsTypedDebt_AndCanBeRetried()
    {
        var repo = CreateAcceptanceCohortRepository();
        var previousRemover = AcceptanceCohortWorkspace.WorkspaceRemover;
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "src/First.cs", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "tests/Second.cs", "second");
            var workspace = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                repo,
                main,
                [
                    Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                    Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
                ]);
            AcceptanceCohortWorkspace.WorkspaceRemover = (_, _) =>
                throw new InvalidOperationException("simulated cohort cleanup failure");

            var failure = Assert.Throws<InvalidOperationException>(workspace.Dispose);

            Assert.Contains("simulated cohort cleanup failure", failure.Message, StringComparison.Ordinal);
            Assert.Contains(GoalWorktrees.ListCleanupDebt(repo), debt =>
                debt.Path.Equals(Path.GetFullPath(workspace.Path), StringComparison.OrdinalIgnoreCase) &&
                debt.Reason == "cohort:worktree-remove-failed");
            AcceptanceCohortWorkspace.WorkspaceRemover = previousRemover;
            workspace.Dispose();
            Assert.False(Directory.Exists(workspace.Path));
        }
        finally
        {
            AcceptanceCohortWorkspace.WorkspaceRemover = previousRemover;
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ConflictingMaterialization_CleansWorkspace_AndLeavesBothBranchesUnchanged()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "seed.txt", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "seed.txt", "second");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "seed.txt", "resource:first"),
                Bind(second.GoalId, second.Revision, "seed.txt", "resource:second")
            };

            var failure = Assert.Throws<AcceptanceCohortMaterializationException>(() =>
                GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings));
            Assert.Equal(AcceptanceCohortMaterializationFailureKind.MergeConflict, failure.Kind);

            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(first.GoalId)}").Trim());
            Assert.Equal(second.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(second.GoalId)}").Trim());
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(repo, GoalWorktrees.DirectoryName), "cohort-*"));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void Store_PersistsReceiptAndTwoCoverageRowsAtomically_AndFairnessSurvivesRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "cohort.db");
            var first = Bind(new GoalId("11111111111111111111111111111111"), new string('b', 40), "src/A.cs", "resource:a");
            var second = Bind(new GoalId("22222222222222222222222222222222"), new string('c', 40), "tests/B.cs", "resource:b");
            var identity = AcceptanceCohortIdentity.Create(
                [first, second],
                new string('a', 40),
                new string('d', 40),
                "manifest-v1");
            var receipt = new AcceptanceCohortReceipt(
                "receipt-1",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.Parse("2026-08-11T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                123,
                [],
                ValidForLanding: true);
            var store = new CohortAcceptanceStore(databasePath);

            Assert.Equal(receipt, store.SaveGateReceipt(receipt));
            var reloadedReceipt = store.SaveGateReceipt(receipt);
            Assert.Equal(receipt.ReceiptId, reloadedReceipt.ReceiptId);
            Assert.Equal(receipt.Identity, reloadedReceipt.Identity);
            Assert.Equal(first, reloadedReceipt.Identity.Members[0]);
            Assert.Equal(second, reloadedReceipt.Identity.Members[1]);
            Assert.Equal(receipt.Outcome, reloadedReceipt.Outcome);
            Assert.Equal(receipt.FailedChecks, reloadedReceipt.FailedChecks);
            Assert.Equal(receipt.ValidForLanding, reloadedReceipt.ValidForLanding);
            store.PrepareLanding(receipt, new string('e', 40));
            var coverage = store.FinalizeLanding(identity.Value, receipt.ReceiptId);
            store.RecordOvertake(first.GoalId);
            store.SuppressPair("pair-1", identity.Value);

            Assert.Equal(2, coverage.Count);
            Assert.All(coverage, member =>
            {
                Assert.True(member.Landed);
                Assert.Equal(receipt.ReceiptId, member.ReceiptId);
            });
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT (SELECT COUNT(*) FROM cohort_receipts), (SELECT COUNT(*) FROM cohort_members);";
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(1, reader.GetInt32(0));
                Assert.Equal(2, reader.GetInt32(1));
            }

            var reopened = new CohortAcceptanceStore(databasePath);
            Assert.Equal(1, reopened.ReadOvertakeCount(first.GoalId));
            Assert.Contains("pair-1", reopened.ReadSuppressedPairs());
            Assert.Equal(identity.Value, reopened.TryReadReceipt(identity.Value)?.Identity.Value);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ExactTestedCombinedCommit_LandsOnce_AndBothGoalsShareReceiptCoverage()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "config"));
            File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), "{}");
            RunGit(repo, "add", "config/acceptance-manifest.json");
            RunGit(repo, "commit", "-m", "Add manifest");
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second cohort member", repo);
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Id.Value, "src/First.cs", "first");
            var second = CreateCandidate(repo, secondGoal.Id.Value, "tests/Second.cs", "second");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
            };
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));

            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                main,
                integration.TreeRevision,
                GoalWorktrees.ComputeAcceptanceManifestIdentity(integration.Path));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "receipt-shared",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                ValidForLanding: true));

            var result = LandingExecutor.ExecuteCohort(
                kernel,
                [firstGoal, secondGoal],
                workspace,
                receipt,
                integration.CommitRevision,
                store,
                ConductorAutonomyPolicy.Conservative);

            Assert.True(result.MainAdvanced);
            Assert.Equal(integration.CommitRevision, RunGitOutput(repo, "rev-parse", "main").Trim());
            Assert.Equal(integration.TreeRevision, RunGitOutput(repo, "rev-parse", "main^{tree}").Trim());
            Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<AcceptanceCohortCoverage>>(result.Coverage).Count);
            Assert.All(result.Coverage!, coverage =>
            {
                Assert.True(coverage.Landed);
                Assert.Equal(receipt.ReceiptId, coverage.ReceiptId);
            });
            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(first.GoalId)}").Trim());
            Assert.Equal(second.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(second.GoalId)}").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PostGateMainMovement_LandsNeitherMember_AndCreatesNoCoverage()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "config"));
            File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), "{}");
            RunGit(repo, "add", "config/acceptance-manifest.json");
            RunGit(repo, "commit", "-m", "Add manifest");
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First invalidated member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second invalidated member", repo);
            var boundMain = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Id.Value, "src/First.cs", "first");
            var second = CreateCandidate(repo, secondGoal.Id.Value, "tests/Second.cs", "second");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
            };
            var orchestratorWorkspace = OrchestratorWorkspace.ForDirectory(repo);
            var databasePath = Path.Combine(orchestratorWorkspace.OrchestratorDirectory, "cohort-acceptance.db");
            var store = new CohortAcceptanceStore(databasePath);
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, boundMain, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                boundMain,
                integration.TreeRevision,
                GoalWorktrees.ComputeAcceptanceManifestIdentity(integration.Path));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "receipt-invalidated",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                ValidForLanding: true));

            File.WriteAllText(Path.Combine(repo, "main-moved.txt"), "new main");
            RunGit(repo, "add", "main-moved.txt");
            RunGit(repo, "commit", "-m", "Move main after gate");

            var result = LandingExecutor.ExecuteCohort(
                kernel,
                [firstGoal, secondGoal],
                orchestratorWorkspace,
                receipt,
                integration.CommitRevision,
                store,
                ConductorAutonomyPolicy.Conservative);

            Assert.False(result.MainAdvanced);
            Assert.Contains("main changed", result.Message, StringComparison.OrdinalIgnoreCase);
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_members WHERE landed=1;";
            Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar()));
            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(first.GoalId)}").Trim());
            Assert.Equal(second.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(second.GoalId)}").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void MainMovementToCohortAncestorAtMutationBoundary_FailsCompareAndSwap()
    {
        var repo = CreateAcceptanceCohortRepository();
        var previousGitRunner = LandingExecutor.GitRunner;
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First CAS member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second CAS member", repo);
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Id.Value, "src/First.cs", "first");
            var second = CreateCandidate(repo, secondGoal.Id.Value, "tests/Second.cs", "second");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
            };
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings, main, integration.TreeRevision, GoalWorktrees.ComputeAcceptanceManifestIdentity(integration.Path));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "receipt-cas", identity, AcceptanceCohortGateOutcome.Passed, DateTimeOffset.UtcNow,
                100, [], ValidForLanding: true));
            var moved = false;
            LandingExecutor.GitRunner = (workingDirectory, args) =>
            {
                var isOldMerge = args.SequenceEqual(["merge", "--ff-only", integration.CommitRevision]);
                var isMainCas = args.Length == 4 && args[0] == "update-ref" && args[1] == "refs/heads/main";
                if (!moved && (isOldMerge || isMainCas))
                {
                    moved = true;
                    var movement = GitCli.Run(workingDirectory, "reset", "--hard", first.Revision);
                    Assert.Equal(0, movement.ExitCode);
                }
                return GitCli.Run(workingDirectory, args);
            };

            var result = LandingExecutor.ExecuteCohort(
                kernel, [firstGoal, secondGoal], workspace, receipt, integration.CommitRevision, store,
                ConductorAutonomyPolicy.Conservative);

            Assert.True(moved);
            Assert.Equal(AcceptanceCohortLandingOutcome.StateInvalidated, result.Outcome);
            Assert.False(result.MainAdvanced);
            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", "main").Trim());
            using var connection = new SqliteConnection($"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_members WHERE landed=1;";
            Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar()));
        }
        finally
        {
            LandingExecutor.GitRunner = previousGitRunner;
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void DeterministicRed_PersistsBothExactPartitionReceiptsBeforeInteractionClassification()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-attribution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "cohort.db");
            var first = Bind(new GoalId("11111111111111111111111111111111"), new string('b', 40), "src/A.cs", "resource:a");
            var second = Bind(new GoalId("22222222222222222222222222222222"), new string('c', 40), "tests/B.cs", "resource:b");
            var identity = AcceptanceCohortIdentity.Create(
                [first, second], new string('a', 40), new string('d', 40), "manifest-v1");
            var store = new CohortAcceptanceStore(databasePath);
            _ = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "combined-red",
                identity,
                AcceptanceCohortGateOutcome.Failed,
                DateTimeOffset.UtcNow,
                100,
                ["combined-check"]));
            var partitions = new[]
            {
                new AcceptanceCohortPartitionReceipt(
                    "partition-a", first.GoalId, 0, first.CandidateRevision, identity.ObservedMainRevision,
                    new string('e', 40), identity.ManifestIdentity, AcceptanceCohortGateOutcome.Passed, 10, ["a.trx"]),
                new AcceptanceCohortPartitionReceipt(
                    "partition-b", second.GoalId, 1, second.CandidateRevision, identity.ObservedMainRevision,
                    new string('f', 40), identity.ManifestIdentity, AcceptanceCohortGateOutcome.Passed, 11, ["b.trx"])
            };

            var receipt = store.SaveAttribution(
                identity.Value,
                AcceptanceCohortAttributionOutcome.InteractionOnly,
                partitions);

            Assert.Equal(AcceptanceCohortAttributionOutcome.InteractionOnly, receipt.Attribution);
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_partition_receipts WHERE cohort_id=$cohort AND outcome='Passed';";
            command.Parameters.AddWithValue("$cohort", identity.Value);
            Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar()));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void PreparedLanding_RecoversCoverageOnlyAfterExactCommitIsReachableFromMain()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var firstGoal = new GoalId("11111111111111111111111111111111");
            var secondGoal = new GoalId("22222222222222222222222222222222");
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Value, "src/A.cs", "a");
            var second = CreateCandidate(repo, secondGoal.Value, "tests/B.cs", "b");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/A.cs", "resource:a"),
                Bind(second.GoalId, second.Revision, "tests/B.cs", "resource:b")
            };
            var databasePath = Path.Combine(repo, ".orchestrator", "cohort-acceptance.db");
            var store = new CohortAcceptanceStore(databasePath);
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings, main, integration.TreeRevision, "manifest-v1");
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "recoverable-receipt",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                ValidForLanding: true));
            store.PrepareLanding(receipt, integration.CommitRevision);

            Assert.Empty(store.RecoverPreparedLandings(repo));
            RunGit(repo, "merge", "--ff-only", integration.CommitRevision);
            Assert.Equal([identity.Value], store.RecoverPreparedLandings(repo));
            Assert.Empty(store.RecoverPreparedLandings(repo));

            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_members WHERE cohort_id=$cohort AND landed=1;";
            command.Parameters.AddWithValue("$cohort", identity.Value);
            Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar()));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void MaterializationStaleBinding_PersistsTypedOutcome_CleansWorkspace_AndReturnsOrdinaryFallback()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First materialization member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second materialization member", repo);
            var firstRevision = CreateWorktreeCandidate(repo, firstGoal.Id, "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            var secondRevision = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new FakeAcceptanceVerifier(
                result: null,
                exception: new InvalidOperationException("The gate must not run after materialization fails."));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel,
                workspace,
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);

            File.WriteAllText(Path.Combine(repo, "main-moved.txt"), "new main");
            RunGit(repo, "add", "main-moved.txt");
            RunGit(repo, "commit", "-m", "Move main before cohort materialization");

            var result = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Null(result.Receipt);
            Assert.Empty(result.MemberResults);
            Assert.Contains("outcome=materialization-failure", result.Detail, StringComparison.Ordinal);
            Assert.Contains("fallback=ordinary", result.Detail, StringComparison.Ordinal);
            Assert.Equal(0, verifier.RunCount);
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(repo, GoalWorktrees.DirectoryName), "cohort-*"));
            Assert.Equal(firstRevision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(firstGoal.Id)}").Trim());
            Assert.Equal(secondRevision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(secondGoal.Id)}").Trim());

            using var connection = new SqliteConnection($"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT outcome FROM cohort_materialization_failures;";
            Assert.Equal("StaleBinding", Assert.IsType<string>(command.ExecuteScalar()));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void RetryableLandingHold_PreservesPassingReceipt_AndLaterRelandsIdenticalState()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First retryable landing member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second retryable landing member", repo);
            var firstRevision = CreateWorktreeCandidate(repo, firstGoal.Id, "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            var secondRevision = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new FakeAcceptanceVerifier(
                result: null,
                exception: new InvalidOperationException("An identical-state landing retry must reuse its exact gate receipt."));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel,
                workspace,
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var bindings = selection.BindMembers();
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            AcceptanceCohortIdentity identity;
            using (var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                       repo,
                       selection.Members[0].MainRevision,
                       bindings))
            {
                identity = AcceptanceCohortIdentity.Create(
                    bindings,
                    selection.Members[0].MainRevision,
                    integration.TreeRevision,
                    GoalWorktrees.ComputeAcceptanceManifestIdentity(integration.Path));
            }
            _ = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "retryable-hold-receipt",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                ValidForLanding: true));

            driver.LandingMutationBlocker = () => "operator-controlled landing pause";
            var held = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.Passed, held.Receipt?.Outcome);
            Assert.True(held.Receipt?.ValidForLanding);
            Assert.All(held.MemberResults.Values, result => Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome));
            Assert.Equal(identity.ObservedMainRevision, RunGitOutput(repo, "rev-parse", "main").Trim());

            driver.LandingMutationBlocker = null;
            var landed = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.Passed, landed.Receipt?.Outcome);
            Assert.True(landed.Receipt?.ValidForLanding);
            Assert.All(landed.MemberResults.Values, result => Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome));
            Assert.Equal(identity.CombinedTreeRevision, RunGitOutput(repo, "rev-parse", "main^{tree}").Trim());
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(firstRevision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(firstGoal.Id)}").Trim());
            Assert.Equal(secondRevision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(secondGoal.Id)}").Trim());
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(repo, GoalWorktrees.DirectoryName), "cohort-*"));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PostGateGoalBranchMovement_PersistsInvalidation_AndReturnsOrdinaryFallback()
    {
        var repo = CreateAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-branch-movement-{Guid.NewGuid():N}.trx");
        try
        {
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, "<TestRun />");
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First moving member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second moving member", repo);
            _ = CreateWorktreeCandidate(repo, firstGoal.Id, "src/First.cs", "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var firstWorktree = Assert.IsType<string>(GoalWorktrees.TryResolve(repo, firstGoal.Id));
            var verifier = new FakeAcceptanceVerifier(
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    TestResultPaths: [trx]),
                onRun: () =>
                {
                    File.WriteAllText(Path.Combine(firstWorktree, "post-gate.txt"), "branch moved");
                    RunGit(firstWorktree, "add", "post-gate.txt");
                    RunGit(firstWorktree, "commit", "-m", "Move branch during cohort gate");
                });
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.Invalidated, result.Receipt?.Outcome);
            Assert.Empty(result.MemberResults);
            Assert.Contains("fallback=ordinary", result.Detail, StringComparison.Ordinal);
            Assert.Equal(1, verifier.RunCount);
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(repo, GoalWorktrees.DirectoryName), "cohort-*"));
        }
        finally
        {
            if (File.Exists(trx)) File.Delete(trx);
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PersistedInfrastructureFailure_DoesNotHoldMembersIndefinitely()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First infrastructure member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second infrastructure member", repo);
            _ = CreateWorktreeCandidate(repo, firstGoal.Id, "src/First.cs", "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = FakeAcceptanceVerifier.Throws(
                new InvalidOperationException("A persisted infrastructure receipt must not rerun the cohort gate."));
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var bindings = selection.BindMembers();
            AcceptanceCohortIdentity identity;
            using (var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                       repo, selection.Members[0].MainRevision, bindings))
            {
                identity = AcceptanceCohortIdentity.Create(
                    bindings,
                    selection.Members[0].MainRevision,
                    integration.TreeRevision,
                    GoalWorktrees.ComputeAcceptanceManifestIdentity(integration.Path));
            }
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            _ = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "infrastructure-receipt",
                identity,
                AcceptanceCohortGateOutcome.InfrastructureFailure,
                DateTimeOffset.UtcNow,
                100,
                ["infrastructure:runner-loss"]));

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, result.Receipt?.Outcome);
            Assert.Empty(result.MemberResults);
            Assert.Contains("fallback=ordinary", result.Detail, StringComparison.Ordinal);
            Assert.Equal(0, verifier.RunCount);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static void AddAcceptanceManifest(string repo)
    {
        Directory.CreateDirectory(Path.Combine(repo, "config"));
        File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), "{}");
        RunGit(repo, "add", "config/acceptance-manifest.json");
        RunGit(repo, "commit", "-m", "Add manifest");
    }

    private static string CreateAcceptanceCohortRepository()
    {
        var repo = CreateSeededRepository();
        RunGit(repo, "branch", "-M", "main");
        return repo;
    }

    private static string CreateWorktreeCandidate(
        string repo,
        GoalId goalId,
        string relativePath,
        string contents)
    {
        var worktree = GoalWorktrees.Ensure(repo, goalId);
        var path = Path.Combine(worktree, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        RunGit(worktree, "add", relativePath);
        RunGit(worktree, "commit", "-m", $"Candidate {goalId.Value[..8]}");
        return RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
    }

    private static ConductorAcceptanceCohortSelection ProjectSelection(
        ConductorDriver driver,
        Goal first,
        Goal second)
    {
        var members = new[] { first, second }
            .Select(goal =>
            {
                var worktree = Assert.IsType<string>(GoalWorktrees.TryResolve(driver.ExecutionDirectory, goal.Id));
                var revisions = ConductorGitRevisionReader.ReadRequiredPair(worktree);
                Assert.False(string.IsNullOrWhiteSpace(revisions.BranchRevision));
                Assert.False(string.IsNullOrWhiteSpace(revisions.MainRevision));
                var result = driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive);
                var ready = result as GateReadyCandidateProjectionResult.Ready;
                Assert.True(
                    ready is not null,
                    result is GateReadyCandidateProjectionResult.Excluded excluded
                        ? $"Expected Ready projection for {goal.Id.Value[..8]}, but got {excluded.Reason}."
                        : $"Expected Ready projection for {goal.Id.Value[..8]}.");
                return ready.Projection;
            })
            .ToArray();
        return new ConductorAcceptanceCohortSelection(members, []);
    }

    private static (GoalId GoalId, string Revision) CreateCandidate(
        string repo,
        string goalValue,
        string relativePath,
        string contents)
    {
        var goalId = new GoalId(goalValue);
        RunGit(repo, "checkout", "-b", GoalWorktrees.BranchName(goalId), "main");
        var path = Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        RunGit(repo, "add", relativePath);
        RunGit(repo, "commit", "-m", $"Candidate {goalValue[..8]}");
        var revision = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
        RunGit(repo, "checkout", "main");
        return (goalId, revision);
    }

    private static AcceptanceCohortMemberBinding Bind(
        GoalId goalId,
        string revision,
        string path,
        string resource) => new(
            goalId,
            revision,
            revision,
            [path],
            [resource],
            ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto,
            GateReadyMergeStatus.Clean.ToString(),
            GateReadyMergeReason.NoConflictsDetected.ToString());
}
