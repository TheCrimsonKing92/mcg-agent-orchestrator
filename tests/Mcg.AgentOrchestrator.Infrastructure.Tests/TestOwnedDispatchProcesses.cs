using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// A test owns the OS host even after an exit artifact has completed its kernel record.
internal sealed class TestOwnedDispatchProcesses : IDisposable
{
    private sealed record OwnedHost(Process Handle, string DispatchFile);
    private static readonly AsyncLocal<TestOwnedDispatchProcesses?> CurrentOwner = new();
    private readonly AgentOrchestratorKernel kernel;
    private readonly Func<Goal?> getGoal;
    private readonly TestOwnedDispatchProcesses? previousOwner;
    private readonly object gate = new();
    private readonly List<OwnedHost> hosts = [];
    private bool disposed;

    internal TestOwnedDispatchProcesses(AgentOrchestratorKernel kernel, Func<Goal?> getGoal)
    {
        this.kernel = kernel;
        this.getGoal = getGoal;
        previousOwner = CurrentOwner.Value;
        CurrentOwner.Value = this;
    }

    internal TestOwnedDispatchProcesses(AgentOrchestratorKernel kernel, Goal goal) : this(kernel, () => goal) { }

    internal static void RecordLaunch(Process process, string dispatchFile)
    {
        if (CurrentOwner.Value is not { } owner) return;
        lock (owner.gate)
        {
            if (owner.disposed) throw new InvalidOperationException("Dispatch launched after its owner was disposed.");
            // Registry-free launches legitimately omit ProcessIdentityStartedAt. Capture the
            // identity at launch and retain its OS handle instead of reopening a recorded PID.
            var handle = Process.GetProcessById(process.Id);
            try
            {
                _ = handle.SafeHandle;
                if (handle.StartTime.ToUniversalTime() != process.StartTime.ToUniversalTime())
                    throw new InvalidOperationException($"Dispatch pid={process.Id} identity changed during ownership capture.");
                owner.hosts.Add(new(handle, dispatchFile));
            }
            catch { handle.Dispose(); throw; }
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(CurrentOwner.Value, this)) CurrentOwner.Value = previousOwner;
        OwnedHost[] ownedHosts;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ownedHosts = hosts.ToArray();
        }
        var failures = new List<Exception>();
        try
        {
            if (getGoal() is { } goal)
            {
                foreach (var task in goal.Tasks)
                {
                    if (task.LastProcess is not { IsRunning: true } record) continue;
                    var host = ownedHosts.FirstOrDefault(owned => Matches(owned, record))?.Handle;
                    if (host is null || host.HasExited) continue;
                    try
                    {
                        new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, task.Id);
                    }
                    catch (Exception exception) { failures.Add(exception); }
                }
            }
        }
        catch (Exception exception) { failures.Add(exception); }
        finally
        {
            // Own every launch, including hosts whose task record has since been replaced
            // or cleared and hosts still tearing down after their exit artifact was written.
            foreach (var owned in ownedHosts)
            {
                using var host = owned.Handle;
                try
                {
                    StopAndAwait(host);
                }
                catch (Exception exception) { failures.Add(exception); }
            }
        }

        if (failures.Count != 0) throw new AggregateException("Could not reap test-owned dispatch hosts.", failures);
    }

    internal static void StopAndAwait(Process host)
    {
        if (!host.HasExited) host.Kill(entireProcessTree: true);
        host.WaitForExit();
    }

    internal static async Task AwaitCompletionAsync(TaskProcessRecord record)
    {
        var owner = CurrentOwner.Value
            ?? throw new InvalidOperationException("A dispatch completion wait requires a test owner.");
        Process host;
        lock (owner.gate)
        {
            ObjectDisposedException.ThrowIf(owner.disposed, owner);
            host = owner.hosts.SingleOrDefault(owned => Matches(owned, record))?.Handle
                ?? throw new InvalidOperationException($"Dispatch host pid={record.ProcessId} was not captured at launch.");
        }
        await TestHangGuard.WaitAsync(host.WaitForExitAsync(TestContext.Current.CancellationToken),
            $"dispatch host pid={record.ProcessId} exit");
    }

    private static bool Matches(OwnedHost owned, TaskProcessRecord record) =>
        owned.Handle.Id == record.ProcessId &&
        owned.DispatchFile == record.ExitCodePath.Replace(".exit.txt", ".dispatch.json", StringComparison.Ordinal);
}
