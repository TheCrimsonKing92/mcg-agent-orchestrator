using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Observation follows the calling execution context, including asynchronous launches.
// It never changes the child environment or process-wide dispatch policy.
internal static class DispatchProcessStartObservation
{
    private static readonly AsyncLocal<Action<Process, string>?> Observer = new();

    internal static IDisposable Observe(Action<Process, string> observer)
    {
        var previous = Observer.Value;
        Observer.Value = observer;
        return new ObservationScope(() => Observer.Value = previous);
    }

    internal static Process? Start(ProcessStartInfo startInfo, Func<ProcessStartInfo, Process?> start)
    {
        var process = start(startInfo);
        if (process is null || Observer.Value is not { } observer) return process;
        var arguments = startInfo.ArgumentList;
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (arguments[index] != DispatchProcessHost.SubcommandName) continue;
            try { observer(process, arguments[index + 1]); }
            catch
            {
                // A failed ownership receipt must never strand a newly started child.
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
                finally { process.Dispose(); }
                throw;
            }
            break;
        }
        return process;
    }

    private sealed class ObservationScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
