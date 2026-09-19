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

internal enum EvidenceOwnershipSource
{
    Declared,
    InferredFromName,
    Unresolved
}

internal enum EvidenceEligibility
{
    Keep,
    Archive,
    DeleteWhenSafe
}

internal sealed record EvidenceRetentionEligibility(
    EvidenceEligibility Disposition,
    string Reason,
    string? AttemptId,
    int? AttemptOrdinal,
    EvidenceOwnershipSource OwnershipSource,
    int PolicyVersion,
    string FactRevision);

internal sealed record EvidenceRetentionFacts(
    bool TerminalGoal,
    RetentionAttemptIdentity? Owner,
    EvidenceOwnershipSource OwnershipSource,
    bool ProtectedAttempt,
    bool ReferencedArtifact,
    bool CountBound,
    bool Aged,
    bool ByteBoundEligible,
    string FactRevision,
    bool RequireOwner = true);

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
    string? FailureExceptionType = null,
    EvidenceOwnershipSource OwnershipSource = EvidenceOwnershipSource.Unresolved,
    string? FactRevision = null,
    EvidenceEligibility? Eligibility = null);

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

    internal static bool OwnsPath(
        RetentionAttemptIdentity attempt,
        string path,
        out EvidenceOwnershipSource source)
    {
        if (attempt.DeclaredPaths is { Count: > 0 })
        {
            source = EvidenceOwnershipSource.Declared;
            var fullPath = Path.GetFullPath(path);
            return attempt.DeclaredPaths.Any(declared =>
                Path.GetFullPath(declared).Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        }

        source = EvidenceOwnershipSource.InferredFromName;
        return Path.GetFileName(path).StartsWith(attempt.AttemptId + ".", StringComparison.OrdinalIgnoreCase) ||
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals(attempt.AttemptId + ".receipts", StringComparison.OrdinalIgnoreCase));
    }

    internal static RetentionPathOwner ResolveOwner(
        IReadOnlyCollection<RetentionAttemptIdentity> attempts,
        string path)
    {
        var declared = attempts
            .Where(attempt => attempt.DeclaredPaths is { Count: > 0 } &&
                OwnsPath(attempt, path, out _))
            .ToArray();
        if (declared.Length == 1)
        {
            return new RetentionPathOwner(declared[0], EvidenceOwnershipSource.Declared, Ambiguous: false);
        }

        if (declared.Length > 1)
        {
            return new RetentionPathOwner(null, EvidenceOwnershipSource.Declared, Ambiguous: true);
        }

        var inferred = attempts
            .Where(attempt => attempt.DeclaredPaths is not { Count: > 0 } &&
                OwnsPath(attempt, path, out _))
            .ToArray();
        return inferred.Length switch
        {
            1 => new RetentionPathOwner(inferred[0], EvidenceOwnershipSource.InferredFromName, Ambiguous: false),
            > 1 => new RetentionPathOwner(null, EvidenceOwnershipSource.InferredFromName, Ambiguous: true),
            _ => new RetentionPathOwner(null, EvidenceOwnershipSource.Unresolved, Ambiguous: false)
        };
    }

    internal static EvidenceRetentionEligibility EvaluatePath(EvidenceRetentionFacts facts)
    {
        if (!facts.TerminalGoal)
        {
            return Eligibility(EvidenceEligibility.Keep, "non-terminal-evidence", facts);
        }

        if (facts.Owner is null && facts.RequireOwner)
        {
            return Eligibility(
                EvidenceEligibility.Keep,
                "artifact-owner-unresolved",
                facts with { OwnershipSource = EvidenceOwnershipSource.Unresolved });
        }

        if (facts.Owner is not null && facts.ProtectedAttempt)
        {
            return Eligibility(
                EvidenceEligibility.Keep,
                facts.Owner.Failed ? "last-failing-attempt" : "final-attempt",
                facts);
        }

        if (facts.Owner is not null && facts.ReferencedArtifact)
        {
            return Eligibility(EvidenceEligibility.Keep, "retained-test-artifact-owner-metadata", facts);
        }

        if (facts.CountBound || facts.Aged || facts.ByteBoundEligible)
        {
            return Eligibility(
                EvidenceEligibility.DeleteWhenSafe,
                facts.CountBound ? "past-count-bound" : facts.Aged ? "past-age-bound" : "past-byte-bound",
                facts);
        }

        return Eligibility(EvidenceEligibility.Archive, "within-retention-bounds", facts);
    }

    internal static string ComputeFactRevision(
        IReadOnlyCollection<RetentionAttemptIdentity> attempts,
        IEnumerable<string> candidatePaths,
        string? lifecycleRevision = null)
    {
        var facts = attempts
            .OrderBy(attempt => attempt.AttemptId, StringComparer.OrdinalIgnoreCase)
            .Select(attempt => string.Join(
                "|",
                attempt.AttemptId,
                attempt.Ordinal,
                attempt.StartedAt.UtcTicks,
                attempt.Failed,
                attempt.Reconciled,
                attempt.LifecycleRevision ?? string.Empty,
                string.Join(",", (attempt.DeclaredPaths ?? []).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))))
            .Concat(candidatePaths.Select(Path.GetFullPath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            .Concat(["lifecycle:" + (lifecycleRevision ?? string.Empty)]);
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", facts));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static EvidenceRetentionEligibility Eligibility(
        EvidenceEligibility disposition,
        string reason,
        EvidenceRetentionFacts facts) =>
        new(
            disposition,
            reason,
            facts.Owner?.AttemptId,
            facts.Owner?.Ordinal,
            facts.OwnershipSource,
            Version,
            facts.FactRevision);
}

internal sealed record RetentionPathOwner(
    RetentionAttemptIdentity? Attempt,
    EvidenceOwnershipSource Source,
    bool Ambiguous);

internal sealed record RetentionAttemptIdentity(
    string AttemptId,
    int Ordinal,
    DateTimeOffset StartedAt,
    bool Failed,
    bool Reconciled,
    IReadOnlyList<string>? DeclaredPaths = null,
    string? LifecycleRevision = null);
