namespace Mcg.AgentOrchestrator.App.Simulation;

internal static class BoardSimulationCalibration
{
    internal static BoardCalibrationReport Compare(BoardHistoryTrace recorded,
        BoardSimulationResult simulated, int countTolerance = 1, TimeSpan? timeTolerance = null)
    {
        if (countTolerance < 0 || timeTolerance < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(countTolerance));
        var actual = recorded.Landings.ToDictionary(l => l.GoalId, l => l.LandedAt, StringComparer.Ordinal);
        var replay = simulated.Landings.ToDictionary(l => l.GoalId, l => l.LandedAt, StringComparer.Ordinal);
        var matched = actual.Keys.Intersect(replay.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(id => new LandingDelta(id, (replay[id] - actual[id]).TotalMinutes)).ToArray();
        var deltas = matched.Select(m => Math.Abs(m.DeltaMinutes)).Order().ToArray();
        var difference = replay.Count - actual.Count;
        return new(actual.Count, replay.Count, difference, matched,
            actual.Keys.Except(replay.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            replay.Keys.Except(actual.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            deltas.LastOrDefault(), Median(deltas),
            Math.Abs(difference) <= countTolerance &&
            deltas.All(d => d <= (timeTolerance ?? TimeSpan.FromMinutes(30)).TotalMinutes)
                ? "within-bar" : "outside-bar");
    }

    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }
}
