using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

public sealed class ProviderIntegrationTests
{
    [Xunit.Fact(DisplayName = "ProviderSmokeTester_sends_minimal_request_and_returns_result")]
    public async Task ProviderSmokeTesterSendsMinimalRequestAndReturnsResult()
{
    var provider = new FakeSmokeProvider();
    var tester = new ProviderSmokeTester();

    var result = await tester.RunAsync(provider);

    var request = provider.LastRequest ?? throw new InvalidOperationException("Expected smoke request was not captured.");
    Assert.Equal("Fake", result.ProviderName);
    Assert.Equal("OK", result.ResponseText);
    Assert.Equal(1, result.Usage!.InputTokens);
    Assert.Equal(2, result.Usage.OutputTokens);
    Assert.Equal("stop", result.StopReason);
    Assert.Equal(0, request.Options.Temperature);
    Assert.Equal(8, request.Options.MaxOutputTokens);
    Assert.Contains("Connectivity smoke test", request.SystemPrompt, StringComparison.Ordinal);
    Assert.Contains("Reply exactly OK", request.SystemPrompt, StringComparison.Ordinal);
    Assert.Equal("Reply OK.", request.Messages.Single().Content);
}
    [Xunit.Fact(DisplayName = "OpenAiResponsesModelProvider_sends_request_and_parses_response")]
    public async Task OpenAiResponsesModelProviderSendsRequestAndParsesResponse()
{
    var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
        {
          "status": "completed",
          "output_text": "implemented",
          "usage": { "input_tokens": 10, "output_tokens": 5 }
        }
        """)
    });
    var provider = new OpenAiResponsesModelProvider(new HttpClient(handler), "openai-key", "gpt-test");

    var response = await provider.CompleteAsync(TestRequest(), CancellationToken.None);

    Assert.Equal("implemented", response.Text);
    Assert.Equal(10, response.Usage!.InputTokens);
    Assert.Equal(5, response.Usage.OutputTokens);
    Assert.Equal("completed", response.StopReason);
    Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
    Assert.Equal("openai-key", handler.LastRequest.Headers.Authorization.Parameter);
    Assert.Equal("https://api.openai.com/v1/responses", handler.LastRequest.RequestUri!.ToString());
    Assert.Contains("\"model\":\"gpt-test\"", handler.LastBody!, StringComparison.Ordinal);
    Assert.Contains("\"instructions\":\"system prompt\"", handler.LastBody!, StringComparison.Ordinal);
    Assert.Contains("\"max_output_tokens\":123", handler.LastBody!, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "OpenAiResponsesModelProvider_uses_routine_cap_when_request_omits_max_output_tokens")]
    public async Task OpenAiResponsesModelProviderUsesRoutineCapWhenRequestOmitsMaxOutputTokens()
{
    var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{ "status": "completed", "output_text": "implemented" }""")
    });
    var provider = new OpenAiResponsesModelProvider(new HttpClient(handler), "openai-key", "gpt-test");

    await provider.CompleteAsync(TestRequest(new ModelOptions()), CancellationToken.None);

    Assert.Contains("\"max_output_tokens\":2048", handler.LastBody!, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "OpenAiResponsesModelProvider_sends_reasoning_effort_when_configured")]
    public async Task OpenAiResponsesModelProviderSendsReasoningEffortWhenConfigured()
{
    var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{ "status": "completed", "output_text": "implemented" }""")
    });
    var provider = new OpenAiResponsesModelProvider(new HttpClient(handler), "openai-key", "gpt-test");

    await provider.CompleteAsync(TestRequest(new ModelOptions(Temperature: 0.2, MaxOutputTokens: 123, ReasoningEffort: "high")), CancellationToken.None);

    Assert.Contains("\"reasoning\":{\"effort\":\"high\"}", handler.LastBody!, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "AnthropicMessagesModelProvider_sends_request_and_parses_response")]
    public async Task AnthropicMessagesModelProviderSendsRequestAndParsesResponse()
{
    var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
        {
          "content": [{ "type": "text", "text": "reviewed" }],
          "stop_reason": "end_turn",
          "usage": { "input_tokens": 11, "output_tokens": 6 }
        }
        """)
    });
    var provider = new AnthropicMessagesModelProvider(new HttpClient(handler), "anthropic-key", "claude-test");

    var response = await provider.CompleteAsync(TestRequest(), CancellationToken.None);

    Assert.Equal("reviewed", response.Text);
    Assert.Equal(11, response.Usage!.InputTokens);
    Assert.Equal(6, response.Usage.OutputTokens);
    Assert.Equal("end_turn", response.StopReason);
    var request = handler.LastRequest ?? throw new InvalidOperationException("Expected HTTP request was not captured.");
    var body = handler.LastBody ?? throw new InvalidOperationException("Expected HTTP request body was not captured.");
    var keys = GetRequiredHeader(request, "x-api-key");
    Assert.Equal("anthropic-key", keys.Single());
    var versions = GetRequiredHeader(request, "anthropic-version");
    Assert.Equal("2023-06-01", versions.Single());
    Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.ToString());
    Assert.Contains("\"model\":\"claude-test\"", body, StringComparison.Ordinal);
    Assert.Contains("\"system\":\"system prompt\"", body, StringComparison.Ordinal);
    Assert.Contains("\"max_tokens\":123", body, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "AnthropicMessagesModelProvider_uses_routine_cap_when_request_omits_max_output_tokens")]
    public async Task AnthropicMessagesModelProviderUsesRoutineCapWhenRequestOmitsMaxOutputTokens()
{
    var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{ "content": [{ "type": "text", "text": "reviewed" }], "stop_reason": "end_turn" }""")
    });
    var provider = new AnthropicMessagesModelProvider(new HttpClient(handler), "anthropic-key", "claude-test");

    await provider.CompleteAsync(TestRequest(new ModelOptions()), CancellationToken.None);

    Assert.Contains("\"max_tokens\":2048", handler.LastBody!, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "ModelProviders_throw_on_http_error")]
    public async Task ModelProvidersThrowOnHttpError()
{
    var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
    {
        Content = new StringContent("""{"error":"bad request"}""")
    });
    var provider = new OpenAiResponsesModelProvider(new HttpClient(handler), "openai-key", "gpt-test");

    var ex = await Xunit.Assert.ThrowsAsync<HttpRequestException>(async () => await provider.CompleteAsync(TestRequest(), CancellationToken.None));

    Assert.Contains("400", ex.Message, StringComparison.Ordinal);
    Assert.Contains("bad request", ex.Message, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "ModelProviders_trim_verbose_http_error_bodies")]
    public async Task ModelProvidersTrimVerboseHttpErrorBodies()
{
    var body = "error-start " + new string('e', 2000) + " error-tail";
    var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(body)
    });
    var provider = new OpenAiResponsesModelProvider(new HttpClient(handler), "openai-key", "gpt-test");

    var ex = await Xunit.Assert.ThrowsAsync<HttpRequestException>(async () => await provider.CompleteAsync(TestRequest(), CancellationToken.None));

    Assert.Contains("OpenAI request failed with 400", ex.Message, StringComparison.Ordinal);
    Assert.Contains("error-start", ex.Message, StringComparison.Ordinal);
    Assert.Contains("error-tail", ex.Message, StringComparison.Ordinal);
    Assert.Contains("[truncated", ex.Message, StringComparison.Ordinal);
    Assert.True(!ex.Message.Contains(new string('e', 2000), StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "ChatCompletionsModelProvider_trims_verbose_http_error_bodies")]
    public async Task ChatCompletionsModelProviderTrimsVerboseHttpErrorBodies()
{
    var body = "chat-error-start " + new string('c', 2000) + " chat-error-tail";
    var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
    {
        Content = new StringContent(body)
    });
    var provider = new ChatCompletionsModelProvider(new HttpClient(handler)
    {
        BaseAddress = new Uri("http://localhost:11434/")
    }, "qwen3", "Ollama");

    var ex = await Xunit.Assert.ThrowsAsync<HttpRequestException>(async () => await provider.CompleteAsync(TestRequest(), CancellationToken.None));

    Assert.Contains("Ollama request failed with 502", ex.Message, StringComparison.Ordinal);
    Assert.Contains("chat-error-start", ex.Message, StringComparison.Ordinal);
    Assert.Contains("chat-error-tail", ex.Message, StringComparison.Ordinal);
    Assert.Contains("[truncated", ex.Message, StringComparison.Ordinal);
    Assert.True(!ex.Message.Contains(new string('c', 2000), StringComparison.Ordinal));
}

}
