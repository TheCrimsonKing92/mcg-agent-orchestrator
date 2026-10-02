using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static DeveloperResumedDeferredNoChangeQualifierTests;

public sealed class DeveloperResumedEmptyHistoryEntryQualifierTests
{
    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" ")]
    public void EmptyBaseEntryBetweenAnswerAndCurrentDispatchDoesNotHideCommit(string? emptyBase)
    {
        var start = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        var clock = new TestClock { UtcNow = start };
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Review the approved implementation.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Resumed deferred no-change with empty history entry", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", start, BaseCommit: new string('a', 40)));
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, Candidate);
        kernel.RecordTaskProcessRefreshed(goal.Id, task.Id,
            new TaskProcessRecord(123, "codex exec", "C:\\repo", "out", "err", "exit", start, start, 0), null);
        clock.UtcNow = start.AddMinutes(1);
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Confirm the implementation.",
            HumanWaitKind.SpecClarification);
        kernel.SubmitHumanInput(request.Id, "The candidate meets the boundary.");
        clock.UtcNow = start.AddMinutes(2).AddSeconds(-5);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", clock.UtcNow, BaseCommit: emptyBase));
        clock.UtcNow = start.AddMinutes(2);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", clock.UtcNow, BaseCommit: Candidate),
            allowPendingRecordedDispatchRefresh: true);
        Xunit.Assert.Null(task.LatestRetryAt);
        Xunit.Assert.Equal(0, task.CriterionRetryCount);
        Xunit.Assert.Empty(task.CriterionRetryFeedback);
        var history = task.DispatchHistory.ToArray();
        Xunit.Assert.Equal(3, history.Length);
        Xunit.Assert.Equal(Candidate, history[0].ResultCommit);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(history[1].BaseCommit));
        Xunit.Assert.Null(history[1].ResultCommit);
        var answer = Xunit.Assert.Single(goal.Timeline.Where(item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.HumanInputReceived));
        Xunit.Assert.True(answer.OccurredAt > history[0].DispatchedAt &&
            answer.OccurredAt < history[1].DispatchedAt && history[1].DispatchedAt < history[2].DispatchedAt);

        Xunit.Assert.True(Qualify(goal, task, out var outcome));
        Xunit.Assert.Equal(Candidate, outcome.CandidateSha);
        Xunit.Assert.Equal(["ClassA"], outcome.TestClasses);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
