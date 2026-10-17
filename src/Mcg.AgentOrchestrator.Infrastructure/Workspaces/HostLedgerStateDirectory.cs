namespace Mcg.AgentOrchestrator.Infrastructure;

// The process installs one route; explicit routes keep callers independent of process state.
internal static class HostLedgerStateDirectory
{
    private static Func<string, string>? _route;

    internal static string Resolve(string worktreePath) => Resolve(worktreePath, Volatile.Read(ref _route));

    internal static string Resolve(string worktreePath, Func<string, string>? route)
    {
        var host = AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath);
        var legacy = Path.Combine(host, ".orchestrator");
        if (route is null) return legacy;

        try
        {
            var directory = route(host);
            return string.IsNullOrWhiteSpace(directory) ? legacy : directory;
        }
        catch (Exception)
        {
            // Best-effort telemetry keeps its legacy location when routing is unavailable.
            return legacy;
        }
    }

    internal static void InstallRoute(Func<string, string> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (Interlocked.CompareExchange(ref _route, route, null) is not null)
            throw new InvalidOperationException("The host ledger state directory route is already installed.");
    }
}
