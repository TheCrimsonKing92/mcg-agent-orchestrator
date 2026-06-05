using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record AgentCatalog(IReadOnlyList<AgentDefinition> Agents)
{
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
        static ModelProfile OpenAi(string reasoningEffort) =>
            new("OpenAI", "gpt-5.5", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, reasoningEffort);

        static SubscriptionLaunchProfile Codex(string reasoningEffort) =>
            new("codex-cli", "gpt-5.5", reasoningEffort);

        return new AgentCatalog(
        [
            new(new AgentId("openai-planner"), "OpenAI planner", AgentRole.Planner, OpenAi("high"), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex("high")),
            new(new AgentId("openai-researcher"), "OpenAI researcher", AgentRole.Researcher, OpenAi("high"), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex("high")),
            new(new AgentId("openai-developer"), "OpenAI developer", AgentRole.Developer, OpenAi("medium"), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex("medium")),
            new(new AgentId("openai-tester"), "OpenAI tester", AgentRole.Tester, OpenAi("high"), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex("high")),
            new(new AgentId("openai-reviewer"), "OpenAI reviewer", AgentRole.Reviewer, OpenAi("high"), ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, Subscription: Codex("high"))
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
            new(new AgentId("ollama-planner"), "Ollama planner", AgentRole.Planner, Qwen3(8192)),
            new(new AgentId("ollama-researcher"), "Ollama researcher", AgentRole.Researcher, Qwen3(8192)),
            new(new AgentId("ollama-developer"), "Ollama developer", AgentRole.Developer, Coder(2048)),
            new(new AgentId("ollama-tester"), "Ollama tester", AgentRole.Tester, Qwen3(8192)),
            new(new AgentId("ollama-reviewer"), "Ollama reviewer", AgentRole.Reviewer, Qwen3(8192))
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
