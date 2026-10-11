using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class SemanticAcceptanceTests
{

    [Xunit.Fact(DisplayName = "RunCommandAsync_drain_timeout_unblocks_when_grandchild_holds_pipe_after_parent_exits")]
    public async Task RunCommandAsyncDrainTimeoutUnblocksWhenGrandchildHoldsPipe()
    {
        // Simulate the grandchild-holds-the-pipe bug: the parent PowerShell writes output
        // and exits, but a grandchild (started via Process.Start with UseShellExecute=false
        // so it inherits the stdout pipe handle) keeps the pipe open for 60s. Without a
        // drain timeout, CopyToAsync hangs until the outer 20s CancellationToken fires.
        // A short injected policy expires the drain, kills the tree, and returns before
        // that hang failsafe. Do not assert wall-clock; the 20s token is failsafe only.
        var exe = WorkerShell.Executable;
        var command = $"Write-Output 'verdict'; $psi = [System.Diagnostics.ProcessStartInfo]::new('{exe}', '-NonInteractive -Command Start-Sleep 60'); $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; [System.Diagnostics.Process]::Start($psi) | Out-Null; exit 0";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var drainPolicy = new ProcessOutputDrainPolicy(
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(200)).Validate();

        await WorkerProcessRunner.RunBufferedAsync(
            new WorkerProcessRunRequest(
                command,
                Path.GetTempPath(),
                OutputDrainPolicy: drainPolicy),
            cts.Token);

        Assert.False(cts.IsCancellationRequested);
    }

    [Xunit.Fact(DisplayName = "ChatCompletionsModelProvider_maps_non_null_reasoning_effort_to_request_field")]
    public async Task ChatCompletionsProviderMapsReasoningEffortToRequestField()
    {
        string? capturedBody = null;
        var handler = new FakeHttpHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":3}}""",
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var provider = new ChatCompletionsModelProvider(http, "test-model", "TestProvider");
        var request = new ModelRequest(
            "system prompt",
            [new ModelMessage("user", "hello")],
            new ModelOptions(ReasoningEffort: "medium"));

        await provider.CompleteAsync(request, default);

        Assert.NotNull(capturedBody);
        var doc = System.Text.Json.JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.TryGetProperty("reasoning_effort", out var prop));
        Assert.Equal("medium", prop.GetString());
    }

    [Xunit.Fact(DisplayName = "ChatCompletionsModelProvider_omits_reasoning_effort_when_null")]
    public async Task ChatCompletionsProviderOmitsReasoningEffortWhenNull()
    {
        string? capturedBody = null;
        var handler = new FakeHttpHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":3}}""",
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var provider = new ChatCompletionsModelProvider(http, "test-model", "TestProvider");
        var request = new ModelRequest(
            "system prompt",
            [new ModelMessage("user", "hello")],
            new ModelOptions(ReasoningEffort: null));

        await provider.CompleteAsync(request, default);

        Assert.NotNull(capturedBody);
        var doc = System.Text.Json.JsonDocument.Parse(capturedBody!);
        Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
