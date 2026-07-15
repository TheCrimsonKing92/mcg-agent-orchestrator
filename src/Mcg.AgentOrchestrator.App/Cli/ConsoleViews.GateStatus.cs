using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintGateStatus(IReadOnlyList<GateHeartbeatStatus> statuses)
    {
        Console.WriteLine("Gate status:");
        foreach (var status in statuses)
        {
            if (!status.IsAvailable || status.Snapshot is null)
            {
                Console.WriteLine($"slot-{status.SlotIndex}: unavailable reason={status.UnavailableReason ?? "unknown"} heartbeat={status.Path}");
                continue;
            }

            var snapshot = status.Snapshot;
            Console.WriteLine(
                $"slot-{status.SlotIndex}: goal={ShortGateGoal(snapshot.GoalId)} phase={snapshot.Phase} state={snapshot.State} " +
                $"elapsed={FormatGateDuration(status.HeartbeatAge is null ? TimeSpan.Zero : snapshot.LastObservedAt - snapshot.StartedAt)} " +
                $"target={Quote(snapshot.CurrentTarget)} child_pid={snapshot.ChildPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
                $"heartbeat_age={FormatGateDuration(status.HeartbeatAge)} idle_for={FormatGateDuration(status.IdleDuration)} output_bytes={snapshot.OutputBytes} " +
                $"heartbeat={status.Path}");
        }
    }

    private static string ShortGateGoal(string? goalId) =>
        string.IsNullOrWhiteSpace(goalId) ? "unknown" : goalId.Length <= 8 ? goalId : goalId[..8];

    private static string FormatGateDuration(TimeSpan? duration)
    {
        if (duration is null)
            return "unknown";

        var value = duration.Value < TimeSpan.Zero ? TimeSpan.Zero : duration.Value;
        return value.TotalMinutes >= 1
            ? $"{value.TotalMinutes:0.#}m"
            : $"{value.TotalSeconds:0.#}s";
    }

    private static string Quote(string value) =>
        value.IndexOfAny([' ', '\t', '\r', '\n', '"']) < 0
            ? value
            : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
