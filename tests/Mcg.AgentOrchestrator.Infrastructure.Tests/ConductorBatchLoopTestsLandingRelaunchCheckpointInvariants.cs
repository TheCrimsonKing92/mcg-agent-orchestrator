using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsLandingRelaunchCheckpointInvariants : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsLandingRelaunchCheckpointInvariants(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void MissingLandingDispositionStillThrows() => AssertHeldEndOfTickCheckpoint(missingLandingOutcome: true);

    [Fact]
    public void HeldLandingCheckpointStillRefusesRelaunch() => AssertHeldEndOfTickCheckpoint(missingLandingOutcome: false);

    private static void AssertHeldEndOfTickCheckpoint(bool missingLandingOutcome)
    {
        var kernel = new AgentOrchestratorKernel();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Existing worker");
        var goal = CreateVerifiedSimpleGoal(kernel, "Update conductor loop");
        var attemptRoot = CreateTempDirectory("mcg-held-landing-checkpoint");
        try
        {
            const string landedHead = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string mainHead = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var landed = false;
            var relaunchCalls = 0;
            var landingCheckpointCalls = 0;
            var driver = MakeDriver(
                getFacts: _ => landed
                    ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) =>
                    new AcceptanceVerificationSummary(true, [], BranchHeadSha: landedHead, MainHeadSha: mainHead),
                land: candidate =>
                {
                    landed = true;
                    kernel.CompleteGoal(candidate.Id, "Landing, recording, and cleanup completed.");
                    return new LandingResult(candidate.Id.Value, candidate.Id.Value[..8],
                        new LandingDecision.Promote(), "integration", true, "Landed", landedHead);
                },
                getLandingFileScopes: _ =>
                    ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"],
                parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    attemptRoot, runInline: true),
                resolveAcceptanceHeads: _ => (landedHead, mainHead));

            var exception = Record.Exception(() => new ConductorBatchLoop(
                selfRelaunch: _ =>
                {
                    relaunchCalls++;
                    var handoff = new ConductorLoopHandoffResult(true, null, null, null, "fixture handoff");
                    return new ConductorSelfRelaunchResult(true, null, handoff.Reason, handoff);
                }, selfRelaunchEnabled: true).Run(
                    kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
                    maxIterations: 3,
                    checkpointGoalTick: (_, requested) =>
                    {
                        var outcomes = new List<GoalSnapshotCheckpointResult>();
                        foreach (var id in requested)
                        {
                            if (id == goal.Id && landed)
                            {
                                landingCheckpointCalls++;
                                if (landingCheckpointCalls == 2 && missingLandingOutcome) continue;
                            }
                            outcomes.Add(new GoalSnapshotCheckpointResult(id.Value,
                                id == goal.Id && landed
                                    ? GoalSnapshotCheckpointDisposition.Held
                                    : GoalSnapshotCheckpointDisposition.Durable,
                                null, "state", "C:/fixture/state.db", "fixture"));
                        }
                        return outcomes;
                    }));

            Assert.True(landed);
            Assert.Equal(2, landingCheckpointCalls);
            Assert.Equal(0, relaunchCalls);
            Assert.NotNull(exception);
            Assert.Contains(missingLandingOutcome
                    ? "Checkpoint persistence returned no disposition for goal"
                    : "Landing state checkpoint was held; refusing self-relaunch.",
                exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }
}
