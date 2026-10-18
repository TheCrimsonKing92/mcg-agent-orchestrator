using System.Globalization;
using Terminal.Gui.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerFleetDiagram
{
    internal const int DefaultWidth = 60;
    internal const int MinimumWidth = 40;

    // Slots describe configured capacity, not live occupancy; peak is historical.
    internal static string Render(OwnerFleetViewModel model, int width = DefaultWidth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, MinimumWidth);
        var lines = new List<string>();
        var host = model.Host;
        var content = new List<string>
        {
            "Orchestrator host",
            $"conductor: {(host.ConductorRunning ? "running" : "stopped")}",
            $"executors: {host.ExecutorCount}"
        };
        var skipRow = host.NoExecutorFreeCount > 0 ? content.Count : -1;
        if (host.NoExecutorFreeCount > 0)
            content.Add($"shadow skipped, no free slot: {host.NoExecutorFreeCount} in last 24h (last {Stamp(host.NoExecutorFreeLastAt)})");
        if (host.ExecutorCount == 0) content.Add("no executors configured");
        if (host.UnreadableOutcomeLines != 0 || host.UnreadableProbeLines != 0)
        {
            content.Add($"unreadable ledger lines: outcomes {host.UnreadableOutcomeLines}");
            content.Add($"unreadable ledger lines: probes {host.UnreadableProbeLines}");
        }
        Box(content, false, wrapRow: skipRow);
        foreach (var executor in model.Executors)
        {
            lines.Add("│" + new string(' ', width - 1));
            content = [$"Executor {executor.Id}"];
            content.Add(executor.Reachability switch
            {
                OwnerFleetReachability.Reachable => "reachability: reachable",
                OwnerFleetReachability.Unreachable => "reachability: UNREACHABLE !",
                _ => "reachability: unknown (no probe)"
            });
            if (executor.ProbeAt is { } probeAt) content.Add($"probe: {Stamp(probeAt)}");
            if (executor.ProbeReasons.Count > 0) content.Add("reasons: " + string.Join(", ", executor.ProbeReasons));
            if (executor.ConfiguredSlots is { } slots)
            {
                content.Add($"slots: {slots} configured (occupancy unknown)");
                var perRow = (width - 4) / 3;
                for (var offset = 0; offset < slots; offset += perRow)
                    content.Add(string.Concat(Enumerable.Repeat("[ ]", Math.Min(perRow, slots - offset))));
            }
            else content.Add("slots: not configured");
            if (executor.SlotsFile is { } sync)
                content.Insert(content.FindIndex(row => row.StartsWith("slots:", StringComparison.Ordinal)) + 1,
                    sync.Outcome == "synced" ? $"slots file: synced {sync.Slots} {Stamp(sync.ObservedAt)}" :
                    $"slots file: FAILED {sync.Reason} {Stamp(sync.ObservedAt)}");
            content.Add($"peak concurrent attempts: {executor.PeakConcurrentAttempts}");
            content.Add(executor.LastOutcome is null ? "last: none" : $"last: {executor.LastOutcome} {Stamp(executor.LastOutcomeAt)}");
            content.Add("accepted: " + (executor.AcceptedShare is { } share ? share.ToString("P0", CultureInfo.InvariantCulture) : "n/a"));
            if (executor.Lanes.Count == 0) content.Add("lanes: no accepted remote runs");
            var laneStart = content.Count;
            foreach (var lane in executor.Lanes)
            {
                content.Add($"{lane.Lane} median {Seconds(lane.MedianSeconds)} n={lane.Samples}");
                content.Add($"  solo {Seconds(lane.SoloMedianSeconds)} conc {Seconds(lane.ConcurrentMedianSeconds)}");
            }
            Box(content, executor.Reachability == OwnerFleetReachability.Unreachable, laneStart);
        }
        return string.Join("\n", lines);

        void Box(IReadOnlyList<string> rows, bool unreachable, int laneStart = int.MaxValue, int wrapRow = -1)
        {
            var edge = unreachable ? '═' : '─';
            var side = unreachable ? '║' : '│';
            lines.Add((unreachable ? "╔" : "┌") + new string(edge, width - 2) + (unreachable ? "╗" : "┐"));
            for (var index = 0; index < rows.Count; index++)
            {
                // Ledger text must not inject terminal controls or extra diagram rows.
                var clean = new string(rows[index].Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
                var fittedRows = index >= laneStart || index == wrapRow ? OwnerFleetLaneWrap.Wrap(clean, width - 4) :
                    [OwnerConsoleLineFitter.Cut(clean, width - 4)];
                foreach (var fitted in fittedRows)
                    lines.Add($"{side} {fitted}{new string(' ', width - 4 - fitted.GetColumns())} {side}");
            }
            lines.Add((unreachable ? "╚" : "└") + new string(edge, width - 2) + (unreachable ? "╝" : "┘"));
        }
    }

    private static string Stamp(DateTimeOffset? at) => at?.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture) ?? "-";
    private static string Seconds(double? seconds) => seconds?.ToString("0.0", CultureInfo.InvariantCulture) + (seconds.HasValue ? "s" : "-");
}
