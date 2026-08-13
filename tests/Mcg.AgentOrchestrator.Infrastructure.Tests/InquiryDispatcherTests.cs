using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

[Xunit.Collection(TestCollections.ChaosGateGit)]
public sealed class InquiryDispatcherTests
{
    private static readonly DateTimeOffset DispatchedAt = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-20T10:05:00Z");

    [Xunit.Fact(DisplayName = "InquiryDispatcher_composes_codex_resume_with_freshness_envelope_and_no_last")]
    public void InquiryDispatcherComposesCodexResumeWithFreshnessEnvelopeAndNoLast()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateCompletedDispatch(temp.Path, ProviderKind.OpenAICodexCli, "codex-cli");
        var dispatcher = NewDispatcher(fixture.Task);

        var plan = dispatcher.Compose(CreateRequest(temp.Path, fixture.Goal, fixture.Task));

        Xunit.Assert.Equal(InquiryExecutionMode.Resume, plan.ExecutionMode);
        Xunit.Assert.StartsWith(InquiryDispatcher.FreshnessEnvelopeHeader, plan.PromptContent, StringComparison.Ordinal);
        Xunit.Assert.Contains("codex exec --skip-git-repo-check", plan.Command, StringComparison.Ordinal);
        Xunit.Assert.Contains(" resume 'parent-session-1234' -", plan.Command, StringComparison.Ordinal);
        Xunit.Assert.Contains("--sandbox read-only", plan.Command, StringComparison.Ordinal);
        Xunit.Assert.True(
            plan.Command.IndexOf("--sandbox read-only", StringComparison.Ordinal) <
            plan.Command.IndexOf(" resume ", StringComparison.Ordinal));
        Xunit.Assert.True(
            plan.Command.IndexOf("--cd ", StringComparison.Ordinal) <
            plan.Command.IndexOf(" resume ", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain("--last", plan.Command, StringComparison.Ordinal);
        Xunit.Assert.True(plan.Admission.AllowsResume);
    }

    [Xunit.Fact(DisplayName = "InquiryDispatcher_composes_claude_forked_resume")]
    public void InquiryDispatcherComposesClaudeForkedResume()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateCompletedDispatch(temp.Path, ProviderKind.AnthropicClaudeCli, "claude-cli", modelName: "claude-sonnet-4-6");
        var dispatcher = NewDispatcher(fixture.Task);

        var plan = dispatcher.Compose(CreateRequest(temp.Path, fixture.Goal, fixture.Task));

        Xunit.Assert.Equal(InquiryExecutionMode.ForkedResume, plan.ExecutionMode);
        Xunit.Assert.Contains("--resume", plan.Command, StringComparison.Ordinal);
        Xunit.Assert.Contains("--fork-session", plan.Command, StringComparison.Ordinal);
        Xunit.Assert.Contains("--permission-mode plan", plan.Command, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("--last", plan.Command, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "InquiryDispatcher_falls_back_to_fresh_exec_on_gate_failure")]
    public void InquiryDispatcherFallsBackToFreshExecOnGateFailure()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateCompletedDispatch(temp.Path, ProviderKind.OpenAICodexCli, "codex-cli", sessionId: null);
        var dispatcher = NewDispatcher(fixture.Task);

        var plan = dispatcher.Compose(CreateRequest(temp.Path, fixture.Goal, fixture.Task));

        Xunit.Assert.Equal(InquiryExecutionMode.FreshExec, plan.ExecutionMode);
        Xunit.Assert.Contains("codex exec --skip-git-repo-check", plan.Command, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(" resume ", plan.Command, StringComparison.Ordinal);
        Xunit.Assert.Contains(
            plan.Admission.FailedChecks,
            check => check.Kind == InquiryAdmissionCheckKind.SessionPresent);
    }

    [Xunit.Fact(DisplayName = "InquiryDispatcher_receipt_is_post_hoc_and_does_not_mutate_task_state")]
    public async Task InquiryDispatcherReceiptIsPostHocAndDoesNotMutateTaskState()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateCompletedDispatch(temp.Path, ProviderKind.AnthropicClaudeCli, "claude-cli", modelName: "claude-sonnet-4-6");
        WorkerProcessRunRequest? captured = null;
        var dispatcher = NewDispatcher(
            fixture.Task,
            (request, _) =>
            {
                captured = request;
                return Task.FromResult(new WorkerProcessRunResult(
                    0,
                    "answer\ninput tokens: 10\ncached tokens: 7\noutput tokens: 3\nsession id: fork-session-1234",
                    ""));
            });
        var status = fixture.Task.Status;
        var lastVerification = fixture.Task.LastVerification;
        var lastProcess = fixture.Task.LastProcess;

        var result = await dispatcher.DispatchAsync(CreateRequest(temp.Path, fixture.Goal, fixture.Task));

        Xunit.Assert.Equal(status, fixture.Task.Status);
        Xunit.Assert.Same(lastVerification, fixture.Task.LastVerification);
        Xunit.Assert.Same(lastProcess, fixture.Task.LastProcess);
        Xunit.Assert.NotNull(captured);
        Xunit.Assert.StartsWith(InquiryDispatcher.FreshnessEnvelopeHeader, captured!.StandardInput, StringComparison.Ordinal);
        Xunit.Assert.Equal(InquiryAnswerReceipt.InquiryKind, result.Receipt.Kind);
        Xunit.Assert.Equal(InquiryAnswerReceipt.PostHocClaimsLabel, result.Receipt.ClaimsLabel);
        Xunit.Assert.Equal(fixture.Task.LastDispatch!.ProviderSessionId, result.Receipt.ParentSessionId);
        Xunit.Assert.Equal("fork-session-1234", result.Receipt.ForkedSessionId);
        Xunit.Assert.Equal(10, result.Receipt.Usage.InputTokens);
        Xunit.Assert.Equal(7, result.Receipt.Usage.CachedInputTokens);
        Xunit.Assert.Equal(3, result.Receipt.Usage.OutputTokens);
        Xunit.Assert.True(File.Exists(result.ReceiptPath));
        Xunit.Assert.True(File.Exists(result.Receipt.AnswerTranscriptPath));
    }

    [Xunit.Fact(DisplayName = "InquiryDispatcher_seals_codex_resume_session_after_nonforked_inquiry")]
    public async Task InquiryDispatcherSealsCodexResumeSessionAfterNonforkedInquiry()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateCompletedDispatch(temp.Path, ProviderKind.OpenAICodexCli, "codex-cli");
        var dispatcher = NewDispatcher(fixture.Task, (_, _) => Task.FromResult(new WorkerProcessRunResult(0, "answer", "")));
        var request = CreateRequest(temp.Path, fixture.Goal, fixture.Task);

        var first = await dispatcher.DispatchAsync(request);
        var second = dispatcher.Compose(request);

        Xunit.Assert.Equal(InquiryExecutionMode.Resume, first.Receipt.ExecutionMode);
        Xunit.Assert.Equal(InquiryExecutionMode.FreshExec, second.ExecutionMode);
        Xunit.Assert.Contains(
            second.Admission.FailedChecks,
            check => check.Kind == InquiryAdmissionCheckKind.SessionNotConsumedByNonForkedInquiry);
    }

    private static InquiryDispatchRequest CreateRequest(string root, Goal goal, TaskSpec task) =>
        new(
            goal,
            task,
            "What changed?",
            Path.Combine(root, "prompts"),
            Path.Combine(root, "inquiries"),
            Path.Combine(root, "goal-events"));

    private static InquiryDispatcher NewDispatcher(
        TaskSpec task,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? runProcessAsync = null) =>
        new(
            runProcessAsync: runProcessAsync,
            clock: new TestClock(Now),
            headResolver: _ => task.LastDispatch!.WorktreeHeadSha,
            capturedHeadIsAncestor: SameHead);

    private static bool SameHead(string _, string? capturedHead, string? currentHead) =>
        string.Equals(capturedHead?.Trim(), currentHead?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static (Goal Goal, TaskSpec Task) CreateCompletedDispatch(
        string root,
        ProviderKind providerKind,
        string workerName,
        string? sessionId = "parent-session-1234",
        string? modelName = AgentCatalog.OpenAiSubscriptionModelAlias)
    {
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        RunGit(repo, "init");
        RunGit(repo, "config", "user.email", "test@example.invalid");
        RunGit(repo, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(repo, "README.md"), "test");
        RunGit(repo, "add", "README.md");
        RunGit(repo, "commit", "-m", "init");
        var head = RunGit(repo, "rev-parse", "HEAD").Trim();

        var kernel = new AgentOrchestratorKernel(new TestClock(DispatchedAt));
        var taskSpec = new TaskSpec(new TaskId("task-1"), "Implement inquiry.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Inquiry goal", [taskSpec]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            workerName,
            "original command",
            repo,
            DispatchedAt,
            modelName is null ? null : "OpenAI",
            modelName,
            "high",
            WorkerProviderKind: providerKind,
            ProviderSessionId: sessionId,
            WorktreeHeadSha: head));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "done");
        return (goal, task);
    }

    private static string RunGit(string workingDirectory, params string[] args)
    {
        var result = GitCli.Run(workingDirectory, args);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}");
        }

        return result.Output;
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "mcg-inquiry-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
