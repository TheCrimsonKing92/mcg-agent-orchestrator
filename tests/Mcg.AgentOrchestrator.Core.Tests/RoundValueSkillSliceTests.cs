using Mcg.AgentOrchestrator.Core;

// Parallel-safe: snapshot-only state and fixed event timestamps.
public sealed class RoundValueSkillSliceTests
{
    [Fact]
    public void AttributesOnlyObservedSkillsAndKeepsUnrecordedRoundsSeparate()
    {
        var kernel = Create();
        var slice = RoundValueSkillSlice.Build(kernel.Goals, RoundValueFixture.Since, RoundValueFixture.Until);
        Assert.Equal(new RoundValueSkillRow("verification-before-completion", 2, 1, 1, 1, 1, 0), slice.Rows[0]);
        Assert.Equal(new RoundValueSkillRow("unrecorded", 0, 0, 0, 0, 0, 1), slice.Rows[1]);
        Assert.Equal(0, slice.ReadUnavailable);
    }

    [Fact]
    public void JoinsNotesToEachAttemptAndCountsUnavailableWithoutInventingARead()
    {
        var source = RoundValueFixture.Create().ExportSnapshot();
        var goal = source.Goals[0];
        var notes = new[]
        {
            new ProgressEventSnapshot(goal.Id, "a-dev", ProgressKind.TaskNote,
                "SKILLS goal=aaaaaaaa task=a-dev selected=research-evidence read=unavailable claimed=none",
                RoundValueFixture.At("2026-09-24T02:31:00Z")),
            new ProgressEventSnapshot(goal.Id, "a-dev", ProgressKind.TaskNote,
                "SKILLS goal=aaaaaaaa task=a-dev selected=none read=skill-authoring claimed=skill-authoring",
                RoundValueFixture.At("2026-09-24T04:46:00Z"))
        };
        var kernel = AgentOrchestratorKernel.FromSnapshot(source with { Goals = [goal with { Timeline = [.. goal.Timeline, .. notes] }] });
        var slice = RoundValueSkillSlice.Build(kernel.Goals, RoundValueFixture.Since, RoundValueFixture.Until);
        Assert.Equal(new RoundValueSkillRow("research-evidence", 1, 0, 0, 1, 0, 0), slice.Rows.Single(r => r.Skill == "research-evidence"));
        Assert.Equal(new RoundValueSkillRow("skill-authoring", 0, 1, 1, 1, 0, 0), slice.Rows.Single(r => r.Skill == "skill-authoring"));
        Assert.Equal(1, slice.ReadUnavailable);
    }

    private static AgentOrchestratorKernel Create()
    {
        const string goalId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var at = RoundValueFixture.At("2026-09-24T01:00:00Z");
        TaskSnapshot Task(string id, AgentRole role) => RoundValueFixture.Task(id, role, WorkTaskStatus.Completed,
            RoundValueFixture.Dispatch("2026-09-24T01:00:00Z"));
        ProgressEventSnapshot Event(string task, ProgressKind kind, string message) => new(goalId, task, kind, message, at.AddMinutes(1));
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([
            new GoalSnapshot(goalId, "Skills", GoalStatus.Completed,
            [Task("dev", AgentRole.Developer), Task("review", AgentRole.Reviewer), Task("orphan", AgentRole.Tester)],
            [Event("dev", ProgressKind.TaskCompleted, "done"), Event("review", ProgressKind.TaskCompleted, "done"),
             Event("dev", ProgressKind.TaskNote, "SKILLS goal=aaaaaaaa task=dev selected=verification-before-completion read=verification-before-completion claimed=verification-before-completion"),
             Event("review", ProgressKind.TaskNote, "SKILLS goal=aaaaaaaa task=review selected=verification-before-completion read=none claimed=none")])], []));
    }
}
