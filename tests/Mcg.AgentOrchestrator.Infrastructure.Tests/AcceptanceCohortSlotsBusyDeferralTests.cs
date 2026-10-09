using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceCohortSlotsBusyDeferralTests : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsecutiveBusyGatesLeaveSamePairSelectable(bool grouped)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            var (kernel, goals) = CreateMembers(repo);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var cleanup = CreateIsolatedCleanupContext(repo).Hooks;
            var verifier = new SequenceAcceptanceVerifier([]);
            var acquisitions = new List<string>();
            var delays = 0;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(workspace.OrchestratorDirectory, "parallel-attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                buildStorageRoot: cleanup.BuildStorageRoot,
                acquireCohortStableSlotRound: (identity, label, _) =>
                {
                    acquisitions.Add(identity);
                    Assert.Equal($"cohort-gate:goal-{goals[0].Id.Value[..8]}+{goals[1].Id.Value[..8]}", label);
                    throw new DotnetBuildSlotsBusyException(new("first-available-stable-slot", []));
                },
                cohortStableSlotRoundDelay: (_, _) => { delays++; return Task.CompletedTask; });
            var driver = new ConductorDriver(kernel, workspace, verifier, AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(), coordinator, cleanup);
            var selection = ProjectSelection(driver, goals[0], goals[1]);
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            var launches = new List<ConductorGroupedGateAttempt>();
            var alive = false;
            if (grouped)
                driver.EnableOwnedGroupedGateAttempts(new ConductorGroupedGateAttemptCoordinator(
                    Path.Combine(workspace.OrchestratorDirectory, "grouped-gate-attempts"),
                    launch: attempt =>
                    {
                        launches.Add(attempt);
                        alive = true;
                        // Run the real host in this process, while liveness is driven by the fixture.
                        return new ConductorGroupedGateLaunchResult(Environment.ProcessId, null, null);
                    }, isProcessAlive: _ => alive));

            var admitted = 0;
            for (var evaluation = 0; evaluation < 2; evaluation++)
            {
                var result = driver.RunAcceptanceCohort(selection, goals, ConductorAutonomyPolicy.Permissive,
                    onGateAdmitted: () => admitted++, runGateInBackground: grouped);
                Assert.Null(result.Receipt);
                if (grouped)
                {
                    Assert.Contains("outcome=inflight", result.Detail);
                    var attempt = launches[evaluation];
                    Assert.Equal(0, ConductorGroupedGateAttemptHost.Run(attempt.MetadataPath,
                        driver.RunGroupedGateAttemptBody, (_, _) => new NoConsoleRedirection()));
                    Assert.True(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(attempt));
                    alive = false;
                }
                else
                {
                    Assert.Contains("outcome=deferred-slots-busy", result.Detail);
                    Assert.All(result.MemberResults.Values, member => Assert.True(member.IsHeld));
                }
                Assert.Null(store.TryReadReceipt(acquisitions[0]));
                Assert.Empty(store.ReadSuppressedPairs());
                AssertSelectable(driver, goals, selection, store);
            }
            Assert.Equal(6, acquisitions.Count);
            Assert.Single(acquisitions.Distinct(StringComparer.Ordinal));
            Assert.Equal(4, delays);
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(grouped ? 2 : 0, admitted);
            if (grouped)
            {
                // Revisit both completed attempts, including the already-reconciled first one.
                var replay = driver.RunAcceptanceCohort(selection, goals, ConductorAutonomyPolicy.Permissive,
                    runGateInBackground: true);
                Assert.Null(replay.Receipt);
                Assert.Equal(3, launches.Count);
                Assert.Null(store.TryReadReceipt(acquisitions[0]));
                Assert.Empty(store.ReadSuppressedPairs());
                AssertSelectable(driver, goals, selection, store);
            }
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void GateStartsOnSecondRoundAndReleasesItsLease()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            var (kernel, goals) = CreateMembers(repo);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var cleanup = CreateIsolatedCleanupContext(repo).Hooks;
            // An indeterminate result prevents landing and attribution after the gate is invoked.
            var verifier = new SequenceAcceptanceVerifier([new(false, false, null, "no exit evidence")]);
            var acquisitions = 0;
            var delays = 0;
            DotnetBuildEnvironmentLease? acquired = null;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(workspace.OrchestratorDirectory, "parallel-attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                acquireCohortStableSlotRound: (_, label, _) =>
                {
                    if (++acquisitions == 1)
                        throw new DotnetBuildSlotsBusyException(new("first-available-stable-slot", []));
                    return acquired = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                        TimeSpan.Zero, storageRoot: cleanup.BuildStorageRoot, holderLabel: label);
                }, cohortStableSlotRoundDelay: (_, _) => { delays++; return Task.CompletedTask; });
            var driver = new ConductorDriver(kernel, workspace, verifier, AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(), coordinator, cleanup);
            var selection = ProjectSelection(driver, goals[0], goals[1]);
            var admitted = 0;

            var result = driver.RunAcceptanceCohort(selection, goals, ConductorAutonomyPolicy.Permissive,
                onGateAdmitted: () => admitted++);

            Assert.NotNull(result.Receipt);
            Assert.Equal(2, acquisitions);
            Assert.Equal(1, delays);
            Assert.Equal(1, admitted);
            Assert.Equal(1, verifier.RunCount);
            Assert.NotNull(acquired);
            Assert.False(acquired.IsExecutionLockHeld);
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData("wrong-identity", "completed", "deferred-slots-busy", "", "cohort", false)]
    [InlineData("identity", "failed", "deferred-slots-busy", "", "cohort", false)]
    [InlineData("identity", "completed", "unknown", "", "cohort", false)]
    [InlineData("identity", "completed", "deferred-slots-busy", "receipt", "cohort", false)]
    [InlineData("identity", "completed", "deferred-slots-busy", "", "train", false)]
    [InlineData("identity", "completed", "deferred-slots-busy", "", "cohort", true)]
    public void RecoveryRequiresMatchingCompletedDeferral(
        string identity, string status, string verdict, string receipt, string kind, bool expected)
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "result.json");
            var attempt = new ConductorGroupedGateAttempt("attempt", kind, [], "main", "tree", "manifest",
                "identity", DateTimeOffset.UnixEpoch, 0, 1, path + ".attempt", path,
                path + ".exit", path + ".out", path + ".err", root, "{}");
            Assert.False(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(attempt));
            File.WriteAllText(path, "{}");
            Assert.False(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(attempt));
            File.WriteAllText(path, "{");
            Assert.False(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(attempt));
            File.WriteAllText(path, JsonSerializer.Serialize(new
                { IdentityValue = identity, Status = status, Verdict = verdict, ReceiptId = receipt }));
            Assert.Equal(expected, ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(attempt));
        }
        finally { DeleteDirectory(root); }
    }

    private static (AgentOrchestratorKernel Kernel, Goal[] Goals) CreateMembers(string repo)
    {
        AddAcceptanceManifest(repo);
        var kernel = new AgentOrchestratorKernel();
        var goals = new[] { CreateCompletedGoal(kernel, "First member", repo), CreateCompletedGoal(kernel, "Second member", repo) };
        var paths = new[] { "tests/Mcg.AgentOrchestrator.Core.Tests/First.cs", "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Second.cs" };
        for (var index = 0; index < goals.Length; index++)
        {
            var revision = CreateWorktreeCandidate(repo, goals[index].Id, paths[index], $"member-{index}");
            kernel.RecordGoalRefinement(goals[index].Id, new RefinedSpec(goals[index].Objective,
                ["The acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                { AcceptanceGateOwnedAcceptanceCriteria = ["The acceptance gate passes"] });
            kernel.MapCriterionEvidenceOwner(goals[index].Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: revision);
        }
        return (kernel, goals);
    }

    private static void AssertSelectable(ConductorDriver driver, Goal[] goals,
        ConductorAcceptanceCohortSelection original, CohortAcceptanceStore store)
    {
        var candidates = goals.Select(goal => new ConductorSpeculativeAcceptanceCandidate(goal.Id,
            driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive))).ToArray();
        var selected = ConductorAcceptanceCohortSelector.Select(candidates,
            suppressedPairFingerprints: store.ReadSuppressedPairs()).Selection;
        Assert.NotNull(selected);
        Assert.Equal(original.Members, selected.Members);
    }

    private sealed class NoConsoleRedirection : IDisposable { public void Dispose() { } }
}
