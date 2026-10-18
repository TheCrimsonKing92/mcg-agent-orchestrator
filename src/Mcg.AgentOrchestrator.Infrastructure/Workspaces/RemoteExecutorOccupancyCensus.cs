namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteExecutorOccupancyEntry(string ExecutorId, int Gate, int FocusedEvidence,
    int Shadow, int Screening, int Unclassified, string State);

// A diagnostic snapshot, never an authority for allocating a slot.
internal static class RemoteExecutorOccupancyCensus
{
    internal static IReadOnlyList<RemoteExecutorOccupancyEntry> Take(string root,
        IEnumerable<(string ExecutorId, string State)> executors, Func<int, DateTimeOffset?>? startTimeOf = null) =>
        executors.Select(executor =>
        {
            var counts = new int[5];
            foreach (var marker in RemoteExecutorOccupancy.LiveMarkers(root, executor.ExecutorId, startTimeOf))
            {
                var index = Enum.TryParse<RemoteExecutorClaimClass>(marker.Class, true, out var claimClass) &&
                    Enum.IsDefined(claimClass) && string.Equals(marker.Class, claimClass.ToString(), StringComparison.OrdinalIgnoreCase) ? (int)claimClass :
                    marker.Attempt.StartsWith("screening-", StringComparison.Ordinal) ? 3 : 4;
                counts[index]++;
            }
            return new RemoteExecutorOccupancyEntry(executor.ExecutorId, counts[0], counts[1], counts[2],
                counts[3], counts[4], executor.State);
        }).ToArray();

    internal static string Reason(IReadOnlyList<RemoteExecutorOccupancyEntry> entries) =>
        "no-idle-slot" + string.Concat(entries.Select(entry =>
            $" {entry.ExecutorId}=gate:{entry.Gate},focused-evidence:{entry.FocusedEvidence},shadow:{entry.Shadow}," +
            $"screening:{entry.Screening},unclassified:{entry.Unclassified},state:{entry.State}"));
}
