using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class SqliteStateJsonMigrator
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

    public static async Task<bool> MigrateIfNeededAsync(
        string jsonPath,
        string dbPath,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(dbPath) || !File.Exists(jsonPath))
            return false;

        var json = await File.ReadAllTextAsync(jsonPath, cancellationToken);

        var snapshot = JsonSerializer.Deserialize<OrchestratorSnapshot>(json, _jsonOptions)
            ?? new OrchestratorSnapshot([], []);
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot);

        File.Copy(jsonPath, jsonPath + ".pre-sqlite-migration.bak", overwrite: true);

        var repository = new SqliteOrchestratorStateRepository(dbPath);
        await repository.SaveAsync(kernel, cancellationToken);

        return true;
    }
}
