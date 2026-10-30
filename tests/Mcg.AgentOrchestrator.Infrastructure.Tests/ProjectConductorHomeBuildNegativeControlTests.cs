using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: fake landings and console routing are scoped to each test invocation.
public sealed class ProjectConductorHomeBuildNegativeControlTests : ConductorBatchLoopTests
{
    public ProjectConductorHomeBuildNegativeControlTests(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ExistingHomeLandingAndProjectOnlyClassificationAreUnchanged(bool project)
    {
        using var fixture = new HomeBuildAdoptionFixture();
        fixture.Publish(fixture.Candidate with { Status = "reverted" });
        var kernel = new AgentOrchestratorKernel();
        CreateVerifiedSimpleGoal(kernel, "Landing negative control");
        var landed = false;
        var calls = 0;
        var driver = MakeDriver(
            getFacts: _ => landed
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: goal =>
            {
                landed = true;
                return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ => [project ? "src/project/Feature.cs"
                : "src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.GoalLifecycle.cs"]);
        var text = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(
            homeBuildAdoption: project ? fixture.Detector() : null,
            homeBuildRelaunch: project ? _ => throw new InvalidOperationException("No eligible home adoption") : null,
            selfRelaunch: _ => { calls++; return ConductorSelfRelaunchResult.PreparationFailed("build", "fixture stop"); })
            .Run(kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(fixture.Root, "stop"), maxIterations: 3));
        if (project)
        {
            Assert.Contains("LOOP_RELAUNCH_NOT_REQUIRED", text, StringComparison.Ordinal);
            Assert.Contains("classification=outside-conductor-sources", text, StringComparison.Ordinal);
            Assert.DoesNotContain("LOOP_RELAUNCH_SCHEDULED", text, StringComparison.Ordinal);
            Assert.Equal(0, calls);
        }
        else
        {
            Assert.Contains("LOOP_RELAUNCH_SCHEDULED", text, StringComparison.Ordinal);
            Assert.Contains("coalesced=true", text, StringComparison.Ordinal);
            Assert.True(calls > 0);
        }
    }
}
