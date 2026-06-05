using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Providers;

internal static class ProviderRegistryFactory
{
    public static IModelProviderRegistry CreateDefaultProviders()
    {
        var providers = new List<IModelProvider>();
        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        providers.Add(string.IsNullOrWhiteSpace(openAiKey)
            ? new ScriptedModelProvider("OpenAI")
            : new OpenAiResponsesModelProvider(ProviderHttpClientFactory.CreateOpenAiClient(), openAiKey, Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-5.5"));

        providers.Add(string.IsNullOrWhiteSpace(anthropicKey)
            ? new ScriptedModelProvider("Anthropic")
            : new AnthropicMessagesModelProvider(ProviderHttpClientFactory.CreateAnthropicClient(), anthropicKey, Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-sonnet-4-20250514"));

        var ollamaBaseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434";
        var ollamaModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen3:8b";

        providers.Add(IsOllamaReachable(ollamaBaseUrl)
            ? new ChatCompletionsModelProvider(ProviderHttpClientFactory.CreateOllamaClient(ollamaBaseUrl), ollamaModel, "Ollama")
            : new ScriptedModelProvider("Ollama"));

        return new InMemoryModelProviderRegistry(providers);
    }

    private static bool IsOllamaReachable(string baseUrl)
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = probe.GetAsync($"{baseUrl.TrimEnd('/')}/api/version").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}


