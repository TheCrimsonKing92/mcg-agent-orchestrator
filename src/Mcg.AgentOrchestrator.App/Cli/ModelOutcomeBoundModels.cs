using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ModelOutcomeBoundModels
{
    private static readonly JsonSerializerOptions CatalogOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static BoundModelSet Load(string agentCatalogPath, string modelFunctionCatalogPath)
    {
        var path = agentCatalogPath;
        try
        {
            // Stores deliberately hide read failures behind defaults/backups. Validate the
            // primary catalogs first so reporting never treats those fallbacks as membership.
            if (!File.Exists(path))
                return BoundModelSet.Unavailable($"{Path.GetFileName(path)} not found at {path}");
            var primaryAgents = JsonSerializer.Deserialize<AgentCatalog>(File.ReadAllText(path), CatalogOptions);
            if (primaryAgents?.Agents is null || primaryAgents.Agents.Any(agent =>
                    agent?.Model?.ModelName is null))
                throw new JsonException("Expected an agent catalog with an Agents array and model names.");

            path = modelFunctionCatalogPath;
            if (!File.Exists(path))
                return BoundModelSet.Unavailable($"{Path.GetFileName(path)} not found at {path}");
            var primaryFunctions = JsonSerializer.Deserialize<ModelFunctionCatalog>(File.ReadAllText(path), CatalogOptions);
            if (primaryFunctions?.Bindings is null || primaryFunctions.Bindings.Any(binding =>
                    binding?.Model?.ModelName is null))
                throw new JsonException("Expected a model-function catalog with a Bindings array and model names.");

            path = agentCatalogPath;
            var agents = AgentCatalogStore.Load(path, new AgentCatalog([]));
            path = modelFunctionCatalogPath;
            var functions = ModelFunctionCatalogStore.Load(path);
            // An empty primary agent catalog is valid here, even if the store recovered a backup.
            return BoundModelSet.FromCatalogs(primaryAgents.Agents.Count == 0 ? [] : agents.Agents, functions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return BoundModelSet.Unavailable($"{Path.GetFileName(path)} unreadable at {path}: {exception.Message}");
        }
    }

    public static void Print(IReadOnlyList<ModelOutcomeRecord> records, OrchestratorWorkspace workspace)
    {
        var boundModels = Load(workspace.AgentCatalogPath, workspace.ModelFunctionCatalogPath);
        ConsoleViews.PrintModelOutcomeScorecard(ModelOutcomeScorecard.ApplyBoundModels(records, boundModels), boundModels);
    }
}
