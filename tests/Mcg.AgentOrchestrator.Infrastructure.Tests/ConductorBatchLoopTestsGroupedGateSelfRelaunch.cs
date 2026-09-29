using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsGroupedGateSelfRelaunch : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsGroupedGateSelfRelaunch(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void RunningGroupedGateDoesNotEnterSelfRelaunchDispatchDrain()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grouped-gate-handoff-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var kernel = new AgentOrchestratorKernel();
            _ = CreateVerifiedSimpleGoal(kernel, "Update conductor loop");
            var first = new GoalId("11111111111111111111111111111111");
            var second = new GoalId("22222222222222222222222222222222");
            var main = new string('c', 40);
            var selection = new ConductorAcceptanceCohortSelection(
            [
                new GateReadyCandidateProjection(first, GoalLifecycleState.Verified,
                    GateReadyVerificationState.Satisfied, ChangeRiskTier.DocsOnly,
                    ConductorTransitionDecision.Auto, ["src/First.cs"], ["production:first"],
                    new GateReadyMergeEvidence(new string('a', 40), main,
                        GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected)),
                new GateReadyCandidateProjection(second, GoalLifecycleState.Verified,
                    GateReadyVerificationState.Satisfied, ChangeRiskTier.DocsOnly,
                    ConductorTransitionDecision.Auto, ["tests/Second.cs"], ["test:second"],
                    new GateReadyMergeEvidence(new string('b', 40), main,
                        GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected))
            ], []);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var landed = false;
            var driver = MakeDriver(
                getFacts: _ => landed
                    ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true,
                        IsRecorded: true, IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                land: goal =>
                {
                    landed = true;
                    return new LandingResult(goal.Id.Value, goal.Id.Value[..8],
                        new LandingDecision.Promote(), "integration", true, "Landed");
                },
                getLandingFileScopes: _ =>
                    ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"]);
            Assert.True(driver.TryRegisterCohortGateRunForTests(selection, completion));
            var attempts = new ConductorGroupedGateAttemptCoordinator(
                Path.Combine(root, "grouped-gate-attempts"), generationId: 111,
                isProcessAlive: pid => pid == 222);
            var record = attempts.Create("cohort", selection.Members, main,
                new string('d', 40), "manifest", "cohort-test", root,
                ConductorAutonomyPolicy.Conservative) with
            { OwnerProcessId = 222, OwnerProcessStartedAt = DateTimeOffset.UnixEpoch,
                OwnerExecutablePath = "C:\\dotnet.exe" };
            ConductorGroupedGateAttemptCoordinator.Save(record);
            var handoffs = 0;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                new ConductorBatchLoop(
                    selfRelaunch: _ =>
                    {
                        handoffs++;
                        return new ConductorSelfRelaunchResult(true, null, null,
                            ConductorLoopHandoffResult.StartedProcess(333, "out", "err"));
                    }, selfRelaunchEnabled: true).Run(
                        kernel, driver, ConductorAutonomyPolicy.Conservative,
                        NoStopPath(), maxIterations: 5));
            Assert.Equal(1, handoffs);
            Assert.Contains("LOOP_RELAUNCH_REBUILD", output, StringComparison.Ordinal);
            Assert.Contains("active=0 admitting=false", output, StringComparison.Ordinal);
            Assert.False(completion.Task.IsCompleted);
            Assert.True(File.Exists(record.MetadataPath));
        }
        finally { TryDeleteDirectory(root); }
    }
}
