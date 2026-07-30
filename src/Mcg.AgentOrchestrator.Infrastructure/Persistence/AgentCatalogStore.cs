using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record AgentCatalog(IReadOnlyList<AgentDefinition> Agents)
{
    public const string OpenAiSubscriptionModelAlias = "gpt-5.5";
    public const string OpenAiSolSubscriptionModelAlias = "gpt-5.6-sol";
    public const string OpenAiTerraSubscriptionModelAlias = "gpt-5.6-terra";
    public const string OpenAiLunaSubscriptionModelAlias = "gpt-5.6-luna";
    public const string StaleOpenAiCodexSubscriptionModelAlias = "gpt-5.3-codex";
    public const int RoutineApiMaxOutputTokens = OutputTokenPolicy.RoutinePaidMaxOutputTokens;
    public const int ComplexApiMaxOutputTokens = OutputTokenPolicy.ComplexPaidMaxOutputTokens;
    public const string RoutineReasoningEffort = "medium";
    public const string RoutineSubscriptionReasoningEffort = "low";
    public const string ComplexReasoningEffort = "high";

    public static string? DefaultSubscriptionReasoningEffort(string providerName, string? modelAlias)
    {
        if (!providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return modelAlias is not null &&
            (modelAlias.Equals(OpenAiTerraSubscriptionModelAlias, StringComparison.OrdinalIgnoreCase) ||
             modelAlias.Equals(OpenAiLunaSubscriptionModelAlias, StringComparison.OrdinalIgnoreCase))
            ? "medium"
            : RoutineSubscriptionReasoningEffort;
    }

    public AgentDefinition GetRequired(AgentRole role)
    {
        return Agents.FirstOrDefault(agent => agent.Role == role)
            ?? throw new KeyNotFoundException($"Agent role '{role}' was not found.");
    }

    public AgentDefinition? FindById(string agentId)
    {
        return Agents.FirstOrDefault(agent => agent.Id.Value.Equals(agentId, StringComparison.OrdinalIgnoreCase));
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

    public AgentCatalog AddOrReplaceById(AgentDefinition agent)
    {
        var agents = Agents.ToList();
        var index = agents.FindIndex(existing => existing.Id == agent.Id);
        if (index >= 0)
        {
            agents[index] = agent;
        }
        else
        {
            agents.Add(agent);
        }

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
            new(new AgentId("openai-planner"), "OpenAI planner", AgentRole.Planner, OpenAiBase(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex(), ComplexModel: OpenAiComplex(), IsProviderRoutingConstrained: false),
            new(new AgentId("openai-ideation"), "OpenAI ideation", AgentRole.Ideation, OpenAiBase(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex(), ComplexModel: OpenAiComplex(), IsProviderRoutingConstrained: false),
            new(new AgentId("openai-researcher"), "OpenAI researcher", AgentRole.Researcher, OpenAiBase(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex(), ComplexModel: OpenAiComplex(), IsProviderRoutingConstrained: false),
            new(new AgentId("openai-developer"), "OpenAI developer", AgentRole.Developer, OpenAiBase(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex(), ComplexModel: OpenAiComplex(), IsProviderRoutingConstrained: false),
            new(new AgentId("openai-tester"), "OpenAI tester", AgentRole.Tester, OpenAiBase(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex(), ComplexModel: OpenAiComplex(), IsProviderRoutingConstrained: false),
            new(new AgentId("openai-reviewer"), "OpenAI reviewer", AgentRole.Reviewer, OpenAiBase(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex(), ComplexModel: OpenAiComplex(), IsProviderRoutingConstrained: false)
        ]);
    }

    public static AgentCatalog AnthropicDefault()
    {
        static ModelProfile Haiku() =>
            new("Anthropic", "claude-haiku-4-5", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, null, RoutineApiMaxOutputTokens);

        static ModelProfile Sonnet() =>
            new("Anthropic", "claude-sonnet-4-6", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, null, ComplexApiMaxOutputTokens);

        static SubscriptionLaunchProfile ClaudeCli() =>
            new("claude-cli");

        return new AgentCatalog(
        [
            new(new AgentId("anthropic-planner"), "Anthropic planner", AgentRole.Planner, Haiku(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: ClaudeCli(), ComplexModel: Sonnet(), IsProviderRoutingConstrained: false),
            new(new AgentId("anthropic-ideation"), "Anthropic ideation", AgentRole.Ideation, Haiku(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: ClaudeCli(), ComplexModel: Sonnet(), IsProviderRoutingConstrained: false),
            new(new AgentId("anthropic-researcher"), "Anthropic researcher", AgentRole.Researcher, Haiku(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: ClaudeCli(), ComplexModel: Sonnet(), IsProviderRoutingConstrained: false),
            new(new AgentId("anthropic-developer"), "Anthropic developer", AgentRole.Developer, Haiku(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: ClaudeCli(), ComplexModel: Sonnet(), IsProviderRoutingConstrained: false),
            new(new AgentId("anthropic-tester"), "Anthropic tester", AgentRole.Tester, Haiku(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: ClaudeCli(), ComplexModel: Sonnet(), IsProviderRoutingConstrained: false),
            new(new AgentId("anthropic-reviewer"), "Anthropic reviewer", AgentRole.Reviewer, Haiku(), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: ClaudeCli(), ComplexModel: Sonnet(), IsProviderRoutingConstrained: false),
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
            new(new AgentId("ollama-planner"), "Ollama planner", AgentRole.Planner, Coder(OutputTokenPolicy.RoutineLocalMaxOutputTokens), ComplexModel: Qwen3(OutputTokenPolicy.ComplexLocalMaxOutputTokens), IsProviderRoutingConstrained: false),
            new(new AgentId("ollama-ideation"), "Ollama ideation", AgentRole.Ideation, Coder(OutputTokenPolicy.RoutineLocalMaxOutputTokens), ComplexModel: Qwen3(OutputTokenPolicy.ComplexLocalMaxOutputTokens), IsProviderRoutingConstrained: false),
            new(new AgentId("ollama-researcher"), "Ollama researcher", AgentRole.Researcher, Coder(OutputTokenPolicy.RoutineLocalMaxOutputTokens), ComplexModel: Qwen3(OutputTokenPolicy.ComplexLocalMaxOutputTokens), IsProviderRoutingConstrained: false),
            new(new AgentId("ollama-developer"), "Ollama developer", AgentRole.Developer, Coder(OutputTokenPolicy.RoutineLocalMaxOutputTokens), ComplexModel: Qwen3(OutputTokenPolicy.ComplexLocalMaxOutputTokens), IsProviderRoutingConstrained: false),
            new(new AgentId("ollama-tester"), "Ollama tester", AgentRole.Tester, Coder(OutputTokenPolicy.RoutineLocalMaxOutputTokens), ComplexModel: Qwen3(OutputTokenPolicy.ComplexLocalMaxOutputTokens), IsProviderRoutingConstrained: false),
            new(new AgentId("ollama-reviewer"), "Ollama reviewer", AgentRole.Reviewer, Coder(OutputTokenPolicy.RoutineLocalMaxOutputTokens), ComplexModel: Qwen3(OutputTokenPolicy.ComplexLocalMaxOutputTokens), IsProviderRoutingConstrained: false)
        ]);
    }
}

public static class AgentCatalogStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AgentCatalog Load(string path, AgentCatalog? fallback = null)
    {
        var defaultCatalog = fallback ?? AgentCatalog.Default();

        if (!File.Exists(path))
        {
            return NormalizePaidProviderCaps(defaultCatalog, defaultCatalog);
        }

        var catalog = TryDeserialize(path);
        if (catalog is not null && catalog.Agents.Count > 0)
        {
            return NormalizePaidProviderCaps(catalog, defaultCatalog);
        }

        var bak = path + ".bak";
        if (File.Exists(bak))
        {
            Console.Error.WriteLine($"[AgentCatalogStore] WARNING: '{Path.GetFileName(path)}' is corrupt or empty; recovering from backup.");
            var bakCatalog = TryDeserialize(bak);
            if (bakCatalog is not null && bakCatalog.Agents.Count > 0)
            {
                return NormalizePaidProviderCaps(bakCatalog, defaultCatalog);
            }
            Console.Error.WriteLine("[AgentCatalogStore] WARNING: backup is also corrupt; falling back to built-in defaults.");
        }
        else
        {
            Console.Error.WriteLine($"[AgentCatalogStore] WARNING: '{Path.GetFileName(path)}' is corrupt or empty and no backup exists; falling back to built-in defaults.");
        }

        return NormalizePaidProviderCaps(defaultCatalog, defaultCatalog);
    }

    public static void Save(string path, AgentCatalog catalog)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        var bak = path + ".bak";
        File.WriteAllText(tmp, JsonSerializer.Serialize(catalog, _jsonOptions));
        if (File.Exists(path))
            File.Replace(tmp, path, bak);
        else
            File.Move(tmp, path);
    }

    private static AgentCatalog? TryDeserialize(string path)
    {
        try { return JsonSerializer.Deserialize<AgentCatalog>(File.ReadAllText(path), _jsonOptions); }
        catch { return null; }
    }

    private static AgentCatalog NormalizePaidProviderCaps(AgentCatalog catalog, AgentCatalog defaultCatalog)
    {
        var builtInRoutingCandidates = AgentCatalog.Default().Agents
            .Concat(AgentCatalog.AnthropicDefault().Agents)
            .Concat(AgentCatalog.OllamaDefault().Agents)
            .Concat(defaultCatalog.Agents)
            .ToList();
        var normalizedAgents = new List<AgentDefinition>(catalog.Agents.Count);
        var repairedAutomaticCount = 0;
        var repairedConstrainedCount = 0;
        foreach (var agent in catalog.Agents)
        {
            var normalized = NormalizeAgent(agent, builtInRoutingCandidates);
            normalizedAgents.Add(normalized);
            if (agent.IsProviderRoutingConstrained is null)
            {
                if (normalized.IsProviderRoutingConstrained == true)
                {
                    repairedConstrainedCount++;
                }
                else
                {
                    repairedAutomaticCount++;
                }
            }
        }

        if (repairedAutomaticCount + repairedConstrainedCount > 0)
        {
            Console.Error.WriteLine(
                $"[AgentCatalogStore] WARNING: migrated legacy provider-routing settings: " +
                $"{repairedAutomaticCount} built-in-compatible assignment(s) remain automatic; " +
                $"{repairedConstrainedCount} customized assignment(s) are provider-constrained. " +
                "Run 'config agents' to inspect provider-routing.");
        }

        return new AgentCatalog(normalizedAgents);
    }

    private static AgentDefinition NormalizeAgent(
        AgentDefinition agent,
        IReadOnlyList<AgentDefinition> builtInRoutingCandidates)
    {
        var normalized = NormalizeAgentConfiguration(agent);

        if (normalized.IsProviderRoutingConstrained is not null)
        {
            return normalized;
        }

        var matchesBuiltInRouting = builtInRoutingCandidates
            .Where(candidate => candidate.Id == normalized.Id && candidate.Role == normalized.Role)
            .Any(candidate => RoutingConfigurationMatches(
                normalized,
                NormalizeAgentConfiguration(candidate)));
        return normalized with { IsProviderRoutingConstrained = !matchesBuiltInRouting };
    }

    private static AgentDefinition NormalizeAgentConfiguration(AgentDefinition agent)
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

    private static bool RoutingConfigurationMatches(AgentDefinition current, AgentDefinition desired)
    {
        return current.Model == desired.Model &&
            current.ExecutionPolicy == desired.ExecutionPolicy &&
            current.Subscription == desired.Subscription &&
            current.ComplexModel == desired.ComplexModel;
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
            ? subscription with { ReasoningEffort = AgentCatalog.DefaultSubscriptionReasoningEffort(agent.Model.ProviderName, subscription.ModelAlias) }
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
