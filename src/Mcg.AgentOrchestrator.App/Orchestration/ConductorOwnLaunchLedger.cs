using System.Collections.Concurrent;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Instance-local knowledge must disappear on restart so a successor still inspects the holder.
internal sealed class ConductorOwnLaunchLedger
{
    private readonly ConcurrentDictionary<string, byte> _attemptIds = new(StringComparer.Ordinal);

    internal void Record(string attemptId) => _attemptIds.TryAdd(attemptId, 0);

    internal bool Contains(string attemptId) => _attemptIds.ContainsKey(attemptId);
}
