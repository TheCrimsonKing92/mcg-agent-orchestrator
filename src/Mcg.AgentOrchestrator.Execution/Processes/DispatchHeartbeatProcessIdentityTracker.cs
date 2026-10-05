namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class DispatchHeartbeatProcessIdentityTracker
{
    private readonly Lock _gate = new();
    private readonly HashSet<SpawnProcessIdentity> _identities = [];

    public DispatchHeartbeatProcessIdentitySnapshot Capture(
        int hostProcessId,
        IEnumerable<int> ownedProcessIds,
        Func<IReadOnlyList<int>?> readCurrentOwnedProcessIds,
        Func<int, SpawnProcessIdentity?>? readCurrentIdentity = null)
    {
        var captured = DispatchProcessHost.CaptureHeartbeatProcessIdentities(
            hostProcessId,
            ownedProcessIds,
            readCurrentOwnedProcessIds,
            readCurrentIdentity);
        lock (_gate)
        {
            var current = new List<SpawnProcessIdentity>();
            foreach (var identity in captured)
            {
                var matchingPrior = _identities
                    .Where(candidate => candidate.ProcessId == identity.ProcessId)
                    .FirstOrDefault(prior =>
                    SpawnProcessIdentityReader.EvaluateRecordedIdentity(prior, identity, out _) ==
                    SpawnTrackedProcessStatus.LiveMatch);
                var accepted = matchingPrior ?? identity;
                _identities.Add(accepted);
                current.Add(accepted);
            }

            return new DispatchHeartbeatProcessIdentitySnapshot(
                current.OrderBy(identity => identity.ProcessId).ToArray(),
                _identities
                    .OrderBy(identity => identity.ProcessId)
                    .ThenBy(identity => identity.StartedAt)
                    .ToArray());
        }
    }
}

internal sealed record DispatchHeartbeatProcessIdentitySnapshot(
    IReadOnlyList<SpawnProcessIdentity> Current,
    IReadOnlyList<SpawnProcessIdentity> Recorded);
