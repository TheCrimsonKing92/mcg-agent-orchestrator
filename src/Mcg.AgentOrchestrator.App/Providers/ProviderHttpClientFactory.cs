using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Providers;

internal static class ProviderHttpClientFactory
{
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    };

    public static HttpClient CreateOpenAiClient()
    {
        return CreateClient(new Uri("https://api.openai.com/"));
    }

    public static HttpClient CreateAnthropicClient()
    {
        return CreateClient(new Uri("https://api.anthropic.com/"));
    }

    public static HttpClient CreateOllamaClient(string? baseUrl = null)
    {
        return CreateClient(new Uri(OllamaDefaults.ResolveBaseUrl(baseUrl)));
    }

    public static HttpClient CreateLlamaCppClient(string? baseUrl = null)
    {
        return CreateClient(new Uri(LlamaCppDefaults.ResolveBaseUrl(baseUrl)));
    }

    private static HttpClient CreateClient(Uri baseAddress)
    {
        return new HttpClient(SharedHandler, disposeHandler: false)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromMinutes(5)
        };
    }
}
