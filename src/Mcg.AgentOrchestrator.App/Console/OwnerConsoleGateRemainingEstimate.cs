namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGateRemainingEstimate
{
    internal static double Makespan(IReadOnlyList<OwnerConsoleGateLane> unfinishedManifestLanes, int concurrency)
    {
        concurrency = Math.Max(1, concurrency);
        // Preserve the original addition order (and truncation) for a serial gate.
        if (concurrency == 1) return unfinishedManifestLanes.Sum(Remaining);
        if (unfinishedManifestLanes.Count == 0) return 0;

        var slots = new double[Math.Min(concurrency, unfinishedManifestLanes.Count)];
        foreach (var lane in unfinishedManifestLanes.Where(lane => lane.State == OwnerConsoleGateLaneState.Running))
            Place(Remaining(lane));
        foreach (var seconds in unfinishedManifestLanes.Where(lane => lane.State == OwnerConsoleGateLaneState.Queued)
                     .Select(Remaining).OrderByDescending(seconds => seconds))
            Place(seconds);
        return slots.Max();

        void Place(double seconds)
        {
            var earliest = 0;
            for (var index = 1; index < slots.Length; index++)
                if (slots[index] < slots[earliest]) earliest = index;
            slots[earliest] += seconds;
        }
    }

    private static double Remaining(OwnerConsoleGateLane lane) => lane.State == OwnerConsoleGateLaneState.Running
        ? Math.Max(0, (lane.MedianSeconds ?? 0) - lane.Elapsed.TotalSeconds)
        : lane.MedianSeconds ?? 0;
}
