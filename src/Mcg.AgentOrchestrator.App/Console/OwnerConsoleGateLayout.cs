using System.Globalization;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Presentation only: all observations, including failure names and host ages, arrive in the snapshot.
internal static class OwnerConsoleGateLayout
{
    private enum RowKind { Lane, Check, Confirming, Baseline }

    internal static IReadOnlyList<string> Lines(OwnerConsoleGateProgress? gate, int width, bool expanded)
    {
        width = Math.Max(1, width);
        if (gate is null) return [Fit("gate: unavailable", width)];
        var lines = new List<string>();
        if (!gate.Live) lines.Add(OwnerConsoleGateFormatter.StatusSegment(gate));
        else
        {
            var ordinary = gate.Lanes.Where(row => Kind(row) != RowKind.Baseline).ToArray();
            var counted = ordinary.Where(row => Kind(row) is RowKind.Lane or RowKind.Check).ToArray();
            var failed = ordinary.Where(Failed).ToArray();
            var running = ordinary.Where(row => row.State == OwnerConsoleGateLaneState.Running).ToArray();
            var queued = ordinary.Where(row => row.State == OwnerConsoleGateLaneState.Queued).ToArray();
            var baseline = gate.Lanes.Where(row => Kind(row) == RowKind.Baseline).ToArray();
            var passed = ordinary.Where(Passed).ToArray();
            var printed = failed.Concat(running).Concat(queued)
                .Concat(expanded ? baseline.Concat(passed) : []).ToArray();
            var columns = new Columns(printed, width);
            lines.Add($"GATE {gate.Kind} | elapsed {(int)gate.Elapsed.TotalMinutes}m" + (gate.Failing ? " | FAILING" : ""));
            lines.Add(string.Join(" + ", gate.Members.Select(member => member.Id + (member.Title.Length == 0 ? "" : " " + member.Title))));
            lines.Add($" x {counted.Count(Failed)} failed   > {counted.Count(row => row.State == OwnerConsoleGateLaneState.Running)} running   + {counted.Count(row => row.State == OwnerConsoleGateLaneState.Done && row.Outcome == "passed")} passed   = {counted.Count(row => row.State == OwnerConsoleGateLaneState.Cached)} cached   . {counted.Count(row => row.State == OwnerConsoleGateLaneState.Queued)} queued");
            if (!gate.Failing) lines.Add(OwnerConsoleGateFormatter.ProgressLine(gate));
            Section("FAILED", failed, failureNames: true);
            Section("RUNNING (longest pole first)", running, sort: true);
            Section("QUEUED", queued);
            var toggle = expanded ? "(Enter to collapse)" : "(Enter to expand)";
            if (baseline.Length > 0)
            {
                var counts = Counts((baseline.Count(Failed), "failed"),
                    (baseline.Count(row => row.State == OwnerConsoleGateLaneState.Running), "running"),
                    (baseline.Count(row => row.State == OwnerConsoleGateLaneState.Queued), "queued"),
                    (baseline.Count(Passed), "passed"));
                lines.Add($"BASELINE  {baseline.Length} comparison runs" + (counts.Length == 0 ? "" : ": " + counts) + " " + toggle);
                if (expanded)
                    foreach (var row in baseline.OrderBy(BaselineOrder)) lines.Add(columns.Row(row));
            }
            if (passed.Length > 0)
            {
                lines.Add("PASSED  " + Counts((passed.Count(row => Kind(row) == RowKind.Lane), "lanes"),
                    (passed.Count(row => Kind(row) == RowKind.Check), "checks"),
                    (passed.Count(row => Kind(row) == RowKind.Confirming), "confirmations")) + " " + toggle);
                if (expanded) Section(null, passed);
            }
            var unknown = ordinary.Count(row => row.State == OwnerConsoleGateLaneState.Done && row.Outcome is not ("passed" or "failed"));
            if (unknown > 0) lines.Add($"DONE  {unknown} without a recorded outcome");
            var locations = ordinary.Where(row => Kind(row) != RowKind.Confirming &&
                    row.State is OwnerConsoleGateLaneState.Running or OwnerConsoleGateLaneState.Done && row.Location.Length > 0)
                .GroupBy(row => row.Location, StringComparer.Ordinal).ToArray();
            if (locations.Length > 0)
                lines.Add("WHERE   " + string.Join("   ", locations.Select(group =>
                    (group.Key.StartsWith("local slot ", StringComparison.Ordinal) ? "local " + group.Key[11..] : group.Key) +
                    " " + Bar((double)group.Count(row => row.State == OwnerConsoleGateLaneState.Done) / group.Count()))));

            void Section(string? heading, OwnerConsoleGateLane[] rows, bool sort = false, bool failureNames = false)
            {
                if (rows.Length == 0) return;
                if (heading is not null) lines.Add(heading);
                var groups = rows.GroupBy(Kind).OrderBy(group => group.Key).ToArray();
                foreach (var group in groups)
                {
                    if (groups.Length > 1) lines.Add("  " + (group.Key switch
                    { RowKind.Lane => "lanes", RowKind.Check => "checks", _ => "confirming" }));
                    var ordered = sort ? group.OrderBy(RunningOrder).ThenByDescending(RunningPole).ThenBy(row => row.Name, StringComparer.Ordinal) : group.AsEnumerable();
                    foreach (var row in ordered)
                    {
                        lines.Add(columns.Row(row));
                        if (!failureNames || Kind(row) == RowKind.Confirming || row.FailingTestNames is not { } names) continue;
                        lines.AddRange(names.Take(3).Select(name => "     " + name));
                        if (names.Count > 3) lines.Add($"     +{names.Count - 3} more");
                    }
                }
            }
        }
        foreach (var other in gate.OtherLiveGates ?? [])
            lines.Add($"ALSO ON HOST  gate {other.Kind} {Members(other)} {(int)other.Elapsed.TotalMinutes}m");
        foreach (var run in gate.HostActivity ?? [])
            lines.Add($"ALSO ON HOST  {run.GoalId[..Math.Min(8, run.GoalId.Length)]} evidence" +
                (run.Label.Length == 0 ? "" : " (" + run.Label + ")") + $" {(int)run.Age.TotalMinutes}m");
        return lines.Select(line => Fit(Ascii(line), width)).ToArray();
    }

    private static RowKind Kind(OwnerConsoleGateLane row) => row.IsAttribution ? RowKind.Confirming :
        row.Name.EndsWith(" [trusted main baseline]", StringComparison.Ordinal) ? RowKind.Baseline :
        row.IsManifestLane ? RowKind.Lane : RowKind.Check;
    private static bool Failed(OwnerConsoleGateLane row) => row.State == OwnerConsoleGateLaneState.Done && row.Outcome == "failed";
    private static bool Passed(OwnerConsoleGateLane row) => row.State == OwnerConsoleGateLaneState.Cached ||
        row.State == OwnerConsoleGateLaneState.Done && row.Outcome == "passed";
    private static int BaselineOrder(OwnerConsoleGateLane row) => Failed(row) ? 0 : row.State switch
    { OwnerConsoleGateLaneState.Running => 1, OwnerConsoleGateLaneState.Queued => 2, _ => 3 };
    private static int RunningOrder(OwnerConsoleGateLane row) => row.MedianSeconds is not > 0 ? 2 : row.Elapsed.TotalSeconds > row.MedianSeconds ? 0 : 1;
    private static double RunningPole(OwnerConsoleGateLane row) => RunningOrder(row) switch
    { 0 => row.Elapsed.TotalSeconds - row.MedianSeconds!.Value, 1 => row.MedianSeconds!.Value - row.Elapsed.TotalSeconds, _ => row.Elapsed.TotalSeconds };
    private static string Counts(params (int Count, string Label)[] counts) => string.Join(", ", counts.Where(item => item.Count > 0).Select(item => $"{item.Count} {item.Label}"));
    private static string Members(OwnerConsoleGateProgress gate) => string.Join("+", gate.Members.Take(3).Select(member => member.Id[..Math.Min(8, member.Id.Length)])) +
        (gate.Members.Count > 3 ? "+" + (gate.Members.Count - 3) : "");
    private static string Ascii(string value) => new(value.Select(c => c is >= ' ' and <= '~' || c == '…' ? c : '?').ToArray());
    private static string Fit(string value, int width) => value.Length <= width ? value : value[..(width - 1)] + "…";
    private static string Bar(double fraction)
    {
        var filled = double.IsFinite(fraction) ? (int)Math.Floor(Math.Clamp(fraction, 0, 1) * 10) : 0;
        return "[" + new string('#', filled) + new string('-', 10 - filled) + "]";
    }

    private sealed class Columns
    {
        private readonly int _name, _elapsed, _median, _location, _outcome;
        internal Columns(OwnerConsoleGateLane[] rows, int width)
        {
            int Max(Func<OwnerConsoleGateLane, string> value) => rows.Select(row => value(row).Length).DefaultIfEmpty(0).Max();
            _elapsed = Max(Elapsed); _median = Max(Median); _location = Max(row => row.Location); _outcome = Max(Outcome);
            var fixedWidth = 3 + 2 + _elapsed + 3 + _median + 2 + _location + 2 + _outcome;
            _name = Math.Max(12, Math.Min(Math.Min(33, Max(Name)), width - fixedWidth));
        }
        internal string Row(OwnerConsoleGateLane row) =>
            $" {OwnerConsoleGateFormatter.Marker(row)} {Fit(Ascii(Name(row)), _name).PadRight(_name)}  {Elapsed(row).PadLeft(_elapsed)} / {Median(row).PadRight(_median)}  {Ascii(row.Location).PadRight(_location)}  {Outcome(row).PadRight(_outcome)}".TrimEnd();
        private static string Name(OwnerConsoleGateLane row) => row.IsAttribution ? OwnerConsoleGateFormatter.AttributionName(row) : row.Name;
        private static string Elapsed(OwnerConsoleGateLane row) => row.State == OwnerConsoleGateLaneState.Queued ? "-" : ((int)row.Elapsed.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
        private static string Median(OwnerConsoleGateLane row) => row.MedianSeconds is { } seconds ? seconds.ToString("0.#", CultureInfo.InvariantCulture) + "s" : "?";
        private static string Outcome(OwnerConsoleGateLane row) => row.State switch
        {
            OwnerConsoleGateLaneState.Running => Bar(row.MedianSeconds is > 0 ? row.Elapsed.TotalSeconds / row.MedianSeconds.Value : 0) +
                (row.QuietSeconds is > 300 ? $" quiet {(int)(row.QuietSeconds.Value / 60)}m" : ""),
            OwnerConsoleGateLaneState.Done => row.Outcome is "passed" or "failed" ? row.Outcome : "done",
            OwnerConsoleGateLaneState.Cached => "cached", OwnerConsoleGateLaneState.Queued => "",
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
    }
}
