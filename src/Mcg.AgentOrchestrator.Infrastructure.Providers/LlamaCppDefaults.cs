namespace Mcg.AgentOrchestrator.Infrastructure;

public static class LlamaCppDefaults
{
    public const string BaseUrl = "http://127.0.0.1:8080";
    public const string OpenAiCompatiblePath = "/v1";
    public const string OpenAiModelsPath = "/v1/models";
    public const string OpenAiApiKey = "llamacpp";
    public const string DefaultModelAlias = "qwen3.6-35b-a3b";

    // Live llama-server on this box is started with `-c 32768`.
    public const int ContextWindowTokens = 32768;

    public static string ResolveBaseUrl()
    {
        return ResolveBaseUrl(Environment.GetEnvironmentVariable("LLAMA_CPP_BASE_URL"));
    }

    public static string ResolveBaseUrl(string? configured)
    {
        return string.IsNullOrWhiteSpace(configured) ? BaseUrl : configured.Trim();
    }

    public static string BuildOpenAiCompatibleBaseUrl(string baseUrl) =>
        $"{baseUrl.TrimEnd('/')}{OpenAiCompatiblePath}";

    public static string BuildOpenAiModelsUrl(string baseUrl) =>
        $"{baseUrl.TrimEnd('/')}{OpenAiModelsPath}";
}
