using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Providers;

internal static class ProviderModelDefaults
{
    public const string OpenAi = "gpt-5.4-mini";
    public const string Anthropic = AgentCatalog.AnthropicComplexModelName;
    public const string Ollama = "qwen3:8b";
}
