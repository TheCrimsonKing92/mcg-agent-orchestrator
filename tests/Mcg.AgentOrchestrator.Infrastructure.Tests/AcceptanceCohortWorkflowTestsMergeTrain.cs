using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class AcceptanceCohortWorkflowTestsMergeTrain : AcceptanceCohortWorkflowTests
{

    [Fact]
    public void MergeTrain_ConflictingMemberIsEjected_AndRemainingTrainContinues()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "seed.txt", "first");
            var conflicting = CreateCandidate(repo, "22222222222222222222222222222222", "seed.txt", "second");
            var third = CreateCandidate(repo, "33333333333333333333333333333333", "src/Third.cs", "third");

            using var workspace = GoalWorktrees.CreateMergeTrainWorkspace(
                repo,
                main,
                [
                    TrainBind(first.GoalId, first.Revision, "seed.txt", "resource:first"),
                    TrainBind(conflicting.GoalId, conflicting.Revision, "seed.txt", "resource:second"),
                    TrainBind(third.GoalId, third.Revision, "src/Third.cs", "resource:third")
                ]);

            Assert.Equal([first.GoalId, third.GoalId], workspace.Members.Select(member => member.GoalId));
            var ejection = Assert.Single(workspace.Ejections);
            Assert.Equal(conflicting.GoalId, ejection.GoalId);
            Assert.Equal(MergeTrainEjectionReason.RebaseConflict, ejection.Reason);
            Assert.Equal(["seed.txt"], ejection.ConflictPaths);
            Assert.True(File.Exists(Path.Combine(workspace.Path, "src", "Third.cs")));
            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(first.GoalId)}").Trim());
            Assert.Equal(conflicting.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(conflicting.GoalId)}").Trim());
            Assert.Equal(third.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(third.GoalId)}").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ExactTestedMergeTrainCommit_LandsThreeGoalsWithPerGoalCoverage()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First train member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second train member", repo);
            var thirdGoal = CreateCompletedGoal(kernel, "Third train member", repo);
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Id.Value, "src/TrainFirst.cs", "first");
            var second = CreateCandidate(repo, secondGoal.Id.Value, "tests/TrainSecond.cs", "second");
            var third = CreateCandidate(repo, thirdGoal.Id.Value, "docs/train-third.md", "third");
            foreach (var (goal, candidate) in new[]
                     {
                         (firstGoal, first.Revision),
                         (secondGoal, second.Revision),
                         (thirdGoal, third.Revision)
                     })
            {
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
                    goal.Objective,
                    ["The merge train full gate passes for this member"],
                    VerificationClass.TestVerifiable,
                    [],
                    []));
                kernel.MapCriterionEvidenceOwner(
                    goal.Id,
                    0,
                    1,
                    CriterionEvidenceOwner.Acceptance,
                    "test",
                    CriterionEvidenceScopes.FullAcceptanceGate,
                    expectedCandidateSha: main);
            }
            var bindings = new[]
            {
                TrainBind(first.GoalId, first.Revision, "src/TrainFirst.cs", "resource:first"),
                TrainBind(second.GoalId, second.Revision, "tests/TrainSecond.cs", "resource:second"),
                TrainBind(third.GoalId, third.Revision, "docs/train-third.md", "resource:third")
            };
            var orchestratorWorkspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new MergeTrainAcceptanceStore(
                Path.Combine(orchestratorWorkspace.OrchestratorDirectory, "merge-train-acceptance.db"));

            using var integration = GoalWorktrees.CreateMergeTrainWorkspace(repo, main, bindings);
            var identity = MergeTrainIdentity.Create(
                integration.Members,
                main,
                integration.TreeRevision,
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path,
                    bindings.SelectMany(member => member.LandingPaths).ToArray()));
            var receipt = store.SaveGateReceipt(new MergeTrainReceipt(
                "receipt-train-shared",
                identity,
                MergeTrainGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "receipt-train-shared.trx")],
                ValidForLanding: true));

            var result = LandingExecutor.ExecuteMergeTrain(
                kernel,
                [firstGoal, secondGoal, thirdGoal],
                orchestratorWorkspace,
                receipt,
                integration.CommitRevision,
                store,
                ConductorAutonomyPolicy.Conservative);

            Assert.True(result.MainAdvanced);
            Assert.Equal(integration.TreeRevision, RunGitOutput(repo, "rev-parse", "main^{tree}").Trim());
            Assert.Equal(3, Assert.IsAssignableFrom<IReadOnlyList<AcceptanceCohortCoverage>>(result.Coverage).Count);
            Assert.All(result.Coverage!, coverage => Assert.True(coverage.Landed));
            Assert.All(new[]
            {
                (Goal: firstGoal, Candidate: first.Revision),
                (Goal: secondGoal, Candidate: second.Revision),
                (Goal: thirdGoal, Candidate: third.Revision)
            }, item =>
            {
                var obligation = Assert.Single(item.Goal.CriterionEvidenceObligations);
                Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
                Assert.Equal(item.Candidate, obligation.CandidateSha);
                Assert.Equal($"full-acceptance:{item.Candidate}", obligation.ReceiptId);
                var intent = GoalOperationJournal.TryGetLatestLandingIntent(GoalOperationJournal.Read(repo, item.Goal.Id));
                Assert.NotNull(intent);
                Assert.Equal(main, intent.BoundMainRevision);
                Assert.Null(intent.PreviousIntegrationRevision);
                Assert.Contains(
                    GoalOperationJournal.Read(repo, item.Goal.Id).Entries,
                    operation => operation.Operation == "conductor:land" && operation.Status == GoalOperationStatus.Completed);
            });
            store.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ExactTestedMergeTrainCommit_OperatorOwnedObligationHoldsWholeTrainBeforeMutation()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var goals = new[]
            {
                CreateCompletedGoal(kernel, "First held train member", repo),
                CreateCompletedGoal(kernel, "Second held train member", repo),
                CreateCompletedGoal(kernel, "Third held train member", repo)
            };
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var candidates = new[]
            {
                CreateCandidate(repo, goals[0].Id.Value, "src/HeldTrainFirst.cs", "first"),
                CreateCandidate(repo, goals[1].Id.Value, "tests/HeldTrainSecond.cs", "second"),
                CreateCandidate(repo, goals[2].Id.Value, "docs/held-train-third.md", "third")
            };
            foreach (var (goal, candidate) in goals.Zip(candidates))
            {
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
                    goal.Objective,
                    ["Full gate passes", "Operator observes the native result"],
                    VerificationClass.TestVerifiable,
                    [],
                    []));
                kernel.MapCriterionEvidenceOwner(
                    goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance, "test",
                    CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: main);
                if (goal == goals[0])
                {
                    kernel.MapCriterionEvidenceOwner(
                        goal.Id, 1, 1, CriterionEvidenceOwner.Operator, "test",
                        "operator:native-observation", expectedCandidateSha: candidate.Revision);
                }
            }

            var bindings = new[]
            {
                TrainBind(candidates[0].GoalId, candidates[0].Revision, "src/HeldTrainFirst.cs", "resource:first"),
                TrainBind(candidates[1].GoalId, candidates[1].Revision, "tests/HeldTrainSecond.cs", "resource:second"),
                TrainBind(candidates[2].GoalId, candidates[2].Revision, "docs/held-train-third.md", "resource:third")
            };
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var databasePath = Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db");
            var store = new MergeTrainAcceptanceStore(databasePath);
            using var integration = GoalWorktrees.CreateMergeTrainWorkspace(repo, main, bindings);
            var identity = MergeTrainIdentity.Create(integration.Members, main, integration.TreeRevision, "manifest-v1");
            var receipt = store.SaveGateReceipt(new MergeTrainReceipt(
                "receipt-train-held",
                identity,
                MergeTrainGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                0,
                [WritePassingTrx(repo, "receipt-train-held.trx")],
                ValidForLanding: true));

            var result = LandingExecutor.ExecuteMergeTrain(
                kernel,
                goals,
                workspace,
                receipt,
                integration.CommitRevision,
                store,
                ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortLandingOutcome.RetryableHold, result.Outcome);
            Assert.Contains(goals[0].Id.Value[..8], result.Message, StringComparison.Ordinal);
            Assert.Contains("criterion-v1-1:Operator:Pending", result.Message, StringComparison.Ordinal);
            Assert.Equal(main, RunGitOutput(repo, "rev-parse", "main").Trim());
            Assert.All(goals, goal => Assert.Null(
                GoalOperationJournal.TryGetLatestLandingIntent(GoalOperationJournal.Read(repo, goal.Id))));
            Assert.Equal(CriterionEvidenceState.Satisfied, goals[0].CriterionEvidenceObligations[0].State);
            Assert.Equal(CriterionEvidenceState.Pending, goals[0].CriterionEvidenceObligations[1].State);
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM merge_train_landings;";
            Assert.Equal(0L, (long)command.ExecuteScalar()!);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PreparedMergeTrainLanding_RecoversUntilPostLandingEffectsComplete()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "src/RecoverTrainFirst.cs", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "tests/RecoverTrainSecond.cs", "second");
            var third = CreateCandidate(repo, "33333333333333333333333333333333", "src/RecoverTrainThird.cs", "third");
            var bindings = new[]
            {
                TrainBind(first.GoalId, first.Revision, "src/RecoverTrainFirst.cs", "resource:first"),
                TrainBind(second.GoalId, second.Revision, "tests/RecoverTrainSecond.cs", "resource:second"),
                TrainBind(third.GoalId, third.Revision, "src/RecoverTrainThird.cs", "resource:third")
            };
            var store = new MergeTrainAcceptanceStore(
                Path.Combine(repo, ".orchestrator", "merge-train-acceptance.db"));
            using var integration = GoalWorktrees.CreateMergeTrainWorkspace(repo, main, bindings);
            var identity = MergeTrainIdentity.Create(
                integration.Members,
                main,
                integration.TreeRevision,
                "manifest-v1");
            var receipt = store.SaveGateReceipt(new MergeTrainReceipt(
                "recoverable-train-receipt",
                identity,
                MergeTrainGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "recoverable-train.trx")],
                ValidForLanding: true));
            store.PrepareLanding(receipt, integration.CommitRevision, main);

            Assert.Empty(store.RecoverPreparedLandings(repo));
            RunGit(repo, "update-ref", "refs/heads/main", integration.CommitRevision, main);
            var recovery = Assert.Single(store.RecoverPreparedLandings(repo));
            Assert.Equal(identity.Value, recovery.Receipt.Identity.Value);
            Assert.Equal(3, recovery.Coverage.Count);
            Assert.Single(store.RecoverPreparedLandings(repo));

            store.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
            Assert.Empty(store.RecoverPreparedLandings(repo));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionMergeTrain_OneGateAttemptLandsThreeMembers()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            AddSourceSizeAuthority(repo, ("tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs", 1));
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First production train member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second production train member", repo);
            var thirdGoal = CreateCompletedGoal(kernel, "Third production train member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs",
                "first");
            _ = CreateWorktreeCandidate(
                repo,
                secondGoal.Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TrainSecond.cs",
                "second");
            _ = CreateWorktreeCandidate(
                repo,
                thirdGoal.Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/TrainThird.cs",
                "third");
            var verifier = new SequenceAcceptanceVerifier(
            [
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("merge train", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "train-green.trx")])
            ]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupContext.Hooks);
            var selection = ProjectTrainSelection(driver, firstGoal, secondGoal, thirdGoal);
            var landings = new List<ConductorLandingReceipt>();
            driver.SuccessfulLandingSink = landings.Add;

            var result = driver.RunMergeTrain(
                selection,
                [firstGoal, secondGoal, thirdGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Equal(1, verifier.RunCount);
            var buildPaths = Assert.Single(verifier.ObservedBuildPaths);
            Assert.NotEmpty(buildPaths);
            Assert.All(buildPaths, path => Assert.True(
                cleanupContext.Hooks.BuildStorageRoot!.ContainsPath(path),
                $"Acceptance lease path must belong to the configured fixture root: {path}"));
            Assert.Equal(3, result.MemberResults.Count);
            Assert.Equal(3, landings.Count);
            Assert.Equal(MergeTrainGateOutcome.Passed, result.Receipt?.Outcome);
            Assert.Contains("attempts=1 landings=3", result.Detail, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(
                repo,
                "tests",
                "Mcg.AgentOrchestrator.Core.Tests",
                "TrainFirst.cs")));
            Assert.True(File.Exists(Path.Combine(
                repo,
                "tests",
                "Mcg.AgentOrchestrator.Infrastructure.Tests",
                "TrainSecond.cs")));
            Assert.True(File.Exists(Path.Combine(
                repo,
                "tests",
                "Mcg.AgentOrchestrator.Dashboard.Tests",
                "TrainThird.cs")));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionMergeTrain_RedNewestDropsThenShorterTrainLands()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First RED train member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second RED train member", repo);
            var thirdGoal = CreateCompletedGoal(kernel, "Newest RED train member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "tests/Mcg.AgentOrchestrator.Core.Tests/RedTrainFirst.cs",
                "first");
            _ = CreateWorktreeCandidate(
                repo,
                secondGoal.Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RedTrainSecond.cs",
                "second");
            _ = CreateWorktreeCandidate(
                repo,
                thirdGoal.Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/RedTrainNewest.cs",
                "newest");
            var verifier = new SequenceAcceptanceVerifier(
            [
                FailedVerification(repo, "train-three-red.trx", "three-member train"),
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("shorter train", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "train-two-green.trx")]),
                FailedVerification(repo, "train-newest-solo-red.trx", "newest solo")
            ]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupContext.Hooks);
            var selection = ProjectTrainSelection(driver, firstGoal, secondGoal, thirdGoal);

            var result = driver.RunMergeTrain(
                selection,
                [firstGoal, secondGoal, thirdGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Equal(2, result.MemberResults.Count);
            Assert.DoesNotContain(thirdGoal.Id.Value, result.MemberResults.Keys);
            var dropped = Assert.Single(result.Ejections, item => item.Reason == MergeTrainEjectionReason.RedNewestMember);
            Assert.Equal(thirdGoal.Id, dropped.GoalId);
            Assert.True(File.Exists(Path.Combine(
                repo,
                "tests",
                "Mcg.AgentOrchestrator.Core.Tests",
                "RedTrainFirst.cs")));
            Assert.True(File.Exists(Path.Combine(
                repo,
                "tests",
                "Mcg.AgentOrchestrator.Infrastructure.Tests",
                "RedTrainSecond.cs")));
            Assert.False(File.Exists(Path.Combine(
                repo,
                "tests",
                "Mcg.AgentOrchestrator.Dashboard.Tests",
                "RedTrainNewest.cs")));
            var soloProjection = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                driver.ProjectGateReadyCandidate(thirdGoal, ConductorAutonomyPolicy.Permissive));
            Assert.Equal(
                ["tests/Mcg.AgentOrchestrator.Dashboard.Tests/RedTrainNewest.cs"],
                soloProjection.Projection.LandingPaths);
            var soloResult = driver.RunParallelLandingAcceptance(
                ConductorParallelAcceptanceCandidate.Create(
                    thirdGoal,
                    slotIndex: 0,
                    soloProjection.Projection.LandingPaths),
                ConductorAutonomyPolicy.Permissive,
                stableSlotLease: null,
                CancellationToken.None,
                new AcceptanceRunExecutionOptions());
            Assert.NotNull(soloResult.Acceptance);
            Assert.False(soloResult.Acceptance!.Passed);
            Assert.Equal(3, verifier.RunCount);
            Assert.Equal<GoalId?>([firstGoal.Id, firstGoal.Id, thirdGoal.Id], verifier.GoalIds);
            Assert.Equal(3, verifier.ChangedFiles[0].Count);
            Assert.Equal(2, verifier.ChangedFiles[1].Count);
            Assert.Equal(
                ["tests/Mcg.AgentOrchestrator.Dashboard.Tests/RedTrainNewest.cs"],
                verifier.ChangedFiles[2]);
            Assert.False(File.Exists(Path.Combine(
                repo,
                "tests",
                "Mcg.AgentOrchestrator.Dashboard.Tests",
                "RedTrainNewest.cs")));
            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM merge_train_receipts;";
            Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar()));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionMergeTrain_StaleMaterializationReturnsTypedFallback()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First stale train member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second stale train member", repo);
            var thirdGoal = CreateCompletedGoal(kernel, "Third stale train member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "tests/Mcg.AgentOrchestrator.Core.Tests/StaleTrainFirst.cs",
                "first");
            _ = CreateWorktreeCandidate(
                repo,
                secondGoal.Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/StaleTrainSecond.cs",
                "second");
            _ = CreateWorktreeCandidate(
                repo,
                thirdGoal.Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/StaleTrainThird.cs",
                "third");
            var verifier = new FakeAcceptanceVerifier(
                result: null,
                exception: new InvalidOperationException("A stale train must not invoke the gate."));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectTrainSelection(driver, firstGoal, secondGoal, thirdGoal);
            File.WriteAllText(Path.Combine(repo, "move-main.txt"), "new main");
            RunGit(repo, "add", "move-main.txt");
            RunGit(repo, "commit", "-m", "Move main before train materialization");

            var result = driver.RunMergeTrain(
                selection,
                [firstGoal, secondGoal, thirdGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Null(result.Receipt);
            Assert.Empty(result.MemberResults);
            Assert.Equal(3, result.Ejections.Count);
            Assert.All(result.Ejections, item => Assert.Equal(MergeTrainEjectionReason.StaleBinding, item.Reason));
            Assert.Contains("materialization fallback: StaleBinding", result.Detail, StringComparison.Ordinal);
            Assert.Equal(0, verifier.RunCount);
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionMergeTrain_ManifestFailureReturnsTypedFallback()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First manifest-failure train member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second manifest-failure train member", repo);
            var thirdGoal = CreateCompletedGoal(kernel, "Third manifest-failure train member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "tests/Mcg.AgentOrchestrator.Core.Tests/ManifestTrainFirst.cs",
                "first");
            _ = CreateWorktreeCandidate(
                repo,
                secondGoal.Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ManifestTrainSecond.cs",
                "second");
            _ = CreateWorktreeCandidate(
                repo,
                thirdGoal.Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/ManifestTrainThird.cs",
                "third");
            var verifier = new ManifestThrowingAcceptanceVerifier(
                new InvalidOperationException("acceptance manifest changed during train materialization"));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectTrainSelection(driver, firstGoal, secondGoal, thirdGoal);

            var result = driver.RunMergeTrain(
                selection,
                [firstGoal, secondGoal, thirdGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Null(result.Receipt);
            Assert.Empty(result.MemberResults);
            Assert.Empty(result.Ejections);
            Assert.Contains("manifest fallback", result.Detail, StringComparison.Ordinal);
            Assert.Equal(0, verifier.RunCount);
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionMergeTrain_RatchetBreach_SkipsGateLease()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            AddSourceSizeAuthority(repo, ("tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs", 2));
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First ratchet train member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second ratchet train member", repo);
            var thirdGoal = CreateCompletedGoal(kernel, "Third ratchet train member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs",
                "one\ntwo\nthree");
            _ = CreateWorktreeCandidate(
                repo,
                secondGoal.Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TrainSecond.cs",
                "second");
            _ = CreateWorktreeCandidate(
                repo,
                thirdGoal.Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/TrainThird.cs",
                "third");
            var verifier = new SequenceAcceptanceVerifier(
            [
                new AcceptanceVerificationResult(
                    Passed: false,
                    Skipped: false,
                    ExitCode: 1,
                    OutputTail: "source size breach",
                    Checks: [new AcceptanceCheckResult("source size ratchet preflight", false, 1, "breach")])
            ]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectTrainSelection(driver, firstGoal, secondGoal, thirdGoal);
            var gateAdmitted = false;

            var result = driver.RunMergeTrain(
                selection,
                [firstGoal, secondGoal, thirdGoal],
                ConductorAutonomyPolicy.Permissive,
                onGateAdmitted: () => gateAdmitted = true);

            Assert.False(gateAdmitted, "The merge-train stable-slot lease must not be acquired for a ratchet breach.");
            Assert.Equal(0, verifier.RunCount);
            Assert.Contains("outcome=Failed", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

}
