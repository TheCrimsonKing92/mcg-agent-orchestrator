using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class OpenAiResponsesModelProvider : IModelProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _modelName;

    public OpenAiResponsesModelProvider(HttpClient httpClient, string apiKey, string modelName)
    {
        _httpClient = httpClient;
        _modelName = RequireText(modelName, nameof(modelName));
        _httpClient.BaseAddress ??= new Uri("https://api.openai.com/");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RequireText(apiKey, nameof(apiKey)));
    }

    public string ProviderName => "OpenAI";

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        var body = new
        {
            model = string.IsNullOrWhiteSpace(request.Options.ModelName) ? _modelName : request.Options.ModelName,
            instructions = request.SystemPrompt,
            input = request.Messages.Select(message => new { role = message.Role, content = message.Content }).ToArray(),
            max_output_tokens = request.Options.MaxOutputTokens,
            reasoning = string.IsNullOrWhiteSpace(request.Options.ReasoningEffort)
                ? null
                : new { effort = request.Options.ReasoningEffort }
        };

        using var response = await _httpClient.PostAsJsonAsync("v1/responses", body, JsonOptions(), cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, json);

        var payload = JsonSerializer.Deserialize<OpenAiResponsePayload>(json, JsonOptions())
            ?? throw new InvalidOperationException("OpenAI returned an empty response.");

        return new ModelResponse(
            ExtractOpenAiText(payload),
            new ModelUsage(payload.Usage?.InputTokens, payload.Usage?.OutputTokens),
            payload.Status ?? "unknown");
    }

    private static string ExtractOpenAiText(OpenAiResponsePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.OutputText))
        {
            return payload.OutputText;
        }

        var text = payload.Output?
            .SelectMany(item => item.Content ?? [])
            .Where(content => !string.IsNullOrWhiteSpace(content.Text))
            .Select(content => content.Text)
            .ToList();

        return text is { Count: > 0 }
            ? string.Join(Environment.NewLine, text)
            : string.Empty;
    }

    private static JsonSerializerOptions JsonOptions() => SharedJson.Options;

    private static string RequireText(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be empty.", parameterName)
            : value.Trim();
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(ProviderHttpErrorFormatter.Format("OpenAI", response, body));
        }
    }
}

public sealed class AnthropicMessagesModelProvider : IModelProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _modelName;

    public AnthropicMessagesModelProvider(HttpClient httpClient, string apiKey, string modelName)
    {
        _httpClient = httpClient;
        _modelName = RequireText(modelName, nameof(modelName));
        _httpClient.BaseAddress ??= new Uri("https://api.anthropic.com/");
        _httpClient.DefaultRequestHeaders.Add("x-api-key", RequireText(apiKey, nameof(apiKey)));
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public string ProviderName => "Anthropic";

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        var body = new
        {
            model = string.IsNullOrWhiteSpace(request.Options.ModelName) ? _modelName : request.Options.ModelName,
            max_tokens = request.Options.MaxOutputTokens ?? 1200,
            system = request.SystemPrompt,
            messages = request.Messages.Select(message => new { role = NormalizeRole(message.Role), content = message.Content }).ToArray()
        };

        using var response = await _httpClient.PostAsJsonAsync("v1/messages", body, JsonOptions(), cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, json);

        var payload = JsonSerializer.Deserialize<AnthropicResponsePayload>(json, JsonOptions())
            ?? throw new InvalidOperationException("Anthropic returned an empty response.");

        var text = payload.Content is null
            ? string.Empty
            : string.Join(Environment.NewLine, payload.Content.Where(item => item.Type == "text").Select(item => item.Text));

        return new ModelResponse(
            text,
            new ModelUsage(payload.Usage?.InputTokens, payload.Usage?.OutputTokens),
            payload.StopReason ?? "unknown");
    }

    private static string NormalizeRole(string role)
    {
        return role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
    }

    private static JsonSerializerOptions JsonOptions() => SharedJson.Options;

    private static string RequireText(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be empty.", parameterName)
            : value.Trim();
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(ProviderHttpErrorFormatter.Format("Anthropic", response, body));
        }
    }
}

internal static class SharedJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}

internal sealed record OpenAiResponsePayload(
    string? Status,
    [property: JsonPropertyName("output_text")] string? OutputText,
    IReadOnlyList<OpenAiOutputItem>? Output,
    OpenAiUsage? Usage);

internal sealed record OpenAiOutputItem(IReadOnlyList<OpenAiContentItem>? Content);

internal sealed record OpenAiContentItem(string? Type, string? Text);

internal sealed record OpenAiUsage(
    [property: JsonPropertyName("input_tokens")] int? InputTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens);

internal sealed record AnthropicResponsePayload(
    IReadOnlyList<AnthropicContentItem>? Content,
    [property: JsonPropertyName("stop_reason")] string? StopReason,
    AnthropicUsage? Usage);

internal sealed record AnthropicContentItem(string Type, string Text);

internal sealed record AnthropicUsage(
    [property: JsonPropertyName("input_tokens")] int? InputTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens);

public sealed class ChatCompletionsModelProvider : IModelProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _modelName;
    private readonly string _providerName;

    public ChatCompletionsModelProvider(HttpClient httpClient, string modelName, string providerName)
    {
        _httpClient = httpClient;
        _modelName = RequireText(modelName, nameof(modelName));
        _providerName = RequireText(providerName, nameof(providerName));
    }

    public string ProviderName => _providerName;

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new { role = "system", content = request.SystemPrompt });
        }

        foreach (var message in request.Messages)
        {
            messages.Add(new { role = message.Role, content = message.Content });
        }

        var body = new
        {
            model = string.IsNullOrWhiteSpace(request.Options.ModelName) ? _modelName : request.Options.ModelName,
            messages,
            stream = false,
            temperature = request.Options.Temperature,
            max_tokens = request.Options.MaxOutputTokens
        };

        using var response = await _httpClient.PostAsJsonAsync("v1/chat/completions", body, JsonOptions(), cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, json);

        var payload = JsonSerializer.Deserialize<ChatCompletionsPayload>(json, JsonOptions())
            ?? throw new InvalidOperationException($"{_providerName} returned an empty response.");

        var choice = payload.Choices?.FirstOrDefault();
        var text = choice?.Message?.Content;
        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(choice?.Message?.Reasoning))
        {
            text = choice.Message.Reasoning;
        }
        text ??= string.Empty;
        var finishReason = choice?.FinishReason ?? "unknown";

        return new ModelResponse(
            text,
            new ModelUsage(payload.Usage?.PromptTokens, payload.Usage?.CompletionTokens),
            finishReason);
    }

    private static JsonSerializerOptions JsonOptions() => SharedJson.Options;

    private static string RequireText(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be empty.", parameterName)
            : value.Trim();
    }

    private void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(ProviderHttpErrorFormatter.Format(_providerName, response, body));
        }
    }
}

internal static class ProviderHttpErrorFormatter
{
    private const int BodyMaxChars = 600;
    private const int BodyHeadChars = 420;
    private const int BodyTailChars = 120;

    public static string Format(string providerName, HttpResponseMessage response, string body)
    {
        return $"{providerName} request failed with {(int)response.StatusCode}: {TrimBody(body)}";
    }

    private static string TrimBody(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length <= BodyMaxChars)
        {
            return trimmed;
        }

        var omitted = trimmed.Length - BodyHeadChars - BodyTailChars;
        return trimmed[..BodyHeadChars] +
            $"{Environment.NewLine}...[truncated {omitted} chars from provider error]...{Environment.NewLine}" +
            trimmed[^BodyTailChars..];
    }
}

internal sealed record ChatCompletionsPayload(
    IReadOnlyList<ChatCompletionsChoice>? Choices,
    ChatCompletionsUsage? Usage);

internal sealed record ChatCompletionsChoice(
    ChatCompletionsMessage? Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record ChatCompletionsMessage(string? Role, string? Content, string? Reasoning);

internal sealed record ChatCompletionsUsage(
    [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int? CompletionTokens);
