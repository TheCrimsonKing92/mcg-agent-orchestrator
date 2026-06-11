using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record AgentCatalog(IReadOnlyList<AgentDefinition> Agents)
{
    public const string OpenAiSubscriptionModelAlias = "gpt-5.5";
    public const string StaleOpenAiCodexSubscriptionModelAlias = "gpt-5.3-codex";
    public const int RoutineApiMaxOutputTokens = 2048;
    public const int ComplexApiMaxOutputTokens = 4096;
    public const string RoutineReasoningEffort = "medium";
    public const string RoutineSubscriptionReasoningEffort = "low";
    public const string ComplexReasoningEffort = "high";

    public AgentDefinition GetRequired(AgentRole role)
    {
        return Agents.FirstOrDefault(agent => agent.Role == role)
            ?? throw new KeyNotFoundException($"Agent role '{role}' was not found.");
    }

    public AgentCatalog UpsertRole(AgentDefinition agent)
    {
        var agents = Agents
            .Where(existing => existing.Role != agent.Role)
            .Append(agent)
            .OrderBy(existing => existing.Role)
            .ToList();

        return new AgentCatalog(agents);
    }

    public static AgentCatalog Default()
    {
        static ModelProfile OpenAiBase() =>
            new("OpenAI", "gpt-5.4-mini", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, RoutineReasoningEffort, RoutineApiMaxOutputTokens);

        static ModelProfile OpenAiComplex() =>
            new("OpenAI", "gpt-5.5", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, ComplexReasoningEffort, ComplexApiMaxOutputTokens);

        static SubscriptionLaunchProfile Codex() =>
            new("codex-cli", OpenAiSubscriptionModelAlias, RoutineSubscriptionReasoningEffort);

        return new AgentCatalog(
        [
            new(new AgentId("openai-planner"), "OpenAI planner", AgentRole.Planner, OpenAiBase(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex(), ComplexModel: OpenAiComplex()),
            new(new AgentId("openai-researcher"), "OpenAI researcher", AgentRole.Researcher, OpenAiBase(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex(), ComplexModel: OpenAiComplex()),
            new(new AgentId("openai-developer"), "OpenAI developer", AgentRole.Developer, OpenAiBase(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex(), ComplexModel: OpenAiComplex()),
            new(new AgentId("openai-tester"), "OpenAI tester", AgentRole.Tester, OpenAiBase(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex(), ComplexModel: OpenAiComplex()),
            new(new AgentId("openai-reviewer"), "OpenAI reviewer", AgentRole.Reviewer, OpenAiBase(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex(), ComplexModel: OpenAiComplex())
        ]);
    }

    public static AgentCatalog OllamaDefault()
    {
        static ModelProfile Qwen3(int maxOutputTokens) =>
            new("Ollama", "qwen3:8b", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.LocalBridge, MaxOutputTokens: maxOutputTokens);

        static ModelProfile Coder(int maxOutputTokens) =>
            new("Ollama", "qwen2.5-coder:7b", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.LocalBridge, MaxOutputTokens: maxOutputTokens);

        return new AgentCatalog(
        [
            new(new AgentId("ollama-planner"), "Ollama planner", AgentRole.Planner, Coder(2048), ComplexModel: Qwen3(8192)),
            new(new AgentId("ollama-researcher"), "Ollama researcher", AgentRole.Researcher, Coder(2048), ComplexModel: Qwen3(8192)),
            new(new AgentId("ollama-developer"), "Ollama developer", AgentRole.Developer, Coder(2048), ComplexModel: Qwen3(8192)),
            new(new AgentId("ollama-tester"), "Ollama tester", AgentRole.Tester, Coder(2048), ComplexModel: Qwen3(8192)),
            new(new AgentId("ollama-reviewer"), "Ollama reviewer", AgentRole.Reviewer, Coder(2048), ComplexModel: Qwen3(8192))
        ]);
    }
}

public static class AgentCatalogStore
{
    public static AgentCatalog Load(string path, AgentCatalog? fallback = null)
    {
        var defaultCatalog = fallback ?? AgentCatalog.Default();

        if (!File.Exists(path))
        {
            return NormalizePaidProviderCaps(defaultCatalog);
        }

        var catalog = JsonSerializer.Deserialize<AgentCatalog>(File.ReadAllText(path), JsonOptions());
        return NormalizePaidProviderCaps(catalog is null || catalog.Agents.Count == 0 ? defaultCatalog : catalog);
    }

    public static void Save(string path, AgentCatalog catalog)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(catalog, JsonOptions()));
    }

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static AgentCatalog NormalizePaidProviderCaps(AgentCatalog catalog)
    {
        return new AgentCatalog(catalog.Agents.Select(NormalizeAgent).ToList());
    }

    private static AgentDefinition NormalizeAgent(AgentDefinition agent)
    {
        return agent with
        {
            Model = NormalizeModel(agent.Model, AgentCatalog.RoutineApiMaxOutputTokens, AgentCatalog.RoutineReasoningEffort),
            ComplexModel = agent.ComplexModel is null
                ? null
                : NormalizeModel(agent.ComplexModel, AgentCatalog.ComplexApiMaxOutputTokens, AgentCatalog.ComplexReasoningEffort),
            Subscription = NormalizeSubscription(agent)
        };
    }

    private static ModelProfile NormalizeModel(ModelProfile model, int defaultMaxOutputTokens, string defaultReasoningEffort)
    {
        if (!IsPaidProvider(model.ProviderName))
        {
            return model;
        }

        return model with
        {
            MaxOutputTokens = model.MaxOutputTokens ?? defaultMaxOutputTokens,
            ReasoningEffort = string.IsNullOrWhiteSpace(model.ReasoningEffort)
                ? defaultReasoningEffort
                : model.ReasoningEffort
        };
    }

    private static SubscriptionLaunchProfile? NormalizeSubscription(AgentDefinition agent)
    {
        if (agent.Subscription is null || !AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy))
        {
            return agent.Subscription;
        }

        var subscription = agent.Subscription;
        if (IsOpenAiCodexSubscription(agent, subscription) &&
            string.Equals(subscription.ModelAlias, AgentCatalog.StaleOpenAiCodexSubscriptionModelAlias, StringComparison.OrdinalIgnoreCase))
        {
            subscription = subscription with { ModelAlias = AgentCatalog.OpenAiSubscriptionModelAlias };
        }

        return string.IsNullOrWhiteSpace(subscription.ReasoningEffort) && IsPaidProvider(agent.Model.ProviderName)
            ? subscription with { ReasoningEffort = AgentCatalog.RoutineSubscriptionReasoningEffort }
            : subscription;
    }

    private static bool IsOpenAiCodexSubscription(AgentDefinition agent, SubscriptionLaunchProfile subscription)
    {
        return agent.Model.ProviderName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) &&
            subscription.WorkerProfileName.Equals("codex-cli", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPaidProvider(string providerName)
    {
        return providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
    }
}
