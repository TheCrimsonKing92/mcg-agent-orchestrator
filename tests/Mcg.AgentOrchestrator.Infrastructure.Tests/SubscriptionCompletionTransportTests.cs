using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every service owns a unique directory and an in-memory kernel/store; all
// provider execution is injected. No processes, shared databases, or environment mutations.
public sealed class SubscriptionCompletionTransportTests : IDisposable
{
    private const string Prompt = "private prompt must never appear in diagnostics";
    private const string SpecJson = """
        {"behavioralContract":"Decoded domain contract","acceptanceCriteria":["Domain criterion A","Domain criterion B"],"verificationClass":"TestVerifiable","decisions":[],"forks":[]}
        """;
    private const string Start = "{\"type\":\"turn.started\"}";
    private const string End = "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":8,\"output_tokens\":3}}";
    private const string Template = "codex exec --json --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox {sandboxMode} --cd {workingDirectory}";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"subscription-transport-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("codex-cli")]
    [InlineData("codex-luna")]
    public async Task FinalCompletedAssistantWins(string profile)
    {
        var raw = string.Join('\n', "{\"type\":\"thread.started\",\"thread_id\":\"test\"}",
            Start, Message("Commentary"), Message(SpecJson),
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"aggregated_output\":\"tool text\"}}", End,
            "{\"type\":\"usage\",\"input_tokens\":8}");
        var calls = 0;
        var completer = Completer(raw, profile: profile, onRun: () => calls++);

        var result = await completer.CompleteSemanticTextWithResultAsync(Prompt, "prompt.md", default);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(SpecJson, result.Text);
        Assert.Equal(raw, result.RawStandardOutput);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FinalTurnExcludesEarlierAnswers()
    {
        var raw = Stream("earlier answer") + "\n" + Stream(SpecJson);
        var result = await Completer(raw).CompleteSemanticTextWithResultAsync(Prompt, "prompt.md", default);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(SpecJson, result.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefinementAcceptsNormalizedSpec(bool fenced)
    {
        var answer = fenced ? $"```json\n{SpecJson}\n```" : SpecJson;
        var raw = " \r\n" + Stream(answer) + "\r\n \t";
        var (service, kernel, goalId) = Scenario(Completer(raw));

        var result = await service.RefineAsync(kernel, goalId);

        Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
        Assert.Equal(RefinementDisposition.Completed, result.Disposition);
        Assert.Null(result.Failure);
        Assert.Equal("OpenAICodexCli", result.Invocation.ProviderKind);
        Assert.Equal("codex-cli", result.Invocation.WorkerProfile);
        Assert.Equal("gpt-test", result.Invocation.Model);
        Assert.Equal("high", result.Invocation.ReasoningEffort);
        var spec = kernel.GetGoal(goalId).RefinedSpec!;
        Assert.Equal("Decoded domain contract", spec.BehavioralContract);
        Assert.Equal(new[] { "Domain criterion A", "Domain criterion B" }, spec.AcceptanceCriteria);
        Assert.Equal(VerificationClass.TestVerifiable, spec.VerificationClass);
        Assert.Contains("outcome=completed", GoalRefinementGate.BuildPolicyReceipt(result));
        await AssertRaw(kernel, goalId, raw);
    }

    public static IEnumerable<object[]> InvalidStreams()
    {
        yield return ["failed-turn", Stream(SpecJson) + "\n" + Start + "\n" + Message(SpecJson) + "\n{\"type\":\"turn.failed\",\"error\":{\"message\":\"private provider error\"}}", "failed-turn"];
        yield return ["error", Stream(SpecJson) + "\n{\"type\":\"error\",\"message\":\"private provider error\"}", "failed-turn"];
        yield return ["missing-final", Stream(SpecJson) + "\n" + Start + "\n" + End, "missing-final-assistant"];
        yield return ["tool-only-final", Stream(SpecJson) + "\n" + Start + "\n{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"text\":\"plausible tool answer\"}}\n" + End, "missing-final-assistant"];
        yield return ["unfinished", Stream(SpecJson) + "\n" + Start + "\n" + Message(SpecJson), "incomplete-turn"];
        yield return ["missing-terminal", Start + "\n" + Message(SpecJson), "incomplete-turn"];
        yield return ["malformed-json", Stream(SpecJson) + "\n{broken private transport", "malformed-json"];
        yield return ["non-object", Stream(SpecJson) + "\n[]", "malformed-event"];
        yield return ["no-type", Stream(SpecJson) + "\n{}", "malformed-event"];
        yield return ["non-string-type", Stream(SpecJson) + "\n{\"type\":7}", "malformed-event"];
        yield return ["bad-item", Start + "\n{\"type\":\"item.completed\",\"item\":[]}", "malformed-event"];
        yield return ["missing-text", Start + "\n{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\"}}\n" + End, "malformed-event"];
        yield return ["empty-last-answer", Start + "\n" + Message(SpecJson) + "\n" + Message(" ") + "\n" + End, "missing-final-assistant"];
        yield return ["nested-turn", Start + "\n" + Message(SpecJson) + "\n" + Start, "nested-turn"];
        yield return ["unmatched-terminal", Stream(SpecJson) + "\n" + End, "unmatched-turn-completion"];
        yield return ["item-outside-turn", Stream(SpecJson) + "\n" + Message(SpecJson), "item-outside-turn"];
        yield return ["bad-usage-shape", Start + "\n" + Message(SpecJson) + "\n{\"type\":\"turn.completed\",\"usage\":[]}", "malformed-event"];
        yield return ["empty", "", "incomplete-turn"];
    }

    [Theory]
    [MemberData(nameof(InvalidStreams))]
    public async Task InvalidTransportFailsClosed(string scenario, string raw, string reason)
    {
        Assert.False(string.IsNullOrWhiteSpace(scenario));
        var completion = await Completer(raw).CompleteSemanticTextWithResultAsync(Prompt, "prompt.md", default);
        Assert.False(completion.Succeeded);
        Assert.Equal(0, completion.ExitCode);
        Assert.Empty(completion.Text);
        Assert.Equal($"codex-transport:{reason}", completion.FailureReason);
        Assert.Equal(raw, completion.RawStandardOutput);
        AssertSafeDiagnostic(completion.FailureReason!);

        var (service, kernel, goalId) = Scenario(Completer(raw));
        var result = await service.RefineAsync(kernel, goalId);
        Assert.Equal(RefinementDisposition.Fallback, result.Disposition);
        Assert.Equal("subscription-failed", result.Failure!.ReasonCode);
        Assert.Contains($"codex-transport:{reason}", result.Failure.Detail);
        Assert.Contains("exit_code=0", result.Failure.Detail);
        AssertSafeDiagnostic(result.Failure.Detail);
        Assert.NotEqual("Decoded domain contract", result.Spec.BehavioralContract);
        Assert.Contains("outcome=fallback", GoalRefinementGate.BuildPolicyReceipt(result));
        await AssertRaw(kernel, goalId, raw);
    }

    [Theory]
    [InlineData("codex-cli", Template)]
    [InlineData("claude-cli", "claude -p")]
    public async Task NonzeroExitPreservesFailure(string profile, string template)
    {
        var raw = " \n" + Stream(SpecJson) + "\n ";
        var completer = Completer(raw, 23, profile, template);
        var completion = await completer.CompleteSemanticTextWithResultAsync(Prompt, "prompt.md", default);
        Assert.False(completion.Succeeded);
        Assert.Equal(23, completion.ExitCode);
        Assert.Equal("subscription-cli-exit:23", completion.FailureReason);
        Assert.Empty(completion.Text);
        Assert.Equal(raw, completion.RawStandardOutput);

        var (service, kernel, goalId) = Scenario(completer, profile);
        var result = await service.RefineAsync(kernel, goalId);
        Assert.Equal(RefinementDisposition.Fallback, result.Disposition);
        Assert.Equal("subscription-failed", result.Failure!.ReasonCode);
        Assert.Contains("subscription-cli-exit:23; exit_code=23", result.Failure.Detail);
        AssertSafeDiagnostic(result.Failure.Detail);
        await AssertRaw(kernel, goalId, raw);
    }

    [Theory]
    [InlineData("codex-cli", "codex exec --json", true)]
    [InlineData("codex-luna", "codex exec '--json'", true)]
    [InlineData("codex-cli", "codex exec \"--json\"", true)]
    [InlineData("codex-cli", "codex exec", false)]
    [InlineData("codex-cli", "codex exec --json-schema schema.json", false)]
    [InlineData("codex-cli", "codex exec --json=true", false)]
    [InlineData("codex-cli", "codex exec --label 'contains --json text'", false)]
    [InlineData("codex-cli", "codex exec --label \"contains --json text\"", false)]
    [InlineData("codex-cli", "codex exec --label 'can''t --json split'", false)]
    [InlineData("codex-cli", "codex exec --label \"quoted `\" --json text\"", false)]
    [InlineData("codex-cli", "codex exec --label some--json", false)]
    [InlineData("codex-cli", "codex exec # --json", false)]
    [InlineData("codex-cli", "codex exec <# --json #>", false)]
    [InlineData("codex-cli", "codex exec <#\n# '--json'\n--json\n#>", false)]
    [InlineData("codex-cli", "codex exec <# --json #> --json", true)]
    [InlineData("codex-cli", "codex exec --json <# ignored #>", true)]
    [InlineData("codex-cli", "codex exec <# ignored #>--json", true)]
    [InlineData("codex-cli", "codex exec '<# --json #>'", false)]
    [InlineData("custom-cli", "custom --json", false)]
    [InlineData("codex-oss-cli", "codex exec --json", false)]
    [InlineData("claude-cli", "claude --json", false)]
    public async Task ProviderAndArgumentGateNormalization(string profile, string template, bool eligible)
    {
        // JSON-looking content cannot establish provider identity or structured invocation.
        var raw = Stream(SpecJson);
        var result = await Completer(raw, profile: profile, template: template)
            .CompleteSemanticTextWithResultAsync(Prompt, "prompt.md", default);
        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(eligible ? SpecJson : raw, result.Text);
        Assert.Equal(raw, result.RawStandardOutput);
    }

    [Theory]
    [InlineData("claude-cli", "claude -p", false)]
    [InlineData("claude-cli", "claude -p", true)]
    [InlineData("custom-cli", "custom --json", true)]
    [InlineData("codex-cli", "codex exec", false)]
    [InlineData("codex-cli", "codex exec --json-schema schema.json", true)]
    [InlineData("codex-cli", "codex exec <# --json #>", false)]
    [InlineData("codex-cli", "codex exec <# --json #>", true)]
    public async Task PlainTemplates_ValidSpec_RetainsRefinement(string profile, string template, bool fenced)
    {
        var text = fenced ? $"```json\n{SpecJson}\n```" : SpecJson;
        var scenario = Scenario(Completer(text, profile: profile, template: template), profile);
        var result = await scenario.Service.RefineAsync(scenario.Kernel, scenario.GoalId);
        Assert.Equal(RefinementDisposition.Completed, result.Disposition);
        Assert.Equal("Decoded domain contract", result.Spec.BehavioralContract);
        Assert.Equal(new[] { "Domain criterion A", "Domain criterion B" }, result.Spec.AcceptanceCriteria);
        await AssertRaw(scenario.Kernel, scenario.GoalId, text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    public async Task LegacyOperationsKeepContracts(int exit)
    {
        var raw = Stream(SpecJson);
        var calls = 0;
        var completer = Completer(raw, exit, onRun: () => calls++);
        Assert.Equal(raw, await completer.CompleteAsync(Prompt, "prompt.md", default));
        var result = await completer.CompleteWithResultAsync(Prompt, "prompt.md", default);
        Assert.Equal(raw, result.StandardOutput);
        Assert.Equal(exit, result.ExitCode);
        Assert.Equal(exit == 0 ? null : "provider failure may contain private text", result.FailureReason);
        Assert.Equal(exit == 0, result.Succeeded);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 17)]
    [InlineData(true, 17)]
    public async Task InvocationKeepsInputAndCleanup(bool promptFile, int exit)
    {
        string? directory = null;
        var calls = 0;
        var raw = "\r\n" + Stream(SpecJson) + "\r\n ";
        var completer = new SubscriptionCliCompleter(
            Template + (promptFile ? " {promptPath}" : ""), "codex-cli", "gpt-test", "high",
            async (command, workingDirectory, standardInput, _) =>
            {
                calls++;
                directory = workingDirectory;
                var path = Path.Combine(workingDirectory, "prompt.md");
                Assert.Equal(Prompt, await File.ReadAllTextAsync(path));
                Assert.Equal(promptFile ? null : Prompt, standardInput);
                Assert.Equal(promptFile, command.Contains("'" + path + "'", StringComparison.Ordinal));
                Assert.Contains("--model 'gpt-test'", command);
                Assert.Contains("model_reasoning_effort='high'", command);
                Assert.Contains("--sandbox 'read-only'", command);
                Assert.Contains("--cd '" + workingDirectory + "'", command);
                // Model the real runner's legacy trim while retaining exact raw stdout.
                return new SubscriptionCliCompletionResult(raw.Trim(), exit) { RawStandardOutput = raw };
            });
        var result = await completer.CompleteSemanticTextWithResultAsync(Prompt, "prompt.md", default);
        Assert.Equal(exit == 0, result.Succeeded);
        Assert.Equal(raw, result.RawStandardOutput);
        Assert.Equal(1, calls);
        Assert.NotNull(directory);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task CancellationAndTimeoutKeepContracts()
    {
        Assert.Equal(TimeSpan.FromSeconds(180), SubscriptionCliCompleter.DefaultTimeout);
        var entered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<SubscriptionCliCompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completer = new SubscriptionCliCompleter(Template, "codex-cli", "gpt-test", "high",
            async (_, directory, _, token) =>
            {
                entered.SetResult(directory);
                return await never.Task.WaitAsync(token);
            });
        var (service, kernel, goalId) = Scenario(completer);
        using var caller = new CancellationTokenSource();
        var pending = service.RefineAsync(kernel, goalId, cancellationToken: caller.Token);
        var directory = await WaitForSignal(entered.Task, "completion runner entry");
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitForSignal(pending, "canceled refinement completion"));
        Assert.False(Directory.Exists(directory));

        // Inject the timeout signal at the runner boundary; no real-time pacing decides this verdict.
        string? timeoutDirectory = null;
        var timeoutCalls = 0;
        var timedOut = new SubscriptionCliCompleter(Template, "codex-cli", "gpt-test", "high",
            (_, dir, _, token) =>
            {
                timeoutCalls++;
                timeoutDirectory = dir;
                Assert.True(token.CanBeCanceled);
                return Task.FromException<SubscriptionCliCompletionResult>(new OperationCanceledException(token));
            });
        var timeoutScenario = Scenario(timedOut);
        var result = await timeoutScenario.Service.RefineAsync(timeoutScenario.Kernel, timeoutScenario.GoalId);
        Assert.Equal(RefinementDisposition.Fallback, result.Disposition);
        Assert.Equal("subscription-timeout", result.Failure!.ReasonCode);
        Assert.Contains("180 seconds", result.Failure.Detail);
        Assert.Equal("OpenAICodexCli", result.Invocation.ProviderKind);
        Assert.Equal(1, timeoutCalls);
        Assert.NotNull(timeoutDirectory);
        Assert.False(Directory.Exists(timeoutDirectory));
    }

    private static SubscriptionCliCompleter Completer(
        string raw, int exit = 0, string profile = "codex-cli", string template = Template, Action? onRun = null) =>
        new(template, profile, "gpt-test", "high", (_, _, _, _) =>
        {
            onRun?.Invoke();
            return Task.FromResult(new SubscriptionCliCompletionResult(raw, exit,
                exit == 0 ? null : "provider failure may contain private text"));
        });

    private (GoalRefinementService Service, AgentOrchestratorKernel Kernel, GoalId GoalId) Scenario(
        SubscriptionCliCompleter completer, string profile = "codex-cli")
    {
        var root = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catalog = new ModelFunctionCatalog([
            new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.Capable,
                new ModelProfile("unused-api", "unused-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Subscription: new SubscriptionLaunchProfile(profile, "gpt-test", "high"))
        ]);
        var service = new GoalRefinementService(new InMemoryModelProviderRegistry([]), catalog,
            new FakeCollaborationItemStore(), new SpecRefinerPrecedentStore(Path.Combine(root, "precedents.json")),
            workerProfiles: new WorkerProfileCatalog([new WorkerProfile(profile, Template)]),
            subscriptionCompleterFactory: _ => completer, rawOutputDirectory: Path.Combine(root, "logs"));
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Original unrefined objective");
        return (service, kernel, goal.Id);
    }

    private static string Message(string text) => JsonSerializer.Serialize(new
    {
        type = "item.completed", item = new { type = "agent_message", text }
    });

    private static string Stream(string answer) => string.Join('\n', Start, Message(answer), End);

    private static void AssertSafeDiagnostic(string diagnostic)
    {
        Assert.InRange(diagnostic.Length, 1, 512);
        Assert.DoesNotContain(Prompt, diagnostic);
        Assert.DoesNotContain("private", diagnostic);
        Assert.DoesNotContain("behavioralContract", diagnostic);
        Assert.DoesNotContain("turn.started", diagnostic);
    }

    private static async Task<T> WaitForSignal<T>(Task<T> task, string signal)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang detector only.
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Did not observe {signal}.", ex);
        }
    }

    private static async Task AssertRaw(AgentOrchestratorKernel kernel, GoalId goalId, string expected)
    {
        const string prefix = "Spec refiner raw output: ";
        var receipt = Assert.Single(kernel.GetGoal(goalId).Timeline.Where(e =>
            e.Kind == ProgressKind.GoalPolicyDecision && e.Message.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.Equal(expected, await File.ReadAllTextAsync(receipt.Message[prefix.Length..]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
