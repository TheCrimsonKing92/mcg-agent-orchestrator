using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorJudgePanelRunnerTests
{
    private static ModelFunctionBinding Binding(string purpose, string profile) => new(purpose, ModelLane.Capable,
        new ModelProfile("test-provider", "unused-profile-name", ModelCapability.Text, SubscriptionMode.ApiKey),
        Subscription: new(profile, "bound-test-alias", "high"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolved_binding_controls_command_and_blinded_working_directory(bool sonnet)
    {
        using var h = new PanelTestHarness();
        var item = h.Enqueue("runner");
        var purpose = sonnet ? ModelFunctionPurposes.PanelJudgeSonnet : ModelFunctionPurposes.PanelJudgeSol;
        var profile = sonnet ? "claude-cli" : "codex-cli";
        WorkerProcessRunRequest? captured = null;
        var raw = sonnet ? JsonSerializer.Serialize(new { type = "result", is_error = false,
            structured_output = JsonSerializer.Deserialize<JsonElement>(PanelTestHarness.Answer(item.Id)),
            usage = new { input_tokens = 10, output_tokens = 20 } }) : PanelTestHarness.Answer(item.Id);
        Task<PanelProcessResult> Process(WorkerProcessRunRequest request, CancellationToken _)
        {
            captured = request;
            Assert.True(Directory.Exists(request.WorkingDirectory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(request.WorkingDirectory));
            return Task.FromResult(new PanelProcessResult(0, raw, "tokens used\n30\n"));
        }
        ModelFunctionCatalog catalog = new([Binding(purpose, profile)]);
        IConductorPanelJudgeRunner runner = sonnet ? new ClaudeSonnetPanelJudgeRunner(catalog, Process) :
            new CodexSolPanelJudgeRunner(catalog, Process);
        var result = await runner.RunAsync(item, CancellationToken.None);
        Assert.NotNull(captured);
        Assert.Contains("--model 'bound-test-alias'", captured.Command);
        Assert.DoesNotContain("unused-profile-name", captured.Command);
        Assert.Equal(PanelV0Contract.Prompt(item), captured.StandardInput);
        Assert.Equal(ConductorJudgePanelBudgets.JudgeTimeout, captured.Timeout);
        if (sonnet)
        {
            Assert.StartsWith("claude -p", captured.Command);
            Assert.Contains("--tools ''", captured.Command);
            Assert.Contains("--restricted", captured.Command);
            Assert.Contains("--output-format json --json-schema", captured.Command);
        }
        else
        {
            Assert.StartsWith("codex exec", captured.Command);
            Assert.Contains("--sandbox read-only", captured.Command);
            Assert.Contains("--cd '" + captured.WorkingDirectory + "' -", captured.Command);
        }
        Assert.False(Directory.Exists(captured.WorkingDirectory));
        Assert.Equal(PanelJudgeOutcome.Valid, result.Outcome);
        Assert.Equal(raw, result.RawStdout);
        Assert.Equal("tokens used\n30\n", result.RawStderr);
        Assert.Equal("bound-test-alias", result.ModelAlias);
        Assert.NotNull(result.UsageJson);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("duplicate")]
    [InlineData("subscription")]
    [InlineData("profile")]
    [InlineData("alias")]
    [InlineData("effort")]
    [InlineData("catalog-null")]
    [InlineData("null-entry")]
    public async Task Malformed_binding_records_reason_without_process_launch(string shape)
    {
        using var h = new PanelTestHarness();
        var item = h.Enqueue("bad-binding");
        foreach (var sonnet in new[] { false, true })
        {
            var purpose = sonnet ? ModelFunctionPurposes.PanelJudgeSonnet : ModelFunctionPurposes.PanelJudgeSol;
            var binding = Binding(purpose, sonnet ? "claude-cli" : "codex-cli");
            ModelFunctionCatalog catalog = shape switch
            {
                "absent" => ModelFunctionCatalog.Empty, "duplicate" => new([binding, binding]),
                "catalog-null" => new(null!), "null-entry" => new([null!]),
                "subscription" => new([binding with { Subscription = null }]),
                "profile" => new([binding with { Subscription = new("unknown", "alias") }]),
                "alias" => new([binding with { Subscription = new(sonnet ? "claude-cli" : "codex-cli", " ") }]),
                _ => new([binding with { Subscription = binding.Subscription! with { ReasoningEffort = "nonsense" } }])
            };
            var calls = 0;
            Task<PanelProcessResult> Process(WorkerProcessRunRequest _, CancellationToken __)
            { calls++; throw new InvalidOperationException("An invalid binding launched a process"); }
            IConductorPanelJudgeRunner runner = sonnet ? new ClaudeSonnetPanelJudgeRunner(catalog, Process) :
                new CodexSolPanelJudgeRunner(catalog, Process);
            var result = await runner.RunAsync(item, CancellationToken.None);
            Assert.Equal(0, calls);
            Assert.Equal(PanelJudgeOutcome.Skipped, result.Outcome);
            Assert.StartsWith("panel-binding-invalid:" + purpose, result.Reason);
        }
        Assert.StartsWith("panel-binding-invalid:", PanelJudgeBindingResolver.Resolve(ModelFunctionCatalog.Empty, "unknown-purpose").Error);
    }

    [Theory]
    [InlineData(0, false, "", "empty-output")]
    [InlineData(0, false, "fenced", "invalid-output")]
    [InlineData(7, false, "valid", "invocation-failed")]
    [InlineData(0, true, "valid", "timed-out")]
    [InlineData(0, false, "valid", "valid")]
    public async Task Positive_process_fault_evidence_precedes_output_validation(int exit, bool timeout, string answer, string expected)
    {
        using var h = new PanelTestHarness();
        var item = h.Enqueue("fault");
        var output = answer == "valid" ? PanelTestHarness.Answer(item.Id) : answer == "fenced" ? "```json\n{}\n```" : "";
        var calls = 0;
        var runner = new CodexSolPanelJudgeRunner(new([Binding(ModelFunctionPurposes.PanelJudgeSol, "codex-cli")]), (_, _) =>
        {
            calls++;
            return Task.FromResult(new PanelProcessResult(exit, output, "raw stderr", timeout));
        });
        var result = await runner.RunAsync(item, CancellationToken.None);
        Assert.Equal(expected, PanelV0Contract.Token(result.Outcome));
        Assert.Equal(exit, result.ExitCode);
        Assert.Equal(output, result.RawStdout);
        Assert.Equal("raw stderr", result.RawStderr);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Claude_provider_error_is_invocation_failed_even_with_valid_structured_output()
    {
        using var h = new PanelTestHarness();
        var item = h.Enqueue("provider-error");
        var envelope = JsonSerializer.Serialize(new { type = "result", is_error = true,
            structured_output = JsonSerializer.Deserialize<JsonElement>(PanelTestHarness.Answer(item.Id)) });
        var runner = new ClaudeSonnetPanelJudgeRunner(new([Binding(ModelFunctionPurposes.PanelJudgeSonnet, "claude-cli")]),
            (_, _) => Task.FromResult(new PanelProcessResult(0, envelope, "")));
        var result = await runner.RunAsync(item, CancellationToken.None);
        Assert.Equal(PanelJudgeOutcome.InvocationFailed, result.Outcome);
        Assert.Equal(envelope, result.RawStdout);
    }
}
