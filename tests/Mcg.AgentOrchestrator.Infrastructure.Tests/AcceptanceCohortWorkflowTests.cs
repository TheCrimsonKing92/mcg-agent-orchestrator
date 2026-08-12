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
    public void DisposableWorkspaces_UnderNestedGitWorktree_UseShortTokensAndCleanUp()
    {
        var repo = CreateAcceptanceCohortRepository();
        var outerToken = Guid.NewGuid().ToString("N")[..8];
        var outerWorktree = Path.Combine(
            repo,
            GoalWorktrees.DirectoryName,
            $"w-{outerToken}");
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "src/First.cs", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "tests/Second.cs", "second");
            RunGit(repo, "worktree", "add", "--detach", outerWorktree, main);
            Assert.True(File.Exists(Path.Combine(outerWorktree, ".git")));

            string cohortWorkspace;
            using (var workspace = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                outerWorktree,
                main,
                [
                    Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                    Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
                ]))
            {
                cohortWorkspace = workspace.Path;
                Assert.Matches("^c-[0-9a-f]{12}$", Path.GetFileName(workspace.Path));
                Assert.True(Directory.Exists(workspace.Path));
                Assert.True(File.Exists(Path.Combine(workspace.Path, "src", "First.cs")));
                Assert.True(File.Exists(Path.Combine(workspace.Path, "tests", "Second.cs")));
            }

            Assert.False(Directory.Exists(cohortWorkspace));

            string partitionWorkspace;
            using (var workspace = GoalWorktrees.CreateAcceptancePartitionWorkspace(
                outerWorktree,
                main,
                Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first")))
            {
                partitionWorkspace = workspace.Path;
                Assert.Matches("^p-[0-9a-f]{12}$", Path.GetFileName(workspace.Path));
                Assert.True(Directory.Exists(workspace.Path));
                Assert.True(File.Exists(Path.Combine(workspace.Path, "src", "First.cs")));
            }

            Assert.False(Directory.Exists(partitionWorkspace));
            AssertNoCohortWorkspaces(outerWorktree);
        }
        finally
        {
            if (Directory.Exists(outerWorktree))
            {
                RunGit(repo, "worktree", "remove", "--force", outerWorktree);
            }
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
    public void FailedMaterialization_CleanupFailurePersistsDebt()
    {
        var repo = CreateAcceptanceCohortRepository();
        var previousRemover = AcceptanceCohortWorkspace.WorkspaceRemover;
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "seed.txt", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "seed.txt", "second");
            AcceptanceCohortWorkspace.WorkspaceRemover = (_, _) =>
                throw new InvalidOperationException("simulated pre-return cleanup failure");

            var failure = Assert.Throws<AcceptanceCohortMaterializationException>(() =>
                GoalWorktrees.CreateAcceptanceCohortWorkspace(
                    repo,
                    main,
                    [
                        Bind(first.GoalId, first.Revision, "seed.txt", "resource:first"),
                        Bind(second.GoalId, second.Revision, "seed.txt", "resource:second")
                    ]));

            Assert.Equal(AcceptanceCohortMaterializationFailureKind.WorkspaceFailure, failure.Kind);
            var debt = Assert.Single(GoalWorktrees.ListCleanupDebt(repo), item =>
                item.Reason == "cohort:worktree-remove-failed");
            Assert.True(Directory.Exists(debt.Path));
            AcceptanceCohortWorkspace.WorkspaceRemover = previousRemover;
            GoalWorktrees.RemoveAcceptanceCohortWorkspace(repo, debt.Path);
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
            AssertNoCohortWorkspaces(repo);
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
                ValidForLanding: true,
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(root, "cohort.trx")]);
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
            Assert.Equal(0, reloadedReceipt.GateExitCode);
            Assert.Equal(receipt.GateTestResultPaths!.ToArray(), reloadedReceipt.GateTestResultPaths!.ToArray());
            store.PrepareLanding(receipt, new string('e', 40));
            var coverage = store.FinalizeLanding(identity.Value, receipt.ReceiptId);
            store.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
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
    public void Store_RejectsPassingReceiptWithoutAuthoritativeEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-store-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var identity = AcceptanceCohortIdentity.Create(
                [
                    Bind(new GoalId("11111111111111111111111111111111"), new string('b', 40), "src/A.cs", "resource:a"),
                    Bind(new GoalId("22222222222222222222222222222222"), new string('c', 40), "tests/B.cs", "resource:b")
                ],
                new string('a', 40),
                new string('d', 40),
                "manifest-v1");
            var store = new CohortAcceptanceStore(Path.Combine(root, "cohort.db"));
            var validTrx = WritePassingTrx(root, "valid.trx");
            var missingFile = Path.GetFullPath(Path.Combine(root, "missing.trx"));
            var malformedTrx = Path.GetFullPath(Path.Combine(root, "malformed.trx"));
            var incoherentTrx = Path.GetFullPath(Path.Combine(root, "incoherent.trx"));
            File.WriteAllText(malformedTrx, "not xml");
            File.WriteAllText(incoherentTrx, """
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results><UnitTestResult testId="1" testName="Passes" outcome="Passed" /></Results>
                  <ResultSummary outcome="Completed"><Counters total="2" executed="2" passed="2" failed="0" /></ResultSummary>
                </TestRun>
                """);

            var missingExit = new AcceptanceCohortReceipt(
                "missing-exit", identity, AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow, 1, [], null, [validTrx],
                ValidForLanding: true);
            var missingTrx = missingExit with
            {
                ReceiptId = "missing-trx",
                GateExitCode = 0,
                GateTestResultPaths = []
            };
            var unnormalizedTrx = missingTrx with
            {
                ReceiptId = "unnormalized-trx",
                GateTestResultPaths = ["relative.trx"]
            };
            var absentTrx = missingExit with
            {
                ReceiptId = "absent-trx",
                GateExitCode = 0,
                GateTestResultPaths = [missingFile]
            };
            var malformed = absentTrx with
            {
                ReceiptId = "malformed-trx",
                GateTestResultPaths = [malformedTrx]
            };
            var incoherent = absentTrx with
            {
                ReceiptId = "incoherent-trx",
                GateTestResultPaths = [incoherentTrx]
            };

            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(missingExit));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(missingTrx));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(unnormalizedTrx));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(absentTrx));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(malformed));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(incoherent));
            Assert.Null(store.TryReadReceipt(identity.Value));
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
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "receipt-shared.trx")],
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
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "receipt-invalidated.trx")],
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
    public void MainCasFailure_RestoresIntegrationRef_AndLandsNeitherMember()
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
            RunGit(repo, "branch", LandingExecutor.IntegrationBranchName, main);
            var integrationBefore = RunGitOutput(
                repo,
                "rev-parse",
                $"refs/heads/{LandingExecutor.IntegrationBranchName}").Trim();
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings, main, integration.TreeRevision, GoalWorktrees.ComputeAcceptanceManifestIdentity(integration.Path));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "receipt-cas", identity, AcceptanceCohortGateOutcome.Passed, DateTimeOffset.UtcNow,
                100, [], 0, [WritePassingTrx(repo, "receipt-cas.trx")], ValidForLanding: true));
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
            Assert.Equal(
                integrationBefore,
                RunGitOutput(repo, "rev-parse", $"refs/heads/{LandingExecutor.IntegrationBranchName}").Trim());
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
    public void DeterministicRed_PersistsPartitionsBeforeInteractionClassification()
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
                ["combined-check"],
                GateExitCode: 1,
                GateTestResultPaths: [Path.GetFullPath("combined-red.trx")]));
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
                partitions,
                "pair-interaction",
                innocentGoalId: null);
            var secondIdentity = AcceptanceCohortIdentity.Create(
                [first, second], new string('a', 40), new string('e', 40), "manifest-v1");
            _ = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "combined-red-second",
                secondIdentity,
                AcceptanceCohortGateOutcome.Failed,
                DateTimeOffset.UtcNow,
                100,
                ["combined-check"],
                GateExitCode: 1,
                GateTestResultPaths: [Path.GetFullPath("combined-red-second.trx")]));
            _ = store.SaveAttribution(
                secondIdentity.Value,
                AcceptanceCohortAttributionOutcome.InteractionOnly,
                partitions,
                "pair-interaction-second",
                innocentGoalId: null);

            Assert.Equal(AcceptanceCohortAttributionOutcome.InteractionOnly, receipt.Attribution);
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_partition_receipts WHERE cohort_id=$cohort AND outcome='Passed';";
            command.Parameters.AddWithValue("$cohort", identity.Value);
            Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar()));
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM cohort_partition_receipts;";
            Assert.Equal(4, Convert.ToInt32(command.ExecuteScalar()));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ProductionRed_RunsBothPartitions_AndClassifiesInteraction()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First RED cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second RED cohort member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var failingTrx = WriteFailingTrx(repo, "combined-red.trx");
            var firstPassingTrx = WritePassingTrx(repo, "first-green.trx");
            var secondPassingTrx = WritePassingTrx(repo, "second-green.trx");
            var verifier = new SequenceAcceptanceVerifier(
            [
                new AcceptanceVerificationResult(
                    Passed: false,
                    Skipped: false,
                    ExitCode: 1,
                    OutputTail: "combined failure",
                    Checks: [new AcceptanceCheckResult("combined", false, 1, "combined failure")],
                    TestResultPaths: [failingTrx]),
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("first partition", true, 0, null)],
                    TestResultPaths: [firstPassingTrx]),
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("second partition", true, 0, null)],
                    TestResultPaths: [secondPassingTrx])
            ]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var mainBefore = RunGitOutput(repo, "rev-parse", "main").Trim();

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(3, verifier.RunCount);
            Assert.Equal<GoalId?>([null, firstGoal.Id, secondGoal.Id], verifier.GoalIds);
            Assert.Equal(AcceptanceCohortGateOutcome.Failed, result.Receipt?.Outcome);
            Assert.Equal(AcceptanceCohortAttributionOutcome.InteractionOnly, result.Receipt?.Attribution);
            Assert.All(result.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Equal(mainBefore, RunGitOutput(repo, "rev-parse", "main").Trim());
            AssertNoCohortWorkspaces(repo);

            var store = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            Assert.Contains(
                ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
                store.ReadSuppressedPairs());
            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_partition_receipts WHERE cohort_id=$cohort;";
            command.Parameters.AddWithValue("$cohort", result.Receipt!.Identity.Value);
            Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar()));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionRed_OneFailedMember_RequeuesGreenPeer()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "Failed cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Innocent cohort member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new SequenceAcceptanceVerifier(
            [
                FailedVerification(repo, "combined-red-one-member.trx", "combined"),
                FailedVerification(repo, "first-member-red.trx", "first partition"),
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("second partition", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "second-member-green.trx")])
            ]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var mainBefore = RunGitOutput(repo, "rev-parse", "main").Trim();

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(3, verifier.RunCount);
            Assert.Equal<GoalId?>([null, firstGoal.Id, secondGoal.Id], verifier.GoalIds);
            Assert.Equal(AcceptanceCohortAttributionOutcome.FirstMemberFailed, result.Receipt?.Attribution);
            Assert.All(result.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Equal(mainBefore, RunGitOutput(repo, "rev-parse", "main").Trim());
            var store = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            Assert.Equal(1, store.ReadOvertakeCount(secondGoal.Id));
            Assert.Equal(0, store.ReadOvertakeCount(firstGoal.Id));
            store.EnsureAttributionSideEffects(
                result.Receipt!.Identity.Value,
                result.Receipt.Attribution,
                ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
                secondGoal.Id);
            Assert.Equal(1, store.ReadOvertakeCount(secondGoal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
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
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "recoverable-receipt.trx")],
                ValidForLanding: true));
            RunGit(repo, "branch", LandingExecutor.IntegrationBranchName, main);
            store.PrepareLanding(receipt, integration.CommitRevision, main);
            RunGit(
                repo,
                "update-ref",
                $"refs/heads/{LandingExecutor.IntegrationBranchName}",
                integration.CommitRevision,
                main);

            Assert.Empty(store.RecoverPreparedLandings(repo));
            Assert.Equal(
                main,
                RunGitOutput(repo, "rev-parse", $"refs/heads/{LandingExecutor.IntegrationBranchName}").Trim());
            store.PrepareLanding(receipt, integration.CommitRevision, main);
            RunGit(
                repo,
                "update-ref",
                $"refs/heads/{LandingExecutor.IntegrationBranchName}",
                integration.CommitRevision,
                main);
            RunGit(repo, "merge", "--ff-only", integration.CommitRevision);
            var recovery = Assert.Single(store.RecoverPreparedLandings(repo));
            Assert.Equal(identity.Value, recovery.Receipt.Identity.Value);
            store.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
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
    public void ConstructorRecovery_ReplaysBothMemberLandingEffects()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First recovered member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second recovered member", repo);
            var firstRevision = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            var secondRevision = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var bindings = new[]
            {
                Bind(firstGoal.Id, firstRevision, "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "resource:first"),
                Bind(secondGoal.Id, secondRevision, "tests/Second.cs", "resource:second")
            };
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                main,
                integration.TreeRevision,
                GoalWorktrees.ComputeAcceptanceManifestIdentity(integration.Path));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "constructor-recovery-receipt",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "constructor-recovery.trx")],
                ValidForLanding: true));
            RunGit(repo, "branch", LandingExecutor.IntegrationBranchName, main);
            store.PrepareLanding(receipt, integration.CommitRevision, main);
            RunGit(
                repo,
                "update-ref",
                $"refs/heads/{LandingExecutor.IntegrationBranchName}",
                integration.CommitRevision,
                main);
            RunGit(repo, "update-ref", "refs/heads/main", integration.CommitRevision, main);

            var recoveredDriver = new ConductorDriver(
                kernel,
                workspace,
                FakeAcceptanceVerifier.Throws(new InvalidOperationException("Recovery must not run a gate.")),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default());
            var recoveredReceipts = new List<ConductorLandingReceipt>();
            recoveredDriver.SuccessfulLandingSink = recoveredReceipts.Add;

            Assert.Equal(2, recoveredReceipts.Count);
            Assert.Equal(
                new[] { firstGoal.Id.Value, secondGoal.Id.Value }.Order(StringComparer.Ordinal),
                recoveredReceipts.Select(item => item.GoalId).Order(StringComparer.Ordinal));
            Assert.All(new[] { firstGoal, secondGoal }, goal =>
                Assert.True(GoalOperationJournal.HasCompletedLandingEvidence(
                    GoalOperationJournal.Read(repo, goal.Id))));
            var lifecycleText = string.Join(
                '\n',
                Directory.EnumerateFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl")
                    .Select(File.ReadAllText));
            Assert.Equal(2, lifecycleText.Split("\"eventType\":\"GoalLanded\"", StringSplitOptions.None).Length - 1);
            Assert.Empty(store.RecoverPreparedLandings(repo));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionBatch_SelectsRunsPersistsLandsAndCleansOneSharedCohort()
    {
        var repo = CreateAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-production-{Guid.NewGuid():N}.trx");
        var previousIsolatedRoot = Environment.GetEnvironmentVariable(
            DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                Path.Combine(repo, ".dotnet-test-root"));
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First production cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second production cohort member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new FakeAcceptanceVerifier(new AcceptanceVerificationResult(
                Passed: true,
                Skipped: false,
                ExitCode: 0,
                OutputTail: null,
                Checks: [new AcceptanceCheckResult("shared-production-gate", true, 0, null)],
                TestResultPaths: [trx]));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel,
                workspace,
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default());
            var fairnessStore = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            fairnessStore.RecordOvertake(firstGoal.Id);
            fairnessStore.RecordOvertake(secondGoal.Id);
            var stopPath = Path.Combine(repo, "stop-does-not-exist");

            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                stopPath,
                maxIterations: 1);

            Assert.Equal(1, verifier.RunCount);
            Assert.True(verifier.StableSlotLeaseObserved);
            Assert.Equal(0, fairnessStore.ReadOvertakeCount(firstGoal.Id));
            Assert.Equal(0, fairnessStore.ReadOvertakeCount(secondGoal.Id));
            Assert.Equal(2, summary.Advanced);
            Assert.True(File.Exists(Path.Combine(
                repo,
                "src",
                "Mcg.AgentOrchestrator.Infrastructure",
                "First.cs")));
            Assert.True(File.Exists(Path.Combine(repo, "tests", "Second.cs")));
            AssertNoCohortWorkspaces(repo);
            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*), MIN(gate_exit_code), MAX(json_array_length(gate_test_result_paths_json))
                FROM cohort_receipts;
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(0, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt32(2));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                previousIsolatedRoot);
            if (File.Exists(trx)) File.Delete(trx);
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void CohortStableSlotLease_RefusesToExceedTrustedHostGateCap()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-slot-cap-{Guid.NewGuid():N}");
        var previousIsolatedRoot = Environment.GetEnvironmentVariable(
            DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                root);
            using var first = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(0, TimeSpan.Zero)).Lease;
            using var second = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(1, TimeSpan.Zero)).Lease;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                runInline: true);

            Assert.Throws<DotnetBuildSlotsBusyException>(() =>
                coordinator.AcquireCohortStableSlotLease("cohort-v2-slot-cap", timeout: TimeSpan.Zero));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                previousIsolatedRoot);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void StaleMaterialization_PersistsOutcomeAndUsesOrdinaryFallback()
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
            var store = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            store.RecordOvertake(firstGoal.Id);
            var gateAdmissions = 0;

            File.WriteAllText(Path.Combine(repo, "main-moved.txt"), "new main");
            RunGit(repo, "add", "main-moved.txt");
            RunGit(repo, "commit", "-m", "Move main before cohort materialization");

            var result = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive,
                onGateAdmitted: () =>
                {
                    gateAdmissions++;
                    driver.RecordCohortAdmissionFairness([firstGoal, secondGoal], selection);
                });

            Assert.Null(result.Receipt);
            Assert.Empty(result.MemberResults);
            Assert.Contains("outcome=materialization-failure", result.Detail, StringComparison.Ordinal);
            Assert.Contains("fallback=ordinary", result.Detail, StringComparison.Ordinal);
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(0, gateAdmissions);
            Assert.Equal(1, store.ReadOvertakeCount(firstGoal.Id));
            AssertNoCohortWorkspaces(repo);
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
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "retryable-hold-receipt.trx")],
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
            AssertNoCohortWorkspaces(repo);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void CachedPassWithoutEvidence_InvalidatesBeforeLanding()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First cached receipt member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second cached receipt member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = FakeAcceptanceVerifier.Throws(
                new InvalidOperationException("An invalid cached pass must not be reused or rerun in place."));
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
            var databasePath = Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db");
            var store = new CohortAcceptanceStore(databasePath);
            _ = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "legacy-cached-pass",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "legacy-cached-pass.trx")],
                ValidForLanding: true));
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE cohort_receipts SET gate_exit_code=NULL, gate_test_result_paths_json='[]';";
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.Invalidated, result.Receipt?.Outcome);
            Assert.False(result.Receipt?.ValidForLanding);
            Assert.Equal(2, result.MemberResults.Count);
            Assert.All(result.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Contains("lacks successful exit", result.Detail, StringComparison.Ordinal);
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(identity.ObservedMainRevision, RunGitOutput(repo, "rev-parse", "main").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PostGateBranchMove_HoldsMembersForFreshProjection()
    {
        var repo = CreateAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-branch-movement-{Guid.NewGuid():N}.trx");
        try
        {
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First moving member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second moving member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
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
            Assert.Equal(2, result.MemberResults.Count);
            Assert.All(result.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Contains("fresh Ready projection", result.Detail, StringComparison.Ordinal);
            Assert.Equal(1, verifier.RunCount);
            AssertNoCohortWorkspaces(repo);
        }
        finally
        {
            if (File.Exists(trx)) File.Delete(trx);
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PersistedInfrastructureFailure_HoldsForFreshProjection()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First infrastructure member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second infrastructure member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
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
                ["infrastructure:runner-loss"],
                GateExitCode: null,
                GateTestResultPaths: []));

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, result.Receipt?.Outcome);
            Assert.Equal(2, result.MemberResults.Count);
            Assert.All(result.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Contains("fresh Ready projection", result.Detail, StringComparison.Ordinal);
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

    private static string ValidPassingTrx() => """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testId="1" testName="Passes" outcome="Passed" />
          </Results>
          <ResultSummary outcome="Completed">
            <Counters total="1" executed="1" passed="1" failed="0" />
          </ResultSummary>
        </TestRun>
        """;

    private static string WritePassingTrx(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        File.WriteAllText(path, ValidPassingTrx());
        return path;
    }

    private static string WriteFailingTrx(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        File.WriteAllText(path, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" testName="Fails" outcome="Failed" />
              </Results>
              <ResultSummary outcome="Failed">
                <Counters total="1" executed="1" passed="0" failed="1" />
              </ResultSummary>
            </TestRun>
            """);
        return path;
    }

    private static AcceptanceVerificationResult FailedVerification(
        string directory,
        string trxFileName,
        string checkName) => new(
            Passed: false,
            Skipped: false,
            ExitCode: 1,
            OutputTail: $"{checkName} failed",
            Checks: [new AcceptanceCheckResult(checkName, false, 1, $"{checkName} failed")],
            TestResultPaths: [WriteFailingTrx(directory, trxFileName)]);

    private static string CreateAcceptanceCohortRepository()
    {
        var repo = CreateSeededRepository();
        RunGit(repo, "branch", "-M", "main");
        return repo;
    }

    private static void AssertNoCohortWorkspaces(string repo)
    {
        var worktreeRoot = Path.Combine(repo, GoalWorktrees.DirectoryName);
        Assert.Empty(Directory.EnumerateDirectories(worktreeRoot, "c-*"));
        Assert.Empty(Directory.EnumerateDirectories(worktreeRoot, "p-*"));
        Assert.Empty(Directory.EnumerateDirectories(worktreeRoot, "cohort-*"));
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

    private sealed class SequenceAcceptanceVerifier(
        IReadOnlyList<AcceptanceVerificationResult> results) : IGoalAcceptanceVerifier
    {
        private int _nextResult;

        internal int RunCount => _nextResult;
        internal List<GoalId?> GoalIds { get; } = [];

        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default)
        {
            GoalIds.Add(goalId);
            Assert.True(_nextResult < results.Count, "The cohort invoked the acceptance verifier more times than expected.");
            return Task.FromResult(results[_nextResult++]);
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Focused evidence is not used by the cohort sequence fixture.");
    }
}
