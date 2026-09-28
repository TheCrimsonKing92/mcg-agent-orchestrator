using Mcg.AgentOrchestrator.Core;

public sealed class WorkerRoundLedgerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-24T00:00:00Z");

    [Fact]
    public void ProjectsEveryDispatchWithItsFirstEndingEventAndReportedUsage()
    {
        var first = Dispatch(1, "anthropic", "claude", Usage(100, 10, 20));
        var second = Dispatch(2, "openai", "gpt", Usage());
        var third = Dispatch(3, "anthropic", "claude", Usage(300, 30, 40));
        var goal = GoalWith(WorkTaskStatus.Completed, [third, first, second],
            [Event(1.5, ProgressKind.TaskFailed), Event(1.7, ProgressKind.TaskRetried),
             Event(2.5, ProgressKind.TaskRetried), Event(3.5, ProgressKind.TaskCompleted)]);

        var rounds = WorkerRoundLedger.FromGoals([goal]);

        Assert.Equal(3, rounds.Count);
        Assert.Equal([1, 2, 3], rounds.Select(round => round.RoundIndex));
        Assert.Equal([Start.AddHours(1), Start.AddHours(2), Start.AddHours(3)],
            rounds.Select(round => round.DispatchedAt));
        Assert.Equal([WorkerRoundStopCause.Failed, WorkerRoundStopCause.Superseded,
            WorkerRoundStopCause.Completed], rounds.Select(round => round.StopCause));
        Assert.Equal([Start.AddHours(1.5), Start.AddHours(2.5), Start.AddHours(3.5)],
            rounds.Select(round => round.EndedAt));
        Assert.Equal(["anthropic", "openai", "anthropic"], rounds.Select(round => round.ProviderName));
        Assert.Equal(["claude", "gpt", "claude"], rounds.Select(round => round.ModelName));
        Assert.Equal(AgentRole.Developer, rounds[0].Role);
        Assert.Equal([100L, null, 300L], rounds.Select(round => round.InputTokens));
        Assert.Equal([10L, null, 30L], rounds.Select(round => round.CachedInputTokens));
        Assert.Equal([20L, null, 40L], rounds.Select(round => round.OutputTokens));
    }

    [Fact]
    public void DistinguishesOpenLastRoundFromUnknownEarlierRound()
    {
        var goal = GoalWith(WorkTaskStatus.Running, [Dispatch(1, "p", "m"), Dispatch(2, "p", "m")], []);

        var rounds = WorkerRoundLedger.FromGoals([goal]);

        Assert.Equal([WorkerRoundStopCause.Unknown, WorkerRoundStopCause.Open],
            rounds.Select(round => round.StopCause));
        Assert.All(rounds, round => Assert.Null(round.EndedAt));
    }

    private static Goal GoalWith(WorkTaskStatus status,
        IReadOnlyList<TaskDispatchSnapshot> dispatches, IReadOnlyList<ProgressEventSnapshot> timeline)
    {
        var task = new TaskSnapshot("task-1", "Work", AgentRole.Developer, status,
            null, null, null, [], null, null, DispatchHistory: dispatches);
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot("goal-1", "Work", GoalStatus.Active, [task], timeline)], [])).Goals.Single();
    }

    private static TaskDispatchSnapshot Dispatch(double hour, string provider, string model,
        WorkerContextPackageReceipt? usage = null) =>
        new("worker", "command", "working-directory", Start.AddHours(hour), provider, model,
            ContextPackageReceipt: usage);

    private static ProgressEventSnapshot Event(double hour, ProgressKind kind) =>
        new("goal-1", "task-1", kind, "event", Start.AddHours(hour));

    private static WorkerContextPackageReceipt Usage(long? input = null, long? cached = null,
        long? output = null) => new("package", [], Value(input), Value(cached), Value(output));

    private static ProviderUsageValue Value(long? count) => count is { } value
        ? ProviderUsageValue.Reported(value)
        : ProviderUsageValue.Unknown("not reported");
}
