using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BackgroundDispatchRunnerClaudeTranscriptUsageTests
{
    [Fact]
    public void ClaudeCompletionAttachesSummedTranscriptUsageToDispatchReceipt() =>
        ClaudeTranscriptUsageReaderTests.WithRoot(root =>
        {
            const string sessionId = "receipt-session";
            ClaudeTranscriptUsageReaderTests.Write(root, sessionId, """
                {"type":"assistant","message":{"id":"a","usage":{"input_tokens":10,"cache_creation_input_tokens":100,"cache_read_input_tokens":1000,"output_tokens":5}}}
                {"type":"assistant","message":{"id":"a","usage":{"input_tokens":10,"cache_creation_input_tokens":100,"cache_read_input_tokens":1000,"output_tokens":5}}}
                {"type":"assistant","message":{"id":"b","usage":{"input_tokens":20,"cache_creation_input_tokens":200,"cache_read_input_tokens":2000,"output_tokens":7}}}
                """);
            var (kernel, goal) = ConductorDriverTests.SimpleGoal("Attach Claude usage");
            var task = goal.Tasks.First();
            var startedAt = DateTimeOffset.Parse("2026-09-05T15:05:00Z");
            var receipt = EmptyReceipt();
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "developer", "claude -p", root, startedAt,
                WorkerProviderKind: ProviderKind.AnthropicClaudeCli,
                ProviderSessionId: sessionId,
                ContextPackageReceipt: receipt));
            var stdout = Path.Combine(root, "worker.out.log");
            var stderr = Path.Combine(root, "worker.err.log");
            var exit = Path.Combine(root, "worker.exit.txt");
            File.WriteAllText(stdout, "no WORKER_RESULT block was emitted");
            File.WriteAllText(stderr, string.Empty);
            File.WriteAllText(exit, "1");
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
                4242, "claude -p", root, stdout, stderr, exit, startedAt, null, null,
                OwnedProcessIds: [4242]));
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "round exited");

            var runner = new BackgroundDispatchRunner(isStillRunning: _ => false)
            {
                ClaudeTranscriptUsage = new ClaudeTranscriptUsageReader([root])
            };
            Assert.Equal(1, runner.SweepExitedProcesses(kernel));
            var attached = kernel.GetTask(goal.Id, task.Id).LastDispatch!.ContextPackageReceipt!;
            Assert.Equal(ProviderUsageState.Reported, attached.InputTokens.State);
            Assert.Equal(3330, attached.InputTokens.Value);
            Assert.Equal(3000, attached.CachedInputTokens.Value);
            Assert.Equal(12, attached.OutputTokens.Value);
        });

    [Fact]
    public void CodexDispatchStillUsesStructuredStdoutUsage() =>
        ClaudeTranscriptUsageReaderTests.WithRoot(root =>
        {
            ClaudeTranscriptUsageReaderTests.Write(root, "codex-session", """
                {"type":"assistant","message":{"id":"a","usage":{"input_tokens":900,"output_tokens":900}}}
                """);
            var stdout = Path.Combine(root, "codex.out.log");
            File.WriteAllText(stdout, """
                {"type":"item.completed","item":{"type":"agent_message","text":"WORKER_RESULT_OK"}}
                {"type":"turn.completed","usage":{"input_tokens":23,"cached_input_tokens":11,"output_tokens":7}}
                """);
            var dispatch = new TaskDispatchRecord("developer", "codex exec --json", root,
                DateTimeOffset.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli,
                ProviderSessionId: "codex-session", ContextPackageReceipt: EmptyReceipt());
            var runner = new BackgroundDispatchRunner
            {
                ClaudeTranscriptUsage = new ClaudeTranscriptUsageReader([root])
            };

            var result = runner.ResolveDispatchProviderUsage(dispatch, stdout);

            Assert.Equal(23, result.Usage!.InputTokens);
            Assert.Equal(11, result.Usage.CachedInputTokens);
            Assert.Equal(7, result.Usage.OutputTokens);
            Assert.Equal("WORKER_RESULT_OK", File.ReadAllText(stdout));
        });

    [Theory]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("no-usage")]
    public void ClaudeUsageUnavailableCasesAttachTypedUnknownWithCaseReason(string caseName) =>
        ClaudeTranscriptUsageReaderTests.WithRoot(root =>
        {
            const string sessionId = "unavailable-session";
            if (caseName != "missing")
                ClaudeTranscriptUsageReaderTests.Write(root, sessionId, caseName == "no-usage" ? "{}" : "ignored");
            var reader = caseName == "unreadable"
                ? new ClaudeTranscriptUsageReader([root], _ => throw new IOException("fixture"))
                : new ClaudeTranscriptUsageReader([root]);
            var dispatch = new TaskDispatchRecord("developer", "claude -p", root,
                DateTimeOffset.UtcNow, WorkerProviderKind: ProviderKind.AnthropicClaudeCli,
                ProviderSessionId: sessionId, ContextPackageReceipt: EmptyReceipt());
            var runner = new BackgroundDispatchRunner { ClaudeTranscriptUsage = reader };

            var usage = runner.ResolveDispatchProviderUsage(dispatch, Path.Combine(root, "unused.out.log"));
            var attached = dispatch.ContextPackageReceipt!.WithProviderUsage(usage.Usage, usage.UnavailableReason);

            Assert.Equal(ProviderUsageState.Unknown, attached.InputTokens.State);
            Assert.Equal(ProviderUsageState.Unknown, attached.CachedInputTokens.State);
            Assert.Equal(ProviderUsageState.Unknown, attached.OutputTokens.State);
            Assert.Contains(caseName, attached.InputTokens.UnknownReason!);
            Assert.Contains(sessionId, attached.InputTokens.UnknownReason!);
        });

    private static WorkerContextPackageReceipt EmptyReceipt() => new(
        "fixture", [], ProviderUsageValue.Unknown("not-yet-reported"),
        ProviderUsageValue.Unknown("not-yet-reported"),
        ProviderUsageValue.Unknown("not-yet-reported"));
}
