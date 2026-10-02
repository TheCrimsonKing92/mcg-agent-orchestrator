using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its SQLite stores and journal directory.
public sealed class OperatorLessonUntilGoalRetirementTests
{
    [Fact]
    public void LandedCondition_RetiresOnceAndSelectorKeepsOtherLessons()
    {
        using var fixture = new OperatorLessonHarness();
        var kernel = new AgentOrchestratorKernel();
        var landed = kernel.CreateGoal("Fix landed");
        var pending = kernel.CreateGoal("Fix pending");
        Add(fixture, "conditional", landed.Id.Value);
        Add(fixture, "unconditional", null);
        Add(fixture, "pending", pending.Id.Value);
        Assert.Equal(3, new ConductorLessonSelector(fixture.Workspace.OperatorLessonsStorePath)
            .Select(["steward"]).Lessons.Count);
        kernel = OperatorLessonUntilGoalRecordTests.Complete(kernel, landed.Id);
        GoalOperationJournal.Completed(fixture.Root, landed, "conductor:land", "Fix landed");
        var coordinator = OperatorLessonUntilGoalRecordTests.WithProductionLandedCheck(fixture);

        Assert.Single(coordinator.RetireLandedUntilGoalLessons(kernel));

        var retired = Assert.Single(fixture.LessonStore.List(includeRetired: true), lesson =>
            lesson.Id == "conditional");
        Assert.Equal("until-goal-landed goal=" + landed.Id.Value, retired.RetireReason);
        Assert.Equal("conductor", retired.RetiredBy);
        Assert.Equal(fixture.Now, retired.RetiredAt);
        Assert.True(fixture.LessonStore.HasRetirementSource("until-goal-landed:conditional"));
        var beforeReplay = JsonSerializer.Serialize(fixture.LessonStore.List(includeRetired: true));
        fixture.Now = fixture.Now.AddDays(1);
        Assert.Empty(coordinator.RetireLandedUntilGoalLessons(kernel));
        Assert.Equal(beforeReplay, JsonSerializer.Serialize(fixture.LessonStore.List(includeRetired: true)));
        Assert.Equal(new[] { "pending", "unconditional" }, fixture.LessonStore.List()
            .Select(lesson => lesson.Id).OrderBy(id => id).ToArray());
        var selected = new ConductorLessonSelector(fixture.Workspace.OperatorLessonsStorePath)
            .Select(["steward"]).Lessons;
        Assert.DoesNotContain(selected, lesson => lesson.Id == "conditional");
        Assert.Contains(selected, lesson => lesson.Id == "unconditional");
        Assert.Contains(selected, lesson => lesson.Id == "pending");
    }

    [Fact]
    public void CompletedWithoutLanding_LeavesLessonActive()
    {
        using var fixture = new OperatorLessonHarness();
        var kernel = new AgentOrchestratorKernel();
        var retired = kernel.CreateGoal("Retired fix");
        kernel = OperatorLessonUntilGoalRecordTests.Complete(kernel, retired.Id);
        GoalOperationJournal.RecordTerminalDisposition(fixture.Root, retired,
            new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "Retired without landing"));
        Add(fixture, "conditional", retired.Id.Value);

        Assert.Empty(OperatorLessonUntilGoalRecordTests.WithProductionLandedCheck(fixture)
            .RetireLandedUntilGoalLessons(kernel));

        Assert.Null(Assert.Single(fixture.LessonStore.List()).RetiredAt);
    }

    [Fact]
    public void TombstonedLandingIntent_LeavesLessonActive()
    {
        using var fixture = new OperatorLessonHarness();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Unpublished fix");
        kernel = OperatorLessonUntilGoalRecordTests.Complete(kernel, goal.Id);
        GoalOperationJournal.RecordLandingIntent(fixture.Root, goal, "goal/fix", "main",
            "abc123", "test", recordedAt: fixture.Now);
        GoalOperationJournal.TombstoneLandingIntent(fixture.Root, goal, "Ref update failed");
        Add(fixture, "conditional", goal.Id.Value);

        Assert.Empty(OperatorLessonUntilGoalRecordTests.WithProductionLandedCheck(fixture)
            .RetireLandedUntilGoalLessons(kernel));

        Assert.Null(Assert.Single(fixture.LessonStore.List()).RetiredAt);
    }

    [Fact]
    public void UnknownGoal_LeavesLessonActive()
    {
        using var fixture = new OperatorLessonHarness();
        Add(fixture, "conditional", Guid.NewGuid().ToString("N"));

        Assert.Empty(fixture.Coordinator.RetireLandedUntilGoalLessons(new AgentOrchestratorKernel()));

        Assert.Null(Assert.Single(fixture.LessonStore.List()).RetiredAt);
    }

    internal static void Add(OperatorLessonHarness fixture, string id, string? untilGoal) =>
        Assert.True(fixture.LessonStore.TryAppendLesson(new OperatorLesson(id, "When fixture fails",
            "Inspect proof", ["steward"], [], "tester", OperatorActorKind.Human, "cli",
            fixture.Now, null, null, null, null, null, untilGoal), "record-" + id));
}
