using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class AcceptanceCohortWorkflowTestsGateOutcomes : AcceptanceCohortWorkflowTests
{

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
            using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
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
    public void ProductionGate_InvalidResultPath_PersistsStructuredInfrastructureFailure()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First invalid-path cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second invalid-path cohort member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new FakeAcceptanceVerifier(
                new AcceptanceVerificationResult(
                    Passed: false,
                    Skipped: false,
                    ExitCode: 1,
                    OutputTail: "invalid result path",
                    Checks: [new AcceptanceCheckResult("combined", false, 1, "invalid result path")],
                    TestResultPaths: ["bad\0path.trx"]),
                exception: null);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var mainBefore = RunGitOutput(repo, "rev-parse", "main").Trim();
            ConductorAcceptanceCohortRunResult? result = null;

            var exception = Record.Exception(() => result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive));

            Assert.Null(exception);
            var receipt = Assert.IsType<AcceptanceCohortReceipt>(result?.Receipt);
            Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, receipt.Outcome);
            Assert.Equal(AcceptanceCohortInfrastructureReasonCodes.ResultPathInvalid, receipt.InfrastructureReasonCode);
            Assert.Empty(receipt.GateTestResultPaths);
            Assert.All(result!.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Equal(mainBefore, RunGitOutput(repo, "rev-parse", "main").Trim());
            AssertNoCohortWorkspaces(repo);

            var persisted = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"))
                .TryReadReceipt(receipt.Identity.Value);
            Assert.Equal(AcceptanceCohortInfrastructureReasonCodes.ResultPathInvalid, persisted?.InfrastructureReasonCode);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionGate_SkippedInvalidResultPath_PersistsStructuredInfrastructureFailure() =>
        AssertEarlyInfrastructureResultWithInvalidPathPersistsStructuredFailure(
            skipped: true,
            exitCode: 0,
            outputTail: "skipped with invalid result path");

    [Fact]
    public void ProductionGate_MissingExitCodeInvalidResultPath_PersistsStructuredInfrastructureFailure() =>
        AssertEarlyInfrastructureResultWithInvalidPathPersistsStructuredFailure(
            skipped: false,
            exitCode: null,
            outputTail: "missing exit code with invalid result path");

    [Fact]
    public void ProductionRed_RunsBothPartitions_AndClassifiesInteraction()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            AddSourceSizeAuthority(
                repo,
                ("src/Mcg.AgentOrchestrator.Infrastructure/First.cs", 1),
                ("tests/Second.cs", 1));
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
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
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
                $"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")};Pooling=False");
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
        var repo = CreateReducedAcceptanceCohortRepository();
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
                FailedVerification(repo, "combined-red-one-member.trx", "combined") with
                { Checks = [new AcceptanceCheckResult("combined", false, 1, "combined failed", FailingTestIdentities: ["Fails"])] },
                FailedVerification(repo, "first-member-red.trx", "first partition") with
                { Checks = [new AcceptanceCheckResult("first partition", false, 1, "first partition failed", FailingTestIdentities: ["Fails"])] },
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
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
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
    public void PersistedInfrastructureFailure_UsesBoundedHoldThenOrdinaryFallback()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
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
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
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
                    GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                        integration.Path,
                        bindings.SelectMany(member => member.LandingPaths).ToArray()));
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
                GateTestResultPaths: [],
                InfrastructureReasonCode: AcceptanceCohortInfrastructureReasonCodes.ExitCodeMissing,
                InfrastructureDetail: "The persisted gate did not report an exit code."));

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, result.Receipt?.Outcome);
            Assert.Equal(AcceptanceCohortInvalidationReason.InfrastructureRetryExhausted, result.Receipt?.Invalidation?.Reason);
            Assert.Empty(result.MemberResults);
            Assert.Contains("fallback=ordinary", result.Detail, StringComparison.Ordinal);
            Assert.Contains(
                ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
                store.ReadSuppressedPairs());
            Assert.Equal(0, verifier.RunCount);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionCohort_RatchetBreach_SkipsGateAndPreservesPair()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            AddSourceSizeAuthority(
                repo,
                ("src/Mcg.AgentOrchestrator.Infrastructure/First.cs", 2),
                ("tests/Second.cs", 4));
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First ratchet member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second ratchet member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "one\ntwo\nthree");
            _ = CreateWorktreeCandidate(
                repo,
                secondGoal.Id,
                "tests/Second.cs",
                "one\ntwo\nthree\nfour\nfive\nsix");
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
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var gateAdmitted = false;

            var result = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive,
                onGateAdmitted: () => gateAdmitted = true);

            Assert.False(gateAdmitted, "The cohort stable-slot lease must not be acquired for a ratchet breach.");
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(AcceptanceCohortGateOutcome.Failed, result.Receipt?.Outcome);
            Assert.Equal(AcceptanceCohortAttributionOutcome.NotApplicable, result.Receipt?.Attribution);
            Assert.Empty(result.MemberResults);
            Assert.Contains("fallback=ordinary", result.Detail, StringComparison.Ordinal);
            var failedChecks = string.Join(Environment.NewLine, result.Receipt!.FailedChecks);
            Assert.Contains("First.cs has 3 lines", failedChecks, StringComparison.Ordinal);
            Assert.Contains("recorded ceiling of 2", failedChecks, StringComparison.Ordinal);
            Assert.Contains("Second.cs has 6 lines", failedChecks, StringComparison.Ordinal);
            Assert.Contains("recorded ceiling of 4", failedChecks, StringComparison.Ordinal);
            var store = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            Assert.Empty(store.ReadSuppressedPairs());
            var eligibility = ConductorAcceptanceCohortSelector.Select(
            [
                new ConductorSpeculativeAcceptanceCandidate(
                    firstGoal.Id,
                    new GateReadyCandidateProjectionResult.Ready(selection.Members[0])),
                new ConductorSpeculativeAcceptanceCandidate(
                    secondGoal.Id,
                    new GateReadyCandidateProjectionResult.Ready(selection.Members[1]))
            ], suppressedPairFingerprints: store.ReadSuppressedPairs());
            Assert.NotNull(eligibility.Selection);

            BatchTickSummary? ordinaryTick = null;
            _ = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                Path.Combine(repo, "stop-does-not-exist"),
                maxIterations: 1,
                onTick: tick => ordinaryTick = tick);

            foreach (var goal in new[] { firstGoal, secondGoal })
            {
                Assert.Contains(ordinaryTick!.ProgressLines!, line =>
                    line.StartsWith($"ACCEPTANCE goal={goal.Id.Value[..8]}", StringComparison.Ordinal) &&
                    line.Contains("result=started", StringComparison.Ordinal));
                Assert.DoesNotContain(ordinaryTick.ProgressLines!, line =>
                    line.Contains($"goal={goal.Id.Value[..8]}", StringComparison.Ordinal) &&
                    line.Contains("result=held", StringComparison.Ordinal) &&
                    line.Contains("cohort", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void RatchetBreachWithoutTrx_IsContentFailure()
    {
        var result = new AcceptanceVerificationResult(
            Passed: false,
            Skipped: false,
            ExitCode: 1,
            OutputTail: "source size breach",
            Checks: [new AcceptanceCheckResult("source size ratchet preflight", false, 1, "breach")]);

        Assert.Equal(
            AcceptanceCohortGateOutcome.Failed,
            ConductorDriver.ClassifyCohortVerification(result));
    }

}
