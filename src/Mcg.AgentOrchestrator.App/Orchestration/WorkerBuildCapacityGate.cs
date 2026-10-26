using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record WorkerBuildCapacitySnapshot(int UnheldPermits, int TotalPermits, int WaitingTickets, bool Readable)
{
    internal static WorkerBuildCapacitySnapshot Unreadable { get; } = new(0, 0, 0, false);
}

internal enum WorkerBuildCapacityVerdict { Retry, Hold, Exhausted }

// This advisory snapshot reserves nothing; the host's permit acquisition remains authoritative.
internal static class WorkerBuildCapacityGate
{
    internal const int MaxCapacityRetries = 3;

    internal static WorkerBuildCapacityVerdict Decide(WorkerBuildCapacitySnapshot snapshot, int capacityDispatchCount) =>
        capacityDispatchCount > MaxCapacityRetries ? WorkerBuildCapacityVerdict.Exhausted :
        !snapshot.Readable || snapshot.UnheldPermits > snapshot.WaitingTickets ? WorkerBuildCapacityVerdict.Retry :
        WorkerBuildCapacityVerdict.Hold;

    internal static WorkerBuildCapacitySnapshot ReadHostSnapshot(DotnetBuildStorageRoot storageRoot,
        TimeProvider timeProvider, Func<string, bool> isExecutionLockHeld)
    {
        try
        {
            var queue = new BuildPermitWaitQueue(storageRoot, timeProvider, isExecutionLockHeld: isExecutionLockHeld);
            var tickets = queue.Snapshot();
            // Snapshot hides fresh corrupt tickets. Inspect the remaining files without rewriting them.
            foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(queue.TicketPath("snapshot"))!, "*.json"))
            {
                var ticket = JsonSerializer.Deserialize<BuildPermitWaitQueue.Ticket>(File.ReadAllText(path));
                if (ticket is not { Key.Length: > 0, OwnerPid: > 0 } || !Enum.IsDefined(ticket.Kind) ||
                    ticket.FirstWaitedAt == default || ticket.LastRefreshedAt < ticket.FirstWaitedAt)
                    return WorkerBuildCapacitySnapshot.Unreadable;
            }
            var total = DotnetBuildEnvironmentManager.BuildConcurrencySlotCount;
            var unheld = Enumerable.Range(0, total).Count(index => !isExecutionLockHeld(
                DotnetBuildEnvironmentManager.BuildSlotExecutionLockPath(index, storageRoot)));
            return new(unheld, total, tickets.Count, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return WorkerBuildCapacitySnapshot.Unreadable;
        }
    }

    // Same execution-lock probe as DotnetBuildEnvironmentManager.IsExecutionLockHeld.
    internal static bool IsExecutionLockHeld(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            stream.Lock(0, 1);
            stream.Unlock(0, 1);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
