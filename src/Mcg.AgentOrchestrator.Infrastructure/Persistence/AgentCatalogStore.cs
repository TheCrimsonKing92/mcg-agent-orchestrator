using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record AgentCatalog(IReadOnlyList<AgentDefinition> Agents)
{
    public const int RoutineApiMaxOutputTokens = 1024;
    public const int ComplexApiMaxOutputTokens = 1200;

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
        static ModelProfile OpenAiBase(string reasoningEffort) =>
            new("OpenAI", "gpt-5.4-mini", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, reasoningEffort, RoutineApiMaxOutputTokens);

        static ModelProfile OpenAiComplex(string reasoningEffort) =>
            new("OpenAI", "gpt-5.5", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, reasoningEffort, ComplexApiMaxOutputTokens);

        static SubscriptionLaunchProfile Codex(string reasoningEffort) =>
            new("codex-cli", "gpt-5.3-codex", reasoningEffort);

        return new AgentCatalog(
        [
            new(new AgentId("openai-planner"), "OpenAI planner", AgentRole.Planner, OpenAiBase("high"), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex("high"), ComplexModel: OpenAiComplex("high")),
            new(new AgentId("openai-researcher"), "OpenAI researcher", AgentRole.Researcher, OpenAiBase("high"), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex("high"), ComplexModel: OpenAiComplex("high")),
            new(new AgentId("openai-developer"), "OpenAI developer", AgentRole.Developer, OpenAiBase("medium"), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex("medium"), ComplexModel: OpenAiComplex("medium")),
            new(new AgentId("openai-tester"), "OpenAI tester", AgentRole.Tester, OpenAiBase("high"), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex("high"), ComplexModel: OpenAiComplex("high")),
            new(new AgentId("openai-reviewer"), "OpenAI reviewer", AgentRole.Reviewer, OpenAiBase("high"), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex("high"), ComplexModel: OpenAiComplex("high"))
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
            return defaultCatalog;
        }

        var catalog = JsonSerializer.Deserialize<AgentCatalog>(File.ReadAllText(path), JsonOptions());
        return catalog is null || catalog.Agents.Count == 0 ? defaultCatalog : catalog;
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
}
