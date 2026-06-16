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

        ModelFunctionCatalog? catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<ModelFunctionCatalog>(File.ReadAllText(path), JsonOptions());
        }
        catch
        {
            catalog = null;
        }

        if (catalog is not null)
        {
            return catalog.Bindings.Count == 0 ? ModelFunctionCatalog.Empty : catalog;
        }

        // Parse failure on existing file — try backup
        var bak = path + ".bak";
        if (File.Exists(bak))
        {
            Console.Error.WriteLine($"[ModelFunctionCatalogStore] WARNING: '{Path.GetFileName(path)}' is corrupt; recovering from backup.");
            try
            {
                var bakCatalog = JsonSerializer.Deserialize<ModelFunctionCatalog>(File.ReadAllText(bak), JsonOptions());
                if (bakCatalog is not null)
                {
                    return bakCatalog.Bindings.Count == 0 ? ModelFunctionCatalog.Empty : bakCatalog;
                }
            }
            catch { }
            Console.Error.WriteLine("[ModelFunctionCatalogStore] WARNING: backup is also corrupt; falling back to empty catalog.");
        }
        else
        {
            Console.Error.WriteLine($"[ModelFunctionCatalogStore] WARNING: '{Path.GetFileName(path)}' is corrupt and no backup exists; falling back to empty catalog.");
        }

        return ModelFunctionCatalog.Empty;
    }

    public static void Save(string path, ModelFunctionCatalog catalog)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        var bak = path + ".bak";
        File.WriteAllText(tmp, JsonSerializer.Serialize(catalog, JsonOptions()));
        if (File.Exists(path))
            File.Replace(tmp, path, bak);
        else
            File.Move(tmp, path);
    }

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
