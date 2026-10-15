using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record StoreSetupResult(string StoreName, string DatabasePath, int Version);

// The inventory owns version eligibility; registrations own setup actions and workspace paths.
public static class StoreSetupRunner
{
    private sealed record Registration(Action<string> Setup, Func<string, string> ResolvePath,
        params string[] AdditionalStoreNames);

    private static readonly IReadOnlyDictionary<string, Registration> Registrations =
        new Dictionary<string, Registration>(StringComparer.Ordinal)
        {
            [StoreSchemaRegistry.Portfolio.StoreName] = new(PortfolioStore.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.Portfolio.Database)),
            [StoreSchemaRegistry.Experiments.StoreName] = new(ExperimentStore.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.Experiments.Database)),
            [StoreSchemaRegistry.Backlog.StoreName] = new(BacklogStore.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.Backlog.Database)),
            [StoreSchemaRegistry.OperatorLessons.StoreName] = new(SqliteOperatorLessonStore.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.OperatorLessons.Database)),
            [StoreSchemaRegistry.OperatorEscapes.StoreName] = new(SqliteOperatorEscapeStore.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.OperatorEscapes.Database)),
            [StoreSchemaRegistry.OperatorDirectives.StoreName] = new(OperatorDirectiveStoreSetup.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.OperatorDirectives.Database)),
            [StoreSchemaRegistry.OperatorIntents.StoreName] = new(SqliteOperatorIntentStore.Setup,
                directory => Path.Combine(directory, SqliteOperatorIntentStore.DatabaseFileName)),
            [StoreSchemaRegistry.CohortAcceptance.StoreName] = new(CohortAcceptanceStoreSetup.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.CohortAcceptance.Database),
                StoreSchemaRegistry.CohortAttributionRetractions.StoreName,
                StoreSchemaRegistry.CohortSoloInheritedReceipts.StoreName),
            [StoreSchemaRegistry.FollowerGateAcceptance.StoreName] = new(FollowerGateAcceptanceStoreSetup.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.FollowerGateAcceptance.Database)),
            [StoreSchemaRegistry.CollaborationItems.StoreName] = new(CollaborationItemStore.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.CollaborationItems.Database)),
            [StoreSchemaRegistry.RunEvents.StoreName] = new(RunEventStoreSetup.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.RunEvents.Database)),
            [StoreSchemaRegistry.MergeTrainAcceptance.StoreName] = new(MergeTrainAcceptanceStore.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.MergeTrainAcceptance.Database)),
            [StoreSchemaRegistry.DogfoodLog.StoreName] = new(DogfoodLogStore.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.DogfoodLog.Database)),
            [StoreSchemaRegistry.AuthorClaims.StoreName] = new(AuthorClaimStoreSetup.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.AuthorClaims.Database)),
            [StoreSchemaRegistry.StewardTriggers.StoreName] = new(StewardTriggerStoreSetup.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.StewardTriggers.Database)),
            [StoreSchemaRegistry.JudgePanel.StoreName] = new(JudgePanelStoreSetup.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.JudgePanel.Database)),
            [StoreSchemaRegistry.BoardFill.StoreName] = new(BoardFillStoreSetup.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.BoardFill.Database)),
            [StoreSchemaRegistry.ProgressiveReviewGlanceCircuit.StoreName] = new(SqliteProgressiveReviewGlanceCircuitStore.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.ProgressiveReviewGlanceCircuit.Database)),
            [StoreSchemaRegistry.ProgressiveReviewSteering.StoreName] = new(ProgressiveReviewSteeringStoreSetup.Setup,
                directory => Path.Combine(directory, StoreSchemaRegistry.ProgressiveReviewSteering.Database))
        };

    private static readonly IReadOnlyDictionary<string, Registration> RegistrationsByStore =
        Registrations.SelectMany(pair => new[] { pair.Key }.Concat(pair.Value.AdditionalStoreNames)
            .Select(name => new KeyValuePair<string, Registration>(name, pair.Value)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    public static IReadOnlyCollection<string> RegisteredStoreNames { get; } =
        Array.AsReadOnly(RegistrationsByStore.Keys.ToArray());

    public static IReadOnlyList<StoreSetupPlanEntry> Plan(string orchestratorDirectory, bool includeState = false)
    {
        var versioned = StoreSchemaRegistry.Inventory.Where(entry => entry.CurrentVersion.HasValue).ToArray();
        var missing = versioned.Where(entry => !RegistrationsByStore.ContainsKey(entry.StoreName))
            .Select(entry => entry.StoreName).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"No setup action registered for versioned stores: {string.Join(", ", missing)}.");

        var results = new List<StoreSetupPlanEntry>(versioned.Length);
        foreach (var group in versioned.GroupBy(entry => RegistrationsByStore[entry.StoreName].ResolvePath(orchestratorDirectory)))
        {
            WorkspacePathGuard(group.Key);
            using var snapshot = TargetExists(group.Key) ? new StoreInspectionSnapshot(group.Key) : null;
            foreach (var entry in group)
            {
                var recorded = snapshot is null ? null : StoreSchemaVersions.Read(snapshot.Connection, entry.StoreName);
                results.Add(new(entry.StoreName, group.Key, recorded, entry.CurrentVersion!.Value));
            }
        }
        if (includeState)
            results.Add(StateDbMigrations.Plan(Path.Combine(orchestratorDirectory, "state.db")));
        // Preserve inventory order, including grouped stores.
        return versioned.Select(entry => results.Single(result => result.StoreName == entry.StoreName))
            .Concat(results.Where(result => result.StoreName == "state-migrations")).ToArray();
    }

    public static void RequireCompatible(IReadOnlyList<StoreSetupPlanEntry> plan, bool requireCurrent = false)
    {
        foreach (var entry in plan)
            if (entry.IsNewer || requireCurrent && entry.RequiresMigration)
                throw new InvalidOperationException(
                    $"Store '{entry.StoreName}' at '{entry.DatabasePath}' records schema version {entry.RecordedVersion?.ToString() ?? "missing"}; " +
                    $"runtime supports {entry.RequiredVersion} (expected version {entry.RequiredVersion}). Use a compatible runtime or independently verified recovery. " +
                    (entry.IsNewer ? "Older setup cannot repair a newer schema." : "Stop/detach, run explicit setup with the new runtime, then restart; unattended activation cannot migrate."));
    }

    internal static void WorkspacePathGuard(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Store target parent is a filesystem alias: {directory.FullName}.");
        if (TargetExists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Store target is a filesystem alias: {path}.");
    }

    internal static bool TargetExists(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
                throw new InvalidOperationException($"Database target is a directory: {path}.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // Access denied and other I/O failures are uninspectable, never missing.
    }

    public static IReadOnlyList<StoreSetupResult> Run(string orchestratorDirectory)
    {
        // Preflight EVERY registration before any setup action, and rebuild the plan for execution.
        var plan = Plan(orchestratorDirectory);
        RequireCompatible(plan);
        var results = new List<StoreSetupResult>(plan.Count);
        var completed = new HashSet<Registration>();
        try
        {
            foreach (var entry in plan)
            {
                var registration = RegistrationsByStore[entry.StoreName];
                var path = entry.DatabasePath;
                var version = entry.RecordedVersion;
                if (entry.RequiresMigration)
                {
                    WorkspacePathGuard(path);
                    if (completed.Add(registration)) registration.Setup(path);
                    using var snapshot = new StoreInspectionSnapshot(path);
                    version = StoreSchemaVersions.Read(snapshot.Connection, entry.StoreName);
                }
                if (version != entry.RequiredVersion)
                    throw new InvalidOperationException($"Setup for store '{entry.StoreName}' at '{path}' did not record required version {entry.RequiredVersion}.");
                results.Add(new(entry.StoreName, path, version.Value));
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Setup stopped; completed: {string.Join(", ", results.Select(result => result.StoreName))}; " +
                $"pending: {string.Join(", ", plan.Skip(results.Count).Select(entry => entry.StoreName))}. {ex.Message}", ex);
        }
        return results.AsReadOnly();
    }
}

public sealed record StoreSetupPlanEntry(string StoreName, string DatabasePath, int? RecordedVersion, int RequiredVersion)
{
    public bool IsNewer => RecordedVersion > RequiredVersion;
    public bool RequiresMigration => RecordedVersion is null || RecordedVersion < RequiredVersion;
    public string Action => IsNewer ? "refuse-newer" : RequiresMigration ? "migrate" : "current";
}
