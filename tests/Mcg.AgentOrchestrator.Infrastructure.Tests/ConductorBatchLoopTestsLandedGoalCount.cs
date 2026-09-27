using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsLandedGoalCount : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsLandedGoalCount(ITestOutputHelper output) : base(output)
    {
    }

    [Xunit.Fact]
    public void LandingTriggeredDelegatedRelaunch_WritesPositiveLandedCount()
    {
        var kernel = new AgentOrchestratorKernel();
        CreateVerifiedSimpleGoal(kernel, "Update conductor loop");
        var landed = false;
        var driver = MakeDriver(
            getFacts: _ => landed
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: candidate =>
            {
                landed = true;
                return new LandingResult(candidate.Id.Value, candidate.Id.Value[..8],
                    new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ =>
                ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"]);

        var summary = new ConductorBatchLoop(
            selfRelaunch: ConductorSelfRelaunch.CreateSupervisorDelegated(),
            selfRelaunchEnabled: true).Run(kernel, driver,
                ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 5);

        Assert.Equal("self-relaunch-handoff", summary.StopReason);
        Assert.Equal(0, summary.Done);
        Assert.True(summary.LandedGoals >= 1);

        var path = Path.Combine(Path.GetTempPath(), $"mcg-landed-exit-{Guid.NewGuid():N}.json");
        try
        {
            ConductorContinuityExitArtifact.Write(path,
                ConductorContinuityExitArtifactFactory.FromLoopSummary(summary,
                    delegateSelfRelaunchToSupervisor: true));
            var artifact = ConductorContinuityExitArtifact.TryRead(path);
            Assert.NotNull(artifact);
            Assert.True(artifact.RestartRequested);
            Assert.True(artifact.LandedGoals >= 1);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
