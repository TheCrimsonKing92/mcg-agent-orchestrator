namespace Mcg.AgentOrchestrator.Infrastructure;

// One command's best-effort process sweep, enabled by its owning launch policy.
internal sealed class AcceptanceChildQos : IDisposable
{
    private static volatile bool enabled;
    private static readonly AsyncLocal<bool?> Override = new();
    private readonly object sync = new();
    private readonly int rootPid;
    private readonly Func<int, IReadOnlyList<int>?> members;
    private readonly Func<int, uint, uint, bool> setter;
    private readonly HashSet<int> tried = [];
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task? loop;
    private int optedOutCount;
    private bool disposed;

    internal static bool Enabled => Override.Value ?? enabled;
    internal int OptedOutCount { get { lock (sync) return optedOutCount; } }
    internal int TriedCount { get { lock (sync) return tried.Count; } }

    internal static void Enable()
    {
        if (Override.Value.HasValue) Override.Value = true;
        else enabled = true;
    }

    internal static IDisposable PushForTests(bool value)
    {
        var previous = Override.Value;
        Override.Value = value;
        return new RestoreScope(() => Override.Value = previous);
    }

    internal static AcceptanceChildQos? StartSweep(int rootPid, bool ownLoop,
        Func<int, IReadOnlyList<int>?>? members = null, Func<int, uint, uint, bool>? setter = null) =>
        Enabled ? new(rootPid, ownLoop, members ?? EnumerateMembers, setter ?? ProcessExecutionThrottlingOptOut.TrySet) : null;

    internal static AcceptanceChildQos? StartSweep(bool enabled, int rootPid, TimeSpan interval,
        Func<int, IReadOnlyList<int>?> members, Func<int, uint, uint, bool>? setter = null) =>
        enabled ? new(rootPid, true, members, setter ?? ProcessExecutionThrottlingOptOut.TrySet, interval) : null;

    private AcceptanceChildQos(int rootPid, bool ownLoop, Func<int, IReadOnlyList<int>?> members,
        Func<int, uint, uint, bool> setter, TimeSpan? interval = null)
    {
        this.rootPid = rootPid;
        this.members = members;
        this.setter = setter;
        TryOnce(rootPid);
        if (ownLoop)
            loop = Task.Run(() => GateLaneHeartbeatLoop.RunAsync(_ => { }, RunPass,
                cancellation.Token, interval ?? TimeSpan.FromSeconds(5)));
    }

    internal static IReadOnlyList<int>? EnumerateMembers(int rootPid) =>
        WorkerProcessJobs.TryGetActiveProcessIds(rootPid, out var ids) ? ids : null;

    internal void RunPass()
    {
        lock (sync)
        {
            if (disposed) return;
            try
            {
                var ids = members(rootPid);
                if (ids is null) return;
                for (var index = 0; index < Math.Min(ids.Count, 256); index++) TryOnce(ids[index]);
            }
            catch (Exception) { /* A failed enumeration does not affect the command. */ }
        }
    }

    private void TryOnce(int pid)
    {
        if (pid <= 0 || !tried.Add(pid)) return;
        try { if (setter(pid, 1, 0)) optedOutCount++; }
        catch (Exception) { /* Failures count as tried, including exited members. */ }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
        }
        cancellation.Cancel();
        try { loop?.GetAwaiter().GetResult(); } catch (Exception) { }
        cancellation.Dispose();
    }

    private sealed class RestoreScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
