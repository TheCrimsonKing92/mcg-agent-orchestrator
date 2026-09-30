using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

internal sealed record DashboardAgentOption(string Value, string Label);

internal sealed record DashboardAgentProviderOptions(
    IReadOnlyList<DashboardAgentOption> ApiModels,
    IReadOnlyList<DashboardAgentOption> ApiReasoning,
    IReadOnlyList<DashboardAgentOption> SubscriptionModels,
    IReadOnlyList<DashboardAgentOption> SubscriptionReasoning,
    IReadOnlyDictionary<string, IReadOnlyList<DashboardAgentOption>> SubscriptionReasoningByModel,
    IReadOnlyDictionary<string, string> DefaultSubscriptionReasoningByModel,
    string DefaultSubscriptionModel,
    string PreferredProfile);

internal static class DashboardAgentOptionCatalog
{
    private static readonly DashboardAgentOption Default = new("", "Default");
    private static readonly DashboardAgentOption UseApiModel = new("", "Use API model");

    private static readonly IReadOnlyList<DashboardAgentOption> OpenAiSubscriptionReasoning =
    [
        Default,
        new("low", "Low"),
        new("medium", "Medium"),
        new("high", "High"),
        new("xhigh", "Extra high"),
        new("max", "Max"),
        new("ultra", "Ultra")
    ];

    private static readonly IReadOnlyList<DashboardAgentOption> OpenAiLunaSubscriptionReasoning =
        OpenAiSubscriptionReasoning.Where(option => !option.Value.Equals("ultra", StringComparison.OrdinalIgnoreCase)).ToList();

    private static readonly Dictionary<string, DashboardAgentProviderOptions> Providers =
        new Dictionary<string, DashboardAgentProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenAI"] = new(
                ApiModels:
                [
                    new("gpt-5.4-mini", "GPT-5.4 mini"),
                    new("gpt-5.4", "GPT-5.4"),
                    new("gpt-5.4-pro", "GPT-5.4 pro"),
                    new("gpt-5.5", "GPT-5.5"),
                    new("gpt-5.5-pro", "GPT-5.5 pro"),
                    new("gpt-5.4-nano", "GPT-5.4 nano"),
                    new("gpt-5-mini", "GPT-5 mini"),
                    new("gpt-5-nano", "GPT-5 nano"),
                    new("gpt-5.2", "GPT-5.2 (previous)"),
                    new(AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias, "GPT-6.1 Sol"),
                    new(AgentCatalog.OpenAiGpt6SolSubscriptionModelAlias, "GPT-6 Sol"),
                    new(AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias, "GPT-6 Luna"),
                    new(AgentCatalog.OpenAiGpt6AstraSubscriptionModelAlias, "GPT-6 Astra")
                ],
                ApiReasoning:
                [
                    Default,
                    new("none", "None"),
                    new("low", "Low"),
                    new("medium", "Medium"),
                    new("high", "High"),
                    new("xhigh", "Extra high")
                ],
                SubscriptionModels:
                [
                    new(AgentCatalog.OpenAiSolSubscriptionModelAlias, "GPT-5.6 Sol"),
                    new(AgentCatalog.OpenAiTerraSubscriptionModelAlias, "GPT-5.6 Terra"),
                    new(AgentCatalog.OpenAiLunaSubscriptionModelAlias, "GPT-5.6 Luna"),
                    new(AgentCatalog.OpenAiSubscriptionModelAlias, "GPT-5.5"),
                    new(AgentCatalog.OpenAiGpt6SolSubscriptionModelAlias, "GPT-6 Sol"),
                    new(AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias, "GPT-6 Luna"),
                    new(AgentCatalog.OpenAiGpt6AstraSubscriptionModelAlias, "GPT-6 Astra"),
                    new(AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias, "GPT-6.1 Sol"),
                    UseApiModel
                ],
                SubscriptionReasoning: OpenAiSubscriptionReasoning,
                SubscriptionReasoningByModel: new Dictionary<string, IReadOnlyList<DashboardAgentOption>>(StringComparer.OrdinalIgnoreCase)
                {
                    [AgentCatalog.OpenAiSolSubscriptionModelAlias] = OpenAiSubscriptionReasoning,
                    [AgentCatalog.OpenAiTerraSubscriptionModelAlias] = OpenAiSubscriptionReasoning,
                    [AgentCatalog.OpenAiLunaSubscriptionModelAlias] = OpenAiLunaSubscriptionReasoning,
                    [AgentCatalog.OpenAiGpt6SolSubscriptionModelAlias] = OpenAiSubscriptionReasoning,
                    [AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias] = OpenAiSubscriptionReasoning,
                    [AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias] = OpenAiLunaSubscriptionReasoning,
                    [AgentCatalog.OpenAiGpt6AstraSubscriptionModelAlias] = OpenAiSubscriptionReasoning
                },
                DefaultSubscriptionReasoningByModel: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [AgentCatalog.OpenAiSubscriptionModelAlias] = AgentCatalog.RoutineSubscriptionReasoningEffort,
                    [AgentCatalog.OpenAiSolSubscriptionModelAlias] = AgentCatalog.RoutineSubscriptionReasoningEffort,
                    [AgentCatalog.OpenAiTerraSubscriptionModelAlias] = "medium",
                    [AgentCatalog.OpenAiLunaSubscriptionModelAlias] = "medium",
                    [AgentCatalog.OpenAiGpt6SolSubscriptionModelAlias] = "medium",
                    [AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias] = "medium",
                    [AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias] = "medium",
                    [AgentCatalog.OpenAiGpt6AstraSubscriptionModelAlias] = "medium"
                },
                DefaultSubscriptionModel: AgentCatalog.OpenAiSubscriptionModelAlias,
                PreferredProfile: "codex-cli"),
            ["Anthropic"] = new(
                ApiModels:
                [
                    new("claude-sonnet-4-6", "Claude Sonnet 4.6"),
                    new("claude-haiku-4-5", "Claude Haiku 4.5"),
                    new("claude-opus-4-8", "Claude Opus 4.8"),
                    new("claude-opus-5", "Claude Opus 5"),
                    new("claude-opus-5-5", "Claude Opus 5.5"),
                    new(AgentCatalog.AnthropicComplexModelName, "Claude Sonnet 5.5"),
                    new("claude-sonnet-5", "Claude Sonnet 5"),
                    new("claude-fable-5-1", "Claude Fable 5.1")
                ],
                ApiReasoning: [Default],
                SubscriptionModels:
                [
                    UseApiModel,
                    new("sonnet", "Claude Sonnet (latest)"),
                    new("haiku", "Claude Haiku (latest)"),
                    new("opus", "Claude Opus (latest)"),
                    new("opus-5", "Claude Opus 5 (pinned)")
                ],
                SubscriptionReasoning: [Default],
                SubscriptionReasoningByModel: new Dictionary<string, IReadOnlyList<DashboardAgentOption>>(StringComparer.OrdinalIgnoreCase),
                DefaultSubscriptionReasoningByModel: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                DefaultSubscriptionModel: string.Empty,
                PreferredProfile: "claude-cli"),
            ["Ollama"] = new(
                ApiModels:
                [
                    new("qwen2.5-coder:7b", "Qwen2.5 Coder 7B"),
                    new("qwen3:8b", "Qwen3 8B")
                ],
                ApiReasoning: [Default],
                SubscriptionModels: [new("", "No subscription model")],
                SubscriptionReasoning: [Default],
                SubscriptionReasoningByModel: new Dictionary<string, IReadOnlyList<DashboardAgentOption>>(StringComparer.OrdinalIgnoreCase),
                DefaultSubscriptionReasoningByModel: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                DefaultSubscriptionModel: string.Empty,
                PreferredProfile: string.Empty),
            ["xAI"] = new(
                ApiModels:
                [
                    new(AgentCatalog.XaiSubscriptionModelAlias, AgentCatalog.XaiSubscriptionModelAlias),
                    new("grok-4.7-build-fast", "grok-4.7-build-fast"),
                    new("grok-4.6", "grok-4.6"),
                    new("grok-4.5", "grok-4.5")
                ],
                ApiReasoning: [Default],
                SubscriptionModels:
                [
                    new(AgentCatalog.XaiSubscriptionModelAlias, AgentCatalog.XaiSubscriptionModelAlias),
                    new("grok-4.7-build-fast", "grok-4.7-build-fast"),
                    new("grok-4.6", "grok-4.6"),
                    new("grok-4.5", "grok-4.5")
                ],
                SubscriptionReasoning: [Default],
                SubscriptionReasoningByModel: new Dictionary<string, IReadOnlyList<DashboardAgentOption>>(StringComparer.OrdinalIgnoreCase),
                DefaultSubscriptionReasoningByModel: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                DefaultSubscriptionModel: AgentCatalog.XaiSubscriptionModelAlias,
                PreferredProfile: WorkerProfileDispatcher.XaiSubscriptionProfileName)
        };

    public static DashboardAgentProviderOptions ForProvider(string provider)
    {
        return Providers.TryGetValue(provider, out var options)
            ? options
            : Providers["OpenAI"];
    }

    public static IReadOnlyList<DashboardAgentOption> ApiModelOptions(string provider) =>
        ForProvider(provider).ApiModels;

    public static IReadOnlyList<DashboardAgentOption> ApiReasoningOptions(string provider) =>
        ForProvider(provider).ApiReasoning;

    public static IReadOnlyList<DashboardAgentOption> SubscriptionModelOptions(string provider) =>
        ForProvider(provider).SubscriptionModels;

    public static IReadOnlyList<DashboardAgentOption> SubscriptionReasoningOptions(string provider, string? modelAlias)
    {
        var options = ForProvider(provider);
        return modelAlias is not null && options.SubscriptionReasoningByModel.TryGetValue(modelAlias, out var modelOptions)
            ? modelOptions
            : options.SubscriptionReasoning;
    }

    public static string DefaultSubscriptionProfile(string provider) =>
        ForProvider(provider).PreferredProfile;

    public static string DefaultSubscriptionModelAlias(string provider) =>
        ForProvider(provider).DefaultSubscriptionModel;

    public static string ToJavaScriptObjectLiteral()
    {
        var html = new StringBuilder();
        html.AppendLine("{");
        var firstProvider = true;
        foreach (var provider in Providers)
        {
            if (!firstProvider)
            {
                html.AppendLine(",");
            }

            firstProvider = false;
            html.AppendLine($"  {provider.Key}: {{");
            html.AppendLine($"    apiModels: {RenderOptions(provider.Value.ApiModels)},");
            html.AppendLine($"    apiReasoning: {RenderOptions(provider.Value.ApiReasoning)},");
            html.AppendLine($"    subscriptionModels: {RenderOptions(provider.Value.SubscriptionModels)},");
            html.AppendLine($"    subscriptionReasoning: {RenderOptions(provider.Value.SubscriptionReasoning)},");
            html.AppendLine($"    subscriptionReasoningByModel: {RenderOptionsMap(provider.Value.SubscriptionReasoningByModel)},");
            html.AppendLine($"    defaultSubscriptionReasoningByModel: {RenderStringMap(provider.Value.DefaultSubscriptionReasoningByModel)},");
            html.AppendLine($"    defaultSubscriptionModel: '{Escape(provider.Value.DefaultSubscriptionModel)}',");
            html.Append($"    preferredProfile: '{Escape(provider.Value.PreferredProfile)}'");
            html.AppendLine();
            html.Append("  ");
            html.Append('}');
        }

        html.AppendLine();
        html.Append('}');
        return html.ToString();
    }

    private static string RenderOptions(IReadOnlyList<DashboardAgentOption> options) =>
        "[" + string.Join(", ", options.Select(option => $"['{Escape(option.Value)}','{Escape(option.Label)}']")) + "]";

    private static string RenderOptionsMap(IReadOnlyDictionary<string, IReadOnlyList<DashboardAgentOption>> optionsByModel) =>
        "{" + string.Join(", ", optionsByModel.Select(item => $"'{Escape(item.Key.ToLowerInvariant())}': {RenderOptions(item.Value)}")) + "}";

    private static string RenderStringMap(IReadOnlyDictionary<string, string> values) =>
        "{" + string.Join(", ", values.Select(item => $"'{Escape(item.Key.ToLowerInvariant())}': '{Escape(item.Value)}'")) + "}";

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
}
