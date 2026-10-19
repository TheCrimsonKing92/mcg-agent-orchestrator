using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Providers;

public static class ProviderRegistryFactory
{
    public static IModelProviderRegistry CreateDefaultProviders() =>
        CreateDefaultProviders(LocalInferenceProbe.IsReachable,
            OllamaDefaults.ResolveBaseUrl(), LlamaCppDefaults.ResolveBaseUrl(), out _);

    internal static IModelProviderRegistry CreateDefaultProviders(
        Func<string, bool> probe,
        string ollamaBaseUrl,
        string llamaBaseUrl,
        out bool llamaCppReachable)
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

        var ollamaModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? ProviderModelDefaults.Ollama;

        providers.Add(probe(OllamaDefaults.BuildOpenAiModelsUrl(ollamaBaseUrl))
            ? new ChatCompletionsModelProvider(ProviderHttpClientFactory.CreateOllamaClient(ollamaBaseUrl), ollamaModel, "Ollama")
            : new ScriptedModelProvider("Ollama"));

        var llamaModel = Environment.GetEnvironmentVariable("LLAMA_CPP_MODEL") ?? LlamaCppDefaults.DefaultModelAlias;
        llamaCppReachable = probe(LlamaCppDefaults.BuildOpenAiModelsUrl(llamaBaseUrl));
        providers.Add(llamaCppReachable
            ? new ChatCompletionsModelProvider(ProviderHttpClientFactory.CreateLlamaCppClient(llamaBaseUrl), llamaModel, "LlamaCpp")
            : new ScriptedModelProvider("LlamaCpp"));

        return new InMemoryModelProviderRegistry(providers);
    }

    public static bool IsOllamaReachable() =>
        IsOllamaReachable(OllamaDefaults.ResolveBaseUrl());

    public static bool IsOllamaReachable(string baseUrl)
    {
        return IsOpenAiCompatibleReachable(OllamaDefaults.BuildOpenAiModelsUrl(baseUrl));
    }

    public static bool IsLlamaCppReachable() =>
        IsLlamaCppReachable(LlamaCppDefaults.ResolveBaseUrl());

    public static bool IsLlamaCppReachable(string baseUrl)
    {
        return IsOpenAiCompatibleReachable(LlamaCppDefaults.BuildOpenAiModelsUrl(baseUrl));
    }

    private static bool IsOpenAiCompatibleReachable(string modelsUrl) =>
        LocalInferenceProbe.IsReachable(modelsUrl);
}

