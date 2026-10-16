using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class LoopExitMaintenanceJoin
{
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    internal static async Task JoinAsync(
        IReadOnlyList<(string Name, Func<Task> Wait)> runners,
        TimeSpan grace,
        Func<TimeSpan, Task> delay,
        Action<string> report)
    {
        if (runners.Count == 0) return;

        var waits = new Task[runners.Count];
        var observed = new Task[runners.Count];
        for (var i = 0; i < runners.Count; i++)
        {
            try { waits[i] = runners[i].Wait(); }
            catch (Exception) { waits[i] = Task.CompletedTask; }
            observed[i] = ObserveAsync(waits[i]);
        }

        await Task.WhenAny(Task.WhenAll(observed), delay(grace)).ConfigureAwait(false);
        var graceSeconds = ((long)grace.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < waits.Length; i++)
        {
            if (!waits[i].IsCompleted)
                report($"LOOP_EXIT_MAINTENANCE_STILL_RUNNING runner={runners[i].Name} graceSeconds={graceSeconds}");
        }
    }

    private static async Task ObserveAsync(Task wait)
    {
        try { await wait.ConfigureAwait(false); }
        catch (Exception) { /* Runners report failures; also observe faults after abandonment. */ }
    }
}
