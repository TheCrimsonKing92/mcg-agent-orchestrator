namespace Mcg.AgentOrchestrator.Infrastructure;

// Diagnostic observations only; neither collection failures nor values decide a gate verdict.
internal sealed class LaneSchedulingSampler(int rootPid,
    Func<int, IReadOnlyList<int>?>? members = null,
    Func<int, ProcessSchedulingSample>? read = null, TimeProvider? clock = null)
{
    private readonly object sync = new();
    private readonly Dictionary<int, ProcessSchedulingSample> processes = [];
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private DateTimeOffset? lastSample;
    private int samples;
    private string? unavailable;

    internal void Tick()
    {
        lock (sync)
        {
            try
            {
                var now = time.GetUtcNow();
                if (lastSample is { } last && now - last < TimeSpan.FromSeconds(30)) return;
                var ids = (members ?? AcceptanceChildQos.EnumerateMembers)(rootPid);
                if (ids is null || ids.Count == 0) { unavailable = "no-job-members"; return; }
                unavailable = null;
                foreach (var pid in ids.Take(256).Distinct())
                {
                    try
                    {
                        var sample = (read ?? ProcessSchedulingProbe.Read)(pid);
                        processes[pid] = sample;
                        unavailable ??= sample.UnavailableReason;
                    }
                    catch (Exception error) { unavailable ??= "probe-error:" + error.GetType().Name; }
                }
                samples++;
                lastSample = now;
            }
            catch (Exception error) { unavailable = "sample-error:" + error.GetType().Name; }
        }
    }

    internal LaneSchedulingSummary Summary()
    {
        lock (sync)
        {
            processes.TryGetValue(rootPid, out var root);
            var lowest = processes.Values.Where(sample => sample.IsBelowNormal)
                .OrderBy(sample => sample.PriorityClass == "Idle" ? 0 : 1).ThenBy(sample => sample.ProcessId).FirstOrDefault();
            return new(samples, processes.Count, root?.PriorityClass,
                root?.ControlMask is { } control && root.StateMask is { } state ? $"{control}/{state}" : null,
                processes.Values.Count(sample => sample.IsBelowNormal), processes.Values.Count(sample => sample.IsExplicitEco),
                processes.Values.Count(sample => sample.IsOptedOut), processes.Values.Count(sample => sample.IsBackgroundMemory),
                lowest is null ? null : $"{lowest.Image}:{lowest.PriorityClass}",
                unavailable ?? (samples == 0 ? "no-sample-tick" : null));
        }
    }
}
