using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all decisions and recording callbacks are local to each test.
public sealed class FailedGoalRetryApplicationExecutorTests
{
    [Xunit.Theory]
    [Xunit.InlineData(FailedGoalRecoveryAction.CriterionRetry, "warning")]
    [Xunit.InlineData(FailedGoalRecoveryAction.FindingRetry, "warning")]
    [Xunit.InlineData(FailedGoalRecoveryAction.CriterionRetry, null)]
    [Xunit.InlineData(FailedGoalRecoveryAction.CriterionRetry, "")]
    [Xunit.InlineData(FailedGoalRecoveryAction.CriterionRetry, " \t")]
    [Xunit.InlineData(FailedGoalRecoveryAction.FindingRetry, " \t")]
    public void Apply_PreservesEffectOrderAndArguments(FailedGoalRecoveryAction action, string? warning)
    {
        var decision = Decision(action, warning: warning);
        var goalId = decision.Identity.GoalId;
        var taskId = decision.Identity.TaskId!;
        var effects = new List<string>();
        IReadOnlyList<string> feedback = new[] { "command evidence", "failure evidence" };

        var result = FailedGoalRetryApplicationExecutor.Apply(goalId, taskId, decision,
            () => { effects.Add("build-feedback"); return feedback; },
            (goal, task, message) =>
            {
                effects.Add("note");
                Assert.Equal(goalId, goal);
                Assert.Equal(taskId, task);
                Assert.Equal(warning, message);
            },
            (goal, task, messages) =>
            {
                effects.Add("feedback");
                Assert.Equal(goalId, goal);
                Assert.Equal(taskId, task);
                Assert.Same(feedback, messages);
                return 17;
            },
            (goal, task, reason, roundKind, cause) =>
            {
                effects.Add("retry");
                Assert.Equal(goalId, goal);
                Assert.Equal(taskId, task);
                Assert.Equal(decision.Reason, reason);
                Assert.Equal(decision.RoundKind, roundKind);
                Assert.Equal(decision.RetryCause, cause);
                return new TaskSpec(task, "Retry target", AgentRole.Developer);
            });

        var expected = new List<string>();
        if (!string.IsNullOrWhiteSpace(warning))
            expected.Add("note");
        if (action == FailedGoalRecoveryAction.CriterionRetry)
            expected.AddRange(["build-feedback", "feedback"]);
        expected.Add("retry");
        Assert.Equal(expected, effects);
        Assert.Null(result);
    }

    [Xunit.Theory]
    [Xunit.InlineData("ACTIONABLE_CANDIDATE_RED: receipt", "Task has a running process.", true)]
    [Xunit.InlineData("ACTIONABLE_CANDIDATE_RED: receipt", "Task has a RUNNING PROCESS.", true)]
    [Xunit.InlineData("OTHER: receipt", "Task has a running process.", false)]
    [Xunit.InlineData("actionable_candidate_red: receipt", "Task has a running process.", false)]
    [Xunit.InlineData("prefix ACTIONABLE_CANDIDATE_RED: receipt", "Task has a running process.", false)]
    [Xunit.InlineData("ACTIONABLE_CANDIDATE_RED: receipt", "Task is unavailable.", false)]
    public void Apply_OnlyMatchingRetryConflictReturnsMessage(string reason, string message, bool matches)
    {
        var decision = Decision(FailedGoalRecoveryAction.FindingRetry, reason);
        var exception = new InvalidOperationException(message);
        var effects = new List<string>();

        string? Apply() => FailedGoalRetryApplicationExecutor.Apply(
            decision.Identity.GoalId, decision.Identity.TaskId!, decision,
            () => throw new Exception("Feedback must not be built."),
            (_, _, _) => effects.Add("note"),
            (_, _, _) => throw new Exception("Feedback must not be recorded."),
            (_, _, _, _, _) => { effects.Add("retry"); throw exception; });

        if (matches)
            Assert.Equal(message, Apply());
        else
            Assert.Same(exception, Assert.Throws<InvalidOperationException>(() => Apply()));
        Assert.Equal(new[] { "note", "retry" }, effects);
    }

    [Xunit.Theory]
    [Xunit.InlineData("note")]
    [Xunit.InlineData("build-feedback")]
    [Xunit.InlineData("feedback")]
    public void Apply_EarlierEffectExceptionPropagatesWithoutRetry(string failingEffect)
    {
        var decision = Decision(FailedGoalRecoveryAction.CriterionRetry, "ACTIONABLE_CANDIDATE_RED: receipt");
        var exception = new InvalidOperationException("Task has a running process.");
        var effects = new List<string>();

        void Record(string effect)
        {
            effects.Add(effect);
            if (effect == failingEffect)
                throw exception;
        }

        var actual = Assert.Throws<InvalidOperationException>(() => FailedGoalRetryApplicationExecutor.Apply(
            decision.Identity.GoalId, decision.Identity.TaskId!, decision,
            () => { Record("build-feedback"); return new[] { "feedback" }; },
            (_, _, _) => Record("note"),
            (_, _, _) => { Record("feedback"); return 1; },
            (_, task, _, _, _) => { Record("retry"); return new TaskSpec(task, "Retry", AgentRole.Developer); }));

        Assert.Same(exception, actual);
        var sequence = new[] { "note", "build-feedback", "feedback" };
        Assert.Equal(sequence.Take(Array.IndexOf(sequence, failingEffect) + 1), effects);
    }

    [Xunit.Fact]
    public void Apply_NullCauseThrowsAfterNoteAndFeedbackBeforeRetry()
    {
        var decision = Decision(FailedGoalRecoveryAction.CriterionRetry, "ACTIONABLE_CANDIDATE_RED: receipt")
            with { RetryCause = null };
        var effects = new List<string>();

        var exception = Assert.Throws<InvalidOperationException>(() => FailedGoalRetryApplicationExecutor.Apply(
            decision.Identity.GoalId, decision.Identity.TaskId!, decision,
            () => { effects.Add("build-feedback"); return new[] { "feedback" }; },
            (_, _, _) => effects.Add("note"),
            (_, _, _) => { effects.Add("feedback"); return 1; },
            (_, task, _, _, _) => { effects.Add("retry"); return new TaskSpec(task, "Retry", AgentRole.Developer); }));

        Assert.Equal("Recovery action CriterionRetry must carry a typed retry cause.", exception.Message);
        Assert.Equal(new[] { "note", "build-feedback", "feedback" }, effects);
    }

    [Xunit.Fact]
    public void Apply_NonInvalidOperationRetryExceptionPropagates()
    {
        var decision = Decision(FailedGoalRecoveryAction.FindingRetry, "ACTIONABLE_CANDIDATE_RED: receipt");
        var exception = new ArgumentException("Task has a running process.");

        Assert.Same(exception, Assert.Throws<ArgumentException>(() => FailedGoalRetryApplicationExecutor.Apply(
            decision.Identity.GoalId, decision.Identity.TaskId!, decision,
            () => throw new Exception("Feedback must not be built."), (_, _, _) => { },
            (_, _, _) => throw new Exception("Feedback must not be recorded."),
            (_, _, _, _, _) => throw exception)));
    }

    private static FailedGoalRecoveryDecision Decision(
        FailedGoalRecoveryAction action, string reason = "retry reason", string? warning = "warning") => new(
        action, new FailedGoalRecoveryIdentity(GoalId.New(), TaskId.New(), "attempt", "context"),
        6, "recording-callback-fixture", reason, RetryCause.NewSourceFinding, RetryRoundKind.Mechanical,
        WarningMessage: warning);
}
