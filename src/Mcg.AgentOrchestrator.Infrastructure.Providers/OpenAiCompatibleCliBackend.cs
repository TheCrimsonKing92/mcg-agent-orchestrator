namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Resolves the OpenAI-compatible backend (what) for file-agent CLIs such as qwen-code (how).
/// </summary>
public static class OpenAiCompatibleCliBackend
{
    public static bool UsesQwenCodeHarness(string? providerName) =>
        IsLlamaCpp(providerName) || IsOllama(providerName);

    public static string ResolveBaseUrl(string? providerName)
    {
        if (IsOllama(providerName))
        {
            return OllamaDefaults.BuildOpenAiCompatibleBaseUrl(OllamaDefaults.ResolveBaseUrl());
        }

        return LlamaCppDefaults.BuildOpenAiCompatibleBaseUrl(LlamaCppDefaults.ResolveBaseUrl());
    }

    public static string ResolveApiKey(string? providerName)
    {
        return IsOllama(providerName)
            ? OllamaDefaults.OpenAiApiKey
            : LlamaCppDefaults.OpenAiApiKey;
    }

    private static bool IsLlamaCpp(string? providerName) =>
        providerName is not null &&
        providerName.Equals("LlamaCpp", StringComparison.OrdinalIgnoreCase);

    private static bool IsOllama(string? providerName) =>
        providerName is not null &&
        providerName.Equals("Ollama", StringComparison.OrdinalIgnoreCase);
}
