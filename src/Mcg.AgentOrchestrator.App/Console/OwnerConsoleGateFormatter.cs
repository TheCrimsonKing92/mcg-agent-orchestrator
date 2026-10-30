namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGateFormatter
{
    internal static string Remaining(int seconds) => seconds is > 0 and < 60 ? "~<1m" : $"~{(int)Math.Ceiling(seconds / 60d)}m";
    internal static string ProgressLine(OwnerConsoleGateProgress gate)
    {
        var fraction = Math.Clamp(gate.Fraction, 0, 1);
        var cells = (int)Math.Floor(fraction * 10);
        return $"[{new string('#', cells)}{new string('-', 10 - cells)}] estimate {(int)Math.Floor(fraction * 100)}%  {Remaining(gate.RemainingSeconds)} left";
    }

    internal static string StatusSegment(OwnerConsoleGateProgress gate)
    {
        if (!gate.Live) return "gate: idle" + (gate.IdleAge is { } age ? $" {(int)age.TotalMinutes}m" : "");
        var members = string.Join("+", gate.Members.Take(3).Select(member => member.Id[..Math.Min(4, member.Id.Length)]));
        if (gate.Members.Count > 3) members += "+" + (gate.Members.Count - 3);
        return $"gate: {gate.Kind} {members} {gate.DoneCount}/{gate.TotalCount} {(gate.Failing ? "FAILING" : Remaining(gate.RemainingSeconds))}";
    }

    internal static IReadOnlyList<string> Lines(OwnerConsoleGateProgress? gate) => OwnerConsoleGateLayout.Lines(gate, OwnerConsoleEpicFormatter.DefaultWidth, false);

    internal static string LaneLine(OwnerConsoleGateLane lane)
    {
        if (lane.State == OwnerConsoleGateLaneState.Skipped) return $"- {lane.Name} skipped: {lane.SkipReason}";
        var marker = Marker(lane);
        var median = lane.MedianSeconds is { } seconds ? $"{seconds:0.#}s" + (lane.IsEstimate ? " estimate" : "") : "?";
        var quiet = lane.State == OwnerConsoleGateLaneState.Running && lane.QuietSeconds is > 300
            ? $" quiet {(int)(lane.QuietSeconds.Value / 60)}m" : "";
        var outcome = lane.State == OwnerConsoleGateLaneState.Done ? lane.Outcome is "passed" or "failed" ? lane.Outcome : "done" : "";
        var name = lane.IsAttribution ? AttributionName(lane) : lane.Name;
        return $"{marker} {name} {(int)lane.Elapsed.TotalSeconds}s / {median} {lane.Location} {outcome}".TrimEnd() + quiet;
    }

    internal static char Marker(OwnerConsoleGateLane lane) => lane.State switch
    {
        OwnerConsoleGateLaneState.Running => '>', OwnerConsoleGateLaneState.Queued => '.',
        OwnerConsoleGateLaneState.Skipped => '-',
        OwnerConsoleGateLaneState.Cached => '=', OwnerConsoleGateLaneState.Done => lane.Outcome switch
        { "passed" => '+', "failed" => 'x', _ => '*' },
        _ => throw new ArgumentOutOfRangeException(nameof(lane))
    };

    internal static string AttributionName(OwnerConsoleGateLane lane)
    {
        var target = lane.Target ?? lane.Name;
        var prefix = target.StartsWith("reviewer focused evidence: ", StringComparison.Ordinal)
            ? "reviewer focused evidence: " : "reviewer mapped project evidence: ";
        return "confirming failure: " + (target.StartsWith(prefix, StringComparison.Ordinal) ? target[prefix.Length..] : target);
    }
}
