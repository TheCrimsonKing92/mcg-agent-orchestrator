using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class StoreSchemaDiagnostics
{
    private const string CompatibilityDiagnosticKey = "schema-compatibility-diagnostic";

    // Preserve the established exception message contract; production diagnostics use
    // the separate compatibility field for Newer rather than recommending older setup.
    public static InvalidOperationException WithCompatibility(InvalidOperationException error,
        StoreSchemaState state, SqliteConnection connection, StoreSchemaEntry entry, string path)
    {
        if (state == StoreSchemaState.Newer)
            error.Data[CompatibilityDiagnosticKey] =
                $"Store '{entry.StoreName}' at '{path}' records schema version {StoreSchemaVersions.Read(connection, entry.StoreName)}, " +
                $"newer than runtime-supported version {entry.CurrentVersion}. Use a compatible runtime or independently verified recovery; older setup cannot repair this schema.";
        return error;
    }

    public static string Message(Exception error) =>
        error.Data[CompatibilityDiagnosticKey] as string ?? error.Message;
}
