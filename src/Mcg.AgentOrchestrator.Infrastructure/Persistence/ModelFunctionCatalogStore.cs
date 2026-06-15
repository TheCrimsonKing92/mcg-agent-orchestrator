using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Persists the orchestrator-internal model-function registry (semantic-acceptance judge lanes today;
// future samplers/summarizers/oracles). Mirrors AgentCatalogStore's JSON shape but is a SEPARATE
// store — these bindings are not worker agents and must never leak into task routing or the SDLC
// role catalog. Absent/empty file => an empty catalog (the feature stays dormant).
public static class ModelFunctionCatalogStore
{
    public static ModelFunctionCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            return ModelFunctionCatalog.Empty;
        }

        var catalog = JsonSerializer.Deserialize<ModelFunctionCatalog>(File.ReadAllText(path), JsonOptions());
        return catalog is null || catalog.Bindings.Count == 0 ? ModelFunctionCatalog.Empty : catalog;
    }

    public static void Save(string path, ModelFunctionCatalog catalog)
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
