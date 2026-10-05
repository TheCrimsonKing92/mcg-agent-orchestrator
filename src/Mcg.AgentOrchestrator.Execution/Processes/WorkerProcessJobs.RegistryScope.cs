namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class WorkerProcessJobs
{
    private sealed record RegistryScopeState(SpawnRegistry? Registry, string? DbPath);

    private static readonly AsyncLocal<RegistryScopeState?> ScopedRegistry = new();
    private static SpawnRegistry? SharedRegistry;
    private static string? SharedRegistryDbPath;

    private static SpawnRegistry? Registry
    {
        get => ScopedRegistry.Value is { } scope ? scope.Registry : SharedRegistry;
        set => SharedRegistry = value;
    }

    private static string? RegistryDbPath
    {
        get => ScopedRegistry.Value is { } scope ? scope.DbPath : SharedRegistryDbPath;
        set => SharedRegistryDbPath = value;
    }

    internal static IDisposable UseRegistryScopeForTests(string? dbPath)
    {
        var previous = ScopedRegistry.Value;
        ScopedRegistry.Value = new RegistryScopeState(
            dbPath is null ? null : new SpawnRegistry(dbPath),
            dbPath);
        return new RegistryScope(() => ScopedRegistry.Value = previous);
    }

    private sealed class RegistryScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
