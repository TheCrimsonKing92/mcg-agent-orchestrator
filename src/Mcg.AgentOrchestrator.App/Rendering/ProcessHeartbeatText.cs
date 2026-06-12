using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Rendering;

internal static class ProcessHeartbeatText
{
    public static string FormatInline(DispatchHeartbeatStatus heartbeat)
    {
        if (!heartbeat.IsAvailable)
        {
            return $"heartbeat unavailable ({heartbeat.UnavailableReason ?? "unknown"}): {heartbeat.Path}";
        }

        return
            $"heartbeat {heartbeat.State}: path={heartbeat.Path}; pid={heartbeat.ProcessId}; child_pid={heartbeat.ChildProcessId?.ToString() ?? "unknown"}; " +
            $"last_observed={FormatTimestamp(heartbeat.LastObservedAt)}; last_progress={FormatTimestamp(heartbeat.LastProgressAt)}; " +
            $"heartbeat_age={FormatDuration(heartbeat.HeartbeatAge)}; idle_for={FormatDuration(heartbeat.IdleDuration)}; " +
            $"stdout_bytes={heartbeat.StandardOutputBytes}; stderr_bytes={heartbeat.StandardErrorBytes}";
    }

    public static IReadOnlyList<string> FormatLines(DispatchHeartbeatStatus heartbeat)
    {
        if (!heartbeat.IsAvailable)
        {
            return
            [
                $"heartbeat: unavailable ({heartbeat.UnavailableReason ?? "unknown"})",
                $"heartbeat path: {heartbeat.Path}"
            ];
        }

        return
        [
            $"heartbeat: available state={heartbeat.State} pid={heartbeat.ProcessId} child_pid={heartbeat.ChildProcessId?.ToString() ?? "unknown"}",
            $"heartbeat path: {heartbeat.Path}",
            $"last observed: {FormatTimestamp(heartbeat.LastObservedAt)} age={FormatDuration(heartbeat.HeartbeatAge)}",
            $"last progress: {FormatTimestamp(heartbeat.LastProgressAt)} idle={FormatDuration(heartbeat.IdleDuration)}",
            $"log bytes: stdout={heartbeat.StandardOutputBytes} stderr={heartbeat.StandardErrorBytes}"
        ];
    }

    private static string FormatTimestamp(DateTimeOffset? value)
    {
        return value?.ToString("u") ?? "n/a";
    }

    private static string FormatDuration(TimeSpan? value)
    {
        if (value is null)
        {
            return "n/a";
        }

        var duration = value.Value < TimeSpan.Zero ? TimeSpan.Zero : value.Value;
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes}m {duration.Seconds}s";
        }

        return $"{Math.Max(0, (int)duration.TotalSeconds)}s";
    }
}
