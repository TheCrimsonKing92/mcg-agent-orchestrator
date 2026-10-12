using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

internal static class SeededStateDatabaseTemplate
{
    private static readonly Lazy<byte[]> Template = new(BuildValidatedTemplate);

    // Keep the canonical bytes private so callers cannot corrupt subsequent seeds.
    internal static byte[] Build() => [.. Template.Value];

    internal static bool Validate(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return false;

        var root = CreateTemporaryRoot();
        try
        {
            Directory.CreateDirectory(root);
            var statePath = Path.Combine(root, "state.db");
            File.WriteAllBytes(statePath, bytes);
            return StateDbMigrations.IsUpToDate(statePath);
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(root);
        }
    }

    internal static void CopyTo(string statePath)
    {
        var bytes = Template.Value;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        File.WriteAllBytes(statePath, bytes);
    }

    private static byte[] BuildValidatedTemplate()
    {
        // Lazy serializes validation and the single rebuild attempt before publication.
        var bytes = BuildMigratedDatabase();
        if (!Validate(bytes))
        {
            bytes = BuildMigratedDatabase();
            if (!Validate(bytes))
                throw new InvalidOperationException("The rebuilt seeded state database template is not fully migrated.");
        }

        return bytes;
    }

    private static byte[] BuildMigratedDatabase()
    {
        var root = CreateTemporaryRoot();
        var statePath = Path.Combine(root, "state.db");
        try
        {
            StateDbMigrations.EnsureUpToDate(statePath);
            if (!StateDbMigrations.IsUpToDate(statePath))
                throw new InvalidOperationException("The seeded state database template is not fully migrated.");

            // Migration connections are nonpooled and disposed before capture, so WAL
            // changes have been checkpointed into this self-contained main file.
            return File.ReadAllBytes(statePath);
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(root);
        }
    }

    private static string CreateTemporaryRoot() =>
        Path.Combine(Path.GetTempPath(), $"mcg-state-template-{Guid.NewGuid():N}");
}
