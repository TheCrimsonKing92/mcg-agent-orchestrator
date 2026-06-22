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
            : new OpenAiResponsesModelProvider(ProviderHttpClientFactory.CreateOpenAiClient(), openAiKey, Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? ProviderModelDefaults.OpenAi));

        providers.Add(string.IsNullOrWhiteSpace(anthropicKey)
            ? new ScriptedModelProvider("Anthropic")
            : new AnthropicMessagesModelProvider(ProviderHttpClientFactory.CreateAnthropicClient(), anthropicKey, Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? ProviderModelDefaults.Anthropic));

        var ollamaBaseUrl = OllamaDefaults.ResolveBaseUrl();
        var ollamaModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? ProviderModelDefaults.Ollama;

        providers.Add(IsOllamaReachable(ollamaBaseUrl)
            ? new ChatCompletionsModelProvider(ProviderHttpClientFactory.CreateOllamaClient(ollamaBaseUrl), ollamaModel, "Ollama")
            : new ScriptedModelProvider("Ollama"));

        return new InMemoryModelProviderRegistry(providers);
    }

    public static bool IsOllamaReachable() =>
        IsOllamaReachable(OllamaDefaults.ResolveBaseUrl());

    public static bool IsOllamaReachable(string baseUrl)
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = probe.GetAsync(OllamaDefaults.BuildOpenAiModelsUrl(baseUrl)).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}


