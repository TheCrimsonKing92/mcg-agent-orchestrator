namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGateFormatter
{
    internal static string Remaining(int seconds) => seconds is > 0 and < 60 ? "~<1m" : $"~{(int)Math.Ceiling(seconds / 60d)}m";
    internal static string ProgressLine(OwnerConsoleGateProgress gate)
    {
        var fraction = Math.Clamp(gate.Fraction, 0, 1);
        var cells = (int)Math.Floor(fraction * 10);
        return $"[{new string('#', cells)}{new string('-', 10 - cells)}] {(int)Math.Floor(fraction * 100)}%  {Remaining(gate.RemainingSeconds)} left";
    }

    internal static string StatusSegment(OwnerConsoleGateProgress gate)
    {
        if (!gate.Live) return "gate: idle" + (gate.IdleAge is { } age ? $" {(int)age.TotalMinutes}m" : "");
        var members = string.Join("+", gate.Members.Take(3).Select(member => member.Id[..Math.Min(4, member.Id.Length)]));
        if (gate.Members.Count > 3) members += "+" + (gate.Members.Count - 3);
        return $"gate: {gate.Kind} {members} {gate.DoneCount}/{gate.TotalCount} {Remaining(gate.RemainingSeconds)}";
    }

    internal static IReadOnlyList<string> Lines(OwnerConsoleGateProgress? gate)
    {
        if (gate is null) return ["gate: unavailable"];
        if (!gate.Live) return [StatusSegment(gate)];
        return new[] { $"GATE {gate.Kind} | elapsed {(int)gate.Elapsed.TotalMinutes}m",
            string.Join(" + ", gate.Members.Select(member => member.Id + (member.Title.Length == 0 ? "" : " " + member.Title))),
            ProgressLine(gate) }.Concat(gate.Lanes.Select(LaneLine)).ToArray();
    }

    internal static string LaneLine(OwnerConsoleGateLane lane)
    {
        var marker = lane.State switch
        {
            OwnerConsoleGateLaneState.Running => '>', OwnerConsoleGateLaneState.Queued => '.',
            OwnerConsoleGateLaneState.Cached => '=', OwnerConsoleGateLaneState.Done => lane.Outcome switch
            { "passed" => '+', "failed" => 'x', _ => '*' },
            _ => throw new ArgumentOutOfRangeException(nameof(lane))
        };
        var median = lane.MedianSeconds is { } seconds ? $"{seconds:0.#}s" + (lane.IsEstimate ? " estimate" : "") : "?";
        var quiet = lane.State == OwnerConsoleGateLaneState.Running && lane.QuietSeconds is > 300
            ? $" quiet {(int)(lane.QuietSeconds.Value / 60)}m" : "";
        var outcome = lane.State == OwnerConsoleGateLaneState.Done ? lane.Outcome is "passed" or "failed" ? lane.Outcome : "done" : "";
        return $"{marker} {lane.Name} {(int)lane.Elapsed.TotalSeconds}s / {median} {lane.Location} {outcome}".TrimEnd() + quiet;
    }
}
