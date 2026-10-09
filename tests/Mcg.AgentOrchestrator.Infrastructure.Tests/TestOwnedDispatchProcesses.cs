using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// A test owns the OS host even after an exit artifact has completed its kernel record.
internal sealed class TestOwnedDispatchProcesses(AgentOrchestratorKernel kernel, Func<Goal?> getGoal) : IDisposable
{
    internal TestOwnedDispatchProcesses(AgentOrchestratorKernel kernel, Goal goal) : this(kernel, () => goal) { }

    public void Dispose()
    {
        var failures = new List<Exception>();
        var goal = getGoal();
        if (goal is null) return;
        foreach (var task in goal.Tasks)
        {
            if (task.LastProcess is not { } record) continue;
            try
            {
                using var host = OpenSameHost(record);
                if (host is null) continue;
                try
                {
                    if (!host.HasExited && record.IsRunning)
                        new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, task.Id);
                }
                finally
                {
                    // Completed records can still have an OS host finishing teardown.
                    if (!host.HasExited) host.Kill(entireProcessTree: true);
                    host.WaitForExit();
                }
            }
            catch (Exception exception) { failures.Add(exception); }
        }

        if (failures.Count != 0) throw new AggregateException("Could not reap test-owned dispatch hosts.", failures);
    }

    internal static async Task AwaitCompletionAsync(TaskProcessRecord record)
    {
        using var host = OpenSameHost(record);
        if (host is not null)
            await TestHangGuard.WaitAsync(host.WaitForExitAsync(TestContext.Current.CancellationToken),
                $"dispatch host pid={record.ProcessId} exit");
    }

    private static Process? OpenSameHost(TaskProcessRecord record)
    {
        Process host;
        try { host = Process.GetProcessById(record.ProcessId); }
        catch (ArgumentException) { return null; }

        try
        {
            // Retain the OS handle across identity validation, termination and the exit wait.
            _ = host.SafeHandle;
            if (host.HasExited || record.ProcessIdentityStartedAt is { } identity &&
                new DateTimeOffset(host.StartTime.ToUniversalTime(), TimeSpan.Zero) != identity)
            {
                host.Dispose();
                return null;
            }

            if (record.ProcessIdentityStartedAt is null)
                throw new InvalidOperationException($"Dispatch host pid={record.ProcessId} has no recorded process identity.");
            return host;
        }
        catch { host.Dispose(); throw; }
    }
}
