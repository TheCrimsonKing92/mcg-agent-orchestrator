namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class DispatchHeartbeatProcessIdentityTracker
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, SpawnProcessIdentity> _identities = [];

    public IReadOnlyList<SpawnProcessIdentity> Capture(
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
            foreach (var identity in captured)
            {
                _identities[identity.ProcessId] = identity;
            }

            return _identities.Values
                .OrderBy(identity => identity.ProcessId)
                .ToArray();
        }
    }
}
