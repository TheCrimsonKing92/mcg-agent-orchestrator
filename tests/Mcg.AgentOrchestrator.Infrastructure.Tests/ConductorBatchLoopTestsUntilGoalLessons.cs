using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsUntilGoalLessons : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsUntilGoalLessons(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void SweepFinalizesLanding_SameTickRetiresConditionalLesson()
    {
        using var fixture = new OperatorLessonHarness();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Fix landing");
        OperatorLessonUntilGoalRetirementTests.Add(fixture, "conditional", goal.Id.Value);
        var swept = false;
        var phases = new List<string>();
        var coordinator = OperatorLessonUntilGoalRecordTests.WithProductionLandedCheck(fixture);
        var loop = new ConductorBatchLoop(operatorIntents: coordinator, utcNow: () => fixture.Now,
            janitorialPhaseProbe: phases.Add,
            sweep: current =>
            {
                Assert.Null(Assert.Single(fixture.LessonStore.List()).RetiredAt);
                current.ReplaceWithSnapshot(OperatorLessonUntilGoalRecordTests.Complete(current, goal.Id)
                    .ExportSnapshot());
                GoalOperationJournal.Completed(fixture.Root, goal, "conductor:land", "Fix landed");
                swept = true;
            });

        loop.Run(kernel, MakeDriver(), ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 1);

        Assert.True(swept);
        Assert.Empty(fixture.LessonStore.List());
        Assert.Equal("until-goal-landed goal=" + goal.Id.Value,
            Assert.Single(fixture.LessonStore.List(includeRetired: true)).RetireReason);
        Assert.Single(phases, phase => phase == "retire-until-goal-lessons");
    }
}
