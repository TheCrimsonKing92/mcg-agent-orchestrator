using Microsoft.Data.Sqlite;
using Mcg.AgentOrchestrator.Infrastructure;

[assembly: Xunit.AssemblyFixture(typeof(OrchestratorAttemptRootLeakGuardFixture))]

public sealed class OrchestratorAttemptRootLeakGuardFixture : IAsyncDisposable
{
    internal static readonly string[] AttemptRootNames =
        ["acceptance-gate-attempts", "pre-review-evidence-attempts"];

    private readonly string? repositoryRoot;
    private readonly Dictionary<string, HashSet<string>> baseline = new(StringComparer.Ordinal);

    public OrchestratorAttemptRootLeakGuardFixture()
    {
        try
        {
            var configured = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
            repositoryRoot = GoalAcceptanceVerifier.ResolveOwnerResultsRepositoryRoot(
                string.IsNullOrWhiteSpace(configured)
                    ? InfrastructureTestSupport.FindRepositoryRoot()
                    : configured);
            if (!File.Exists(StateDbPath(repositoryRoot)))
            {
                Console.Error.WriteLine("attempt-root-leak-guard skipped reason=state.db absent");
                repositoryRoot = null;
                return;
            }

            foreach (var rootName in AttemptRootNames)
            {
                baseline[rootName] = Snapshot(AttemptRoot(repositoryRoot, rootName));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"attempt-root-leak-guard skipped reason={exception.GetType().Name}");
            repositoryRoot = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (repositoryRoot is null)
        {
            return ValueTask.CompletedTask;
        }

        if (!TryReadGoalIds(StateDbPath(repositoryRoot), out var goalIds))
        {
            Console.Error.WriteLine("attempt-root-leak-guard skipped reason=state.db unreadable");
            return ValueTask.CompletedTask;
        }

        var leaks = new List<string>();
        foreach (var rootName in AttemptRootNames)
        {
            var root = AttemptRoot(repositoryRoot, rootName);
            foreach (var name in FindLeaks(baseline[rootName], Snapshot(root), goalIds))
            {
                var entry = Path.Combine(root, name);
                var created = Directory.GetCreationTimeUtc(entry);
                var children = Directory.Exists(entry)
                    ? Directory.EnumerateFileSystemEntries(entry).Take(5).Select(Path.GetFileName)
                    : [];
                leaks.Add($"{rootName}/{name} created={created:O} children={string.Join(',', children)}");
            }
        }

        if (leaks.Count > 0)
        {
            throw new InvalidOperationException(
                "Test assembly created non-goal entries in live attempt roots: " + string.Join("; ", leaks));
        }

        return ValueTask.CompletedTask;
    }

    internal static HashSet<string> Snapshot(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName)
                .Where(name => name is not null).Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"attempt-root-leak-guard skipped root={root} reason={exception.GetType().Name}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static IReadOnlyList<string> FindLeaks(
        IReadOnlySet<string> baseline,
        IReadOnlySet<string> current,
        IReadOnlySet<string> goalIds) =>
        current.Where(name => !baseline.Contains(name) &&
                              !name.Equals("operator", StringComparison.OrdinalIgnoreCase) &&
                              !goalIds.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();

    internal static bool TryReadGoalIds(string stateDbPath, out HashSet<string> goalIds)
    {
        goalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(stateDbPath))
        {
            return false;
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = stateDbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM goals";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                goalIds.Add(reader.GetString(0));
            }

            return true;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            goalIds.Clear();
            return false;
        }
    }

    private static string AttemptRoot(string repositoryRoot, string name) =>
        Path.Combine(repositoryRoot, ".orchestrator", name);

    private static string StateDbPath(string repositoryRoot) =>
        Path.Combine(repositoryRoot, ".orchestrator", "state.db");
}
