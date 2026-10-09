using System.Diagnostics;
using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit.v3;

[assembly: DispatchProcessLeakGuard]

// After failures are attached to the owning test before assembly temp cleanup.
internal sealed class DispatchProcessLeakGuardAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test) =>
        DispatchProcessLeakGuard.Begin(test.UniqueID);

    public override void After(MethodInfo methodUnderTest, IXunitTest test) =>
        DispatchProcessLeakGuard.End(test.UniqueID);
}

internal static class DispatchProcessLeakGuard
{
    private sealed record OwnedHost(Process Handle, string DispatchFile);

    private sealed class Scope
    {
        internal object Gate { get; } = new();
        internal List<OwnedHost> Hosts { get; } = [];
        internal IDisposable Observation { get; set; } = null!;
        internal bool Ended { get; set; }

        internal void Record(Process process, string dispatchFile)
        {
            // Injected launch fakes can return the test process or a shell instead of a dispatch host.
            if (process.Id == Environment.ProcessId || process.HasExited ||
                !string.Equals(process.ProcessName, "dotnet", StringComparison.OrdinalIgnoreCase)) return;
            lock (Gate)
            {
                if (Ended) throw new InvalidOperationException("Dispatch launched after its owning test returned.");
                // Retain a separate OS handle while the launcher still owns the child.
                // The guard never acts on a bare PID or scans another test's hosts.
                var handle = Process.GetProcessById(process.Id);
                try
                {
                    _ = handle.SafeHandle;
                    if (handle.StartTime.ToUniversalTime() != process.StartTime.ToUniversalTime())
                        throw new InvalidOperationException($"Dispatch pid={process.Id} identity changed during registration.");
                    TestOwnedDispatchProcesses.RecordLaunch(process, dispatchFile);
                    Hosts.Add(new(handle, dispatchFile));
                }
                catch { handle.Dispose(); throw; }
            }
        }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Scope> Scopes = [];

    internal static bool HasScope(string testId)
    {
        lock (Gate) return Scopes.ContainsKey(testId);
    }

    internal static bool HasHost(string testId, int processId, string dispatchFile)
    {
        lock (Gate)
        {
            if (!Scopes.TryGetValue(testId, out var scope)) return false;
            lock (scope.Gate)
                return scope.Hosts.Any(host => host.Handle.Id == processId && host.DispatchFile == dispatchFile);
        }
    }

    internal static void Begin(string testId)
    {
        lock (Gate)
        {
            var scope = new Scope();
            Scopes.Add(testId, scope);
            scope.Observation = DispatchProcessStartObservation.Observe(scope.Record);
        }
    }

    internal static void End(string testId)
    {
        Scope scope;
        lock (Gate)
        {
            // Contract tests invoke the real After hook and assert its exception themselves.
            if (!Scopes.Remove(testId, out scope!)) return;
        }
        scope.Observation.Dispose();

        var leaks = new List<string>();
        var cleanupFailures = new List<Exception>();
        lock (scope.Gate)
        {
            scope.Ended = true;
            foreach (var host in scope.Hosts)
            {
                using var handle = host.Handle;
                if (handle.HasExited) continue;
                leaks.Add($"pid={handle.Id} dispatch={host.DispatchFile}");
                // Preserve the failure while preventing a leak from poisoning assembly cleanup.
                try
                {
                    TestOwnedDispatchProcesses.StopAndAwait(handle);
                }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
        }
        if (leaks.Count != 0)
            throw new Xunit.Sdk.XunitException(
                "Test returned while dispatch hosts it started are still running:" + Environment.NewLine +
                string.Join(Environment.NewLine, leaks) +
                (cleanupFailures.Count == 0 ? string.Empty : Environment.NewLine +
                    "Dispatch cleanup failed: " + string.Join("; ", cleanupFailures.Select(error => error.Message))));
    }
}
