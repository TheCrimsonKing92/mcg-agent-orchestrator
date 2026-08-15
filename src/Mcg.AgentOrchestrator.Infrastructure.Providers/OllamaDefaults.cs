namespace Mcg.AgentOrchestrator.Infrastructure;

public static class OllamaDefaults
{
    // 127.0.0.1 rather than localhost: Windows resolves localhost through an
    // IPv6 attempt first, and the fallback to IPv4 can exceed reachability
    // probe timeouts while Ollama listens on IPv4 only.
    public const string BaseUrl = "http://127.0.0.1:11434";
    public const string OpenAiModelsPath = "/v1/models";

    public static string ResolveBaseUrl()
    {
        return ResolveBaseUrl(Environment.GetEnvironmentVariable("OLLAMA_BASE_URL"));
    }

    public static string ResolveBaseUrl(string? configured)
    {
        return string.IsNullOrWhiteSpace(configured) ? BaseUrl : configured.Trim();
    }

    public static string BuildOpenAiModelsUrl(string baseUrl) =>
        $"{baseUrl.TrimEnd('/')}{OpenAiModelsPath}";
}
