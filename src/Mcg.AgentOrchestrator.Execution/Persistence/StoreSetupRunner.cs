using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record StoreSetupResult(string StoreName, string DatabasePath, int Version);

// The inventory owns version eligibility; registrations own setup actions and workspace paths.
public static class StoreSetupRunner
{
    private sealed record Registration(Action<string> Setup, Func<string, string> ResolvePath);

    private static readonly IReadOnlyDictionary<string, Registration> Registrations =
        new Dictionary<string, Registration>(StringComparer.Ordinal)
        {
            [StoreSchemaRegistry.Portfolio.StoreName] = new(PortfolioStore.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.Portfolio.Database)),
            [StoreSchemaRegistry.Backlog.StoreName] = new(BacklogStore.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.Backlog.Database)),
            [StoreSchemaRegistry.OperatorLessons.StoreName] = new(SqliteOperatorLessonStore.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.OperatorLessons.Database)),
            [StoreSchemaRegistry.OperatorEscapes.StoreName] = new(SqliteOperatorEscapeStore.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.OperatorEscapes.Database))
        };

    public static IReadOnlyCollection<string> RegisteredStoreNames { get; } =
        Array.AsReadOnly(Registrations.Keys.ToArray());

    public static IReadOnlyList<StoreSetupResult> Run(string orchestratorDirectory)
    {
        var versioned = StoreSchemaRegistry.Inventory.Where(entry => entry.CurrentVersion.HasValue).ToArray();
        var missing = versioned.Where(entry => !Registrations.ContainsKey(entry.StoreName))
            .Select(entry => entry.StoreName).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"No setup action registered for versioned stores: {string.Join(", ", missing)}.");

        var results = new List<StoreSetupResult>(versioned.Length);
        foreach (var entry in versioned)
        {
            var registration = Registrations[entry.StoreName];
            var path = registration.ResolvePath(orchestratorDirectory);
            registration.Setup(path);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();
            var version = StoreSchemaVersions.Read(connection, entry.StoreName)
                ?? throw new InvalidOperationException($"Setup for store '{entry.StoreName}' at '{path}' did not record a version.");
            results.Add(new(entry.StoreName, path, version));
        }
        return results.AsReadOnly();
    }
}
