using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorSelfRelaunchDefaultEnablementTests : ConductorBatchLoopTests
{
    public ConductorSelfRelaunchDefaultEnablementTests(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory]
    [Xunit.InlineData(null, true)]
    [Xunit.InlineData("false", false)]
    public void UnsetSettingSchedulesConductorLanding(string? setting, bool expected)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update conductor loop");
        var landed = false;
        var relaunchCalls = 0;
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

        var output = AsyncLocalConsoleRouter.Capture(() =>
            new ConductorBatchLoop(
                selfRelaunch: _ =>
                {
                    relaunchCalls++;
                    return new ConductorSelfRelaunchResult(false, "build", "test stop");
                },
                selfRelaunchEnabled: ConductorBatchLoop.ResolveSelfRelaunchEnabled(setting)).Run(
                    kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 3));

        Assert.Equal(expected, relaunchCalls > 0);
        Assert.Equal(expected, output.Contains("LOOP_RELAUNCH_SCHEDULED", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void MalformedSettingWarnsAndKeepsDefaultEnabled()
    {
        var warnings = new List<string>();
        Assert.True(ConductorBatchLoop.ResolveSelfRelaunchEnabled("garbage", warnings.Add));
        Assert.Contains("SELF_RELAUNCH_SETTING_MALFORMED", Assert.Single(warnings), StringComparison.Ordinal);
    }
}
