namespace Mcg.AgentOrchestrator.App.Orchestration;

// In-memory evidence of successful writes, owned by one batch loop. Keep the last pair across
// self-clear so a transient clean observation cannot write the same conflict again.
internal sealed class PreLandingMergeConflictEscalationLedger
{
    private readonly Dictionary<string, string> _fingerprints = new(StringComparer.Ordinal);

    internal bool IsEscalated(string goalId, string fingerprint) =>
        _fingerprints.TryGetValue(goalId, out var recorded) && recorded == fingerprint;

    internal void Record(string goalId, string fingerprint) => _fingerprints[goalId] = fingerprint;
}
