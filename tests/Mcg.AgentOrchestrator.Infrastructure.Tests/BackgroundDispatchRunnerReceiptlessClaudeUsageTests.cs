using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixtures use unique temporary roots and injected process liveness; no process is spawned.
public sealed class BackgroundDispatchRunnerReceiptlessClaudeUsageTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-05T15:05:00Z");

    [Fact]
    public void ReceiptlessClaudeCompletionStoresUsage() => WithCompletedDispatch((kernel, goal, reader) =>
    {
        var dispatch = kernel.GetTask(goal.Id, goal.Tasks.First().Id).LastDispatch!;
        Assert.Null(dispatch.ContextPackageReceipt);
        var expected = reader.Read(dispatch.ProviderSessionId).Usage!;
        Assert.Equal(3330, expected.InputTokens);
        Assert.Equal(3000, expected.CachedInputTokens);
        Assert.Equal(12, expected.OutputTokens);
        Assert.Equal(DispatchProviderUsage.From(expected, null), dispatch.ProviderUsage);
        Assert.Equal(dispatch.ProviderUsage, goal.Tasks.First().DispatchHistory.Single().ProviderUsage);

        var round = Assert.Single(WorkerRoundLedger.FromGoals([goal]));
        Assert.True(round.UsageReported);
        Assert.Equal(expected.InputTokens, round.InputTokens);
        Assert.Equal(expected.CachedInputTokens, round.CachedInputTokens);
        Assert.Equal(expected.OutputTokens, round.OutputTokens);
    });

    [Fact]
    public void ReceiptlessClaudeMissingTranscriptStoresReason() => WithCompletedDispatch((kernel, goal, reader) =>
    {
        var dispatch = kernel.GetTask(goal.Id, goal.Tasks.First().Id).LastDispatch!;
        var expected = reader.Read(dispatch.ProviderSessionId);
        Assert.Null(expected.Usage);
        Assert.StartsWith("claude-transcript-missing", expected.UnavailableReason);
        Assert.Null(dispatch.ContextPackageReceipt);
        Assert.NotNull(dispatch.ProviderUsage);
        Assert.All(new[] { dispatch.ProviderUsage.InputTokens, dispatch.ProviderUsage.CachedInputTokens,
            dispatch.ProviderUsage.OutputTokens }, value =>
        {
            Assert.Equal(ProviderUsageState.Unknown, value.State);
            Assert.Null(value.Value);
            Assert.Equal(expected.UnavailableReason, value.UnknownReason);
        });
        var round = Assert.Single(WorkerRoundLedger.FromGoals([goal]));
        Assert.False(round.UsageReported);
        Assert.Null(round.InputTokens);
        Assert.Null(round.CachedInputTokens);
        Assert.Null(round.OutputTokens);
    }, writeTranscript: false);

    [Fact]
    public void OwnerDigestRoundsIncludesReceiptlessClaudeTotals() => WithCompletedDispatch((_, goal, _) =>
    {
        var end = Start.AddHours(1);
        var digest = OwnerDigestReport.Build([], [], new FixedClock(end), Start, end);
        var row = Assert.Single(CliOwnerDigestRounds.Aggregate(digest, [goal]).ByModel);
        Assert.Equal("Anthropic/claude-opus-5-5", row.Key);
        Assert.Equal(1, row.Rounds);
        Assert.Equal(3330, row.InputTokens);
        Assert.Equal(3000, row.CachedInputTokens);
        Assert.Equal(12, row.OutputTokens);
        Assert.Equal(0, row.UsageUnreported);
    });

    [Fact]
    public void ReceiptlessClaudeLowIntegrityUsesSandboxTranscriptRoot() =>
        ClaudeTranscriptUsageReaderTests.WithRoot(root =>
        {
            var sandbox = Path.Combine(root, ".mcg-sandbox");
            ClaudeTranscriptUsageReaderTests.Write(sandbox, "sandbox-session", """
                {"type":"assistant","message":{"id":"sandbox","usage":{"input_tokens":5,"cache_read_input_tokens":3,"output_tokens":2}}}
                """);
            var dispatch = new TaskDispatchRecord("developer", "claude -p", root, Start,
                SandboxLowIntegrity: true, WorkerProviderKind: ProviderKind.AnthropicClaudeCli,
                ProviderSessionId: "sandbox-session");
            var runner = new BackgroundDispatchRunner
            {
                ClaudeTranscriptUsage = new ClaudeTranscriptUsageReader([])
            };
            var result = runner.ResolveDispatchProviderUsage(dispatch, Path.Combine(root, "unused.out.log"));
            Assert.Equal(new ProviderReportedUsage(8, 2, 3), result.Usage);
        });

    private static void WithCompletedDispatch(
        Action<AgentOrchestratorKernel, Goal, ClaudeTranscriptUsageReader> assert, bool writeTranscript = true) =>
        ClaudeTranscriptUsageReaderTests.WithRoot(root =>
        {
            const string sessionId = "receiptless-session";
            if (writeTranscript)
                ClaudeTranscriptUsageReaderTests.Write(root, sessionId, """
                    {"type":"assistant","message":{"id":"a","usage":{"input_tokens":10,"cache_creation_input_tokens":100,"cache_read_input_tokens":1000,"output_tokens":5}}}
                    {"type":"assistant","message":{"id":"b","usage":{"input_tokens":20,"cache_creation_input_tokens":200,"cache_read_input_tokens":2000,"output_tokens":7}}}
                    """);
            var (kernel, goal) = ConductorDriverTests.SimpleGoal("Receiptless usage fixture");
            var task = goal.Tasks.First();
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "developer", "claude -p", root, Start, ProviderName: "Anthropic", ModelName: "claude-opus-5-5",
                WorkerProviderKind: ProviderKind.AnthropicClaudeCli, ProviderSessionId: sessionId));
            Assert.Null(task.LastDispatch!.ContextPackageReceipt);
            var stdout = Path.Combine(root, "worker.out.log");
            var stderr = Path.Combine(root, "worker.err.log");
            var exit = Path.Combine(root, "worker.exit.txt");
            File.WriteAllText(stdout, "no WORKER_RESULT block was emitted");
            File.WriteAllText(stderr, string.Empty);
            File.WriteAllText(exit, "1");
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
                4242, "claude -p", root, stdout, stderr, exit, Start, null, null, OwnedProcessIds: [4242]));
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "round exited");
            var reader = new ClaudeTranscriptUsageReader([root]);
            var runner = new BackgroundDispatchRunner(isStillRunning: _ => false) { ClaudeTranscriptUsage = reader };
            Assert.Equal(1, runner.SweepExitedProcesses(kernel));
            Assert.NotNull(task.LastVerification);
            Assert.NotNull(task.LastProcess!.CompletedAt);
            assert(kernel, goal, reader);
        });

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}
