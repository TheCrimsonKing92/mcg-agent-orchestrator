using System.Diagnostics.CodeAnalysis;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class GoalGitFactSnapshotCache
{
    private const int MaxConsecutiveHits = 20;
    private readonly object _sync = new();
    private Entry? _entry;
    private int _consecutiveHits;

    private sealed record Entry(
        string ExecutionDirectory, string IntegrationBranch, string Fingerprint,
        GoalGitFactSnapshot Snapshot);

    internal bool TryGet(
        string executionDirectory, string integrationBranch, string? fingerprint,
        [NotNullWhen(true)] out GoalGitFactSnapshot? snapshot)
    {
        lock (_sync)
        {
            snapshot = null;
            if (fingerprint is null || _entry is null || _consecutiveHits >= MaxConsecutiveHits ||
                !string.Equals(_entry.ExecutionDirectory, executionDirectory, StringComparison.Ordinal) ||
                !string.Equals(_entry.IntegrationBranch, integrationBranch, StringComparison.Ordinal) ||
                !string.Equals(_entry.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            _consecutiveHits++;
            snapshot = _entry.Snapshot;
            return true;
        }
    }

    internal void Store(
        string executionDirectory, string integrationBranch, string? fingerprint,
        GoalGitFactSnapshot snapshot)
    {
        if (fingerprint is null || !snapshot.EvidenceAvailable)
        {
            return;
        }

        lock (_sync)
        {
            _entry = new(executionDirectory, integrationBranch, fingerprint, snapshot);
            _consecutiveHits = 0;
        }
    }
}
