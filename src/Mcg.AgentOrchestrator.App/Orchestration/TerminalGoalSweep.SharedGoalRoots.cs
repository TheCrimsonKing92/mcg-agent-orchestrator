using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class TerminalGoalSweep
{
    // All discovered stores sharing the per-user build root must agree a goal is absent.
    // If a store cannot be enumerated or read, leave roots in place.
    internal static IReadOnlyList<string>? GetSharedGoalStorePaths(
        string stateDbPath, out string? failure, string? canonicalRepoRoot = null,
        OrchestratorProjectRegistry? projectRegistry = null)
    {
        failure = null;
        var repoRoot = canonicalRepoRoot ?? OrchestratorWorkspace.ResolveRepoRoot();
        var canonicalDb = OrchestratorWorkspace.ForDirectory(repoRoot).SqliteStatePath;
        if (!PathsEqual(stateDbPath, canonicalDb))
            return null;

        try
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { canonicalDb };
            AddStateStores(paths, Path.Combine(repoRoot, ".orchestrator", "tenants"));
            AddStateStores(paths, Path.Combine(repoRoot, ".orchestrator", "projects"));
            foreach (var project in (projectRegistry ?? OrchestratorProjectRegistry.CreateDefault()).ListProjects())
            {
                var projectDirectory = OrchestratorWorkspace.ForProject(
                    project.Name, project.RootDirectory).OrchestratorDirectory;
                AddStateStores(paths, projectDirectory);
            }
            return paths.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or InvalidDataException)
        {
            failure = $"shared goal store discovery failed: {ex.Message}";
            return null;
        }
    }

    private static void AddStateStores(HashSet<string> paths, string directory)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var path in Directory.EnumerateFiles(
            directory, "state.db", SearchOption.AllDirectories))
            paths.Add(Path.GetFullPath(path));
    }

    private static bool TryReadGoalIds(
        IReadOnlyList<string> storePaths, out HashSet<string> goalIds, out string? failure)
    {
        goalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        failure = null;
        try
        {
            foreach (var path in storePaths)
            {
                if (!File.Exists(path) || !StateDbMigrations.IsUpToDate(path))
                {
                    failure = $"shared goal store unavailable or not migrated: {path}";
                    return false;
                }
                using var connection = StateDbConnectionFactory.Open(
                    path, StateDbConnectionProfile.FastFailRead);
                using var goals = connection.CreateCommand();
                goals.CommandText = "SELECT id FROM goals";
                using var reader = goals.ExecuteReader();
                while (reader.Read())
                    goalIds.Add(reader.GetString(0));
            }
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            failure = $"shared goal store read failed: {ex.Message}";
            return false;
        }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
