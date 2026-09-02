namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum EvidenceArtifactFamily
{
    DispatchLogs,
    AcceptanceGateAttempts,
    PreReviewEvidenceAttempts,
    Prompts,
    GoalEvents,
    GoalOperationJournals,
    MtpTestRuns,
    RunEvents
}

internal enum EvidenceRetentionAction
{
    Preserved,
    Deleted,
    Compressed,
    ReceiptWritten,
    DeferredLive,
    DeferredLocked,
    DeferredLease,
    RetainedUndecidable,
    Failed
}

internal enum EvidenceOwnerResolution
{
    UniqueTerminal,
    NonTerminal,
    AmbiguousPrefix,
    Unmatched,
    Unrecorded
}

internal sealed record EvidenceRetentionDecision(
    EvidenceArtifactFamily Family,
    EvidenceRetentionAction Action,
    string Path,
    string? GoalId,
    EvidenceOwnerResolution OwnerResolution,
    string Reason,
    string? AttemptId = null,
    int? AttemptOrdinal = null,
    long BytesAttempted = 0,
    long BytesReclaimed = 0,
    string? FailureExceptionType = null);

internal static class EvidenceRetentionPolicy
{
    internal const int Version = 1;

    internal static IReadOnlySet<string> ProtectedAttemptIds(
        IReadOnlyCollection<RetentionAttemptIdentity> attempts)
    {
        var ordered = attempts
            .OrderByDescending(attempt => attempt.Ordinal)
            .ThenByDescending(attempt => attempt.StartedAt)
            .ThenByDescending(attempt => attempt.AttemptId, StringComparer.Ordinal)
            .ToArray();
        var protectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ordered.FirstOrDefault() is { } final)
        {
            protectedIds.Add(final.AttemptId);
        }

        if (ordered.FirstOrDefault(attempt => attempt.Failed) is { } failed)
        {
            protectedIds.Add(failed.AttemptId);
        }

        return protectedIds;
    }

    internal static IReadOnlySet<string> AttemptIdsPastCountBound(
        IReadOnlyCollection<RetentionAttemptIdentity> attempts,
        int retainCount,
        IReadOnlySet<string> protectedAttemptIds) =>
        attempts
            .OrderByDescending(attempt => attempt.Ordinal)
            .ThenByDescending(attempt => attempt.StartedAt)
            .ThenByDescending(attempt => attempt.AttemptId, StringComparer.Ordinal)
            .Skip(Math.Max(0, retainCount))
            .Select(attempt => attempt.AttemptId)
            .Where(id => !protectedAttemptIds.Contains(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

internal sealed record RetentionAttemptIdentity(
    string AttemptId,
    int Ordinal,
    DateTimeOffset StartedAt,
    bool Failed,
    bool Reconciled);
