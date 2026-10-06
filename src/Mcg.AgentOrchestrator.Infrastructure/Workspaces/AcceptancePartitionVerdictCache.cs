using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record PartitionVerdictJournalSnapshot(
    IReadOnlyList<PartitionVerdictRecord> Records,
    IReadOnlyList<PartitionVerdictJournalSummary> Summaries);

internal sealed record PartitionVerdictJournalSummary(
    string PairKey,
    int ReuseAttemptCount,
    bool ForcedFullRerun,
    DateTimeOffset RecordedAt);

internal sealed record PartitionVerdictJournalEntry(
    string IdempotencyKey,
    GoalId GoalId,
    string Operation,
    string Status,
    DateTimeOffset At,
    string? Detail = null,
    string? BranchHeadSha = null,
    string? MainHeadSha = null,
    string? AcceptanceOutcome = null,
    string? PartitionVerdictCacheKey = null,
    string? PartitionPairKey = null,
    string? PartitionId = null,
    string? PartitionFilterHash = null,
    string? PartitionVerdict = null,
    string? PartitionAttemptId = null,
    IReadOnlyList<string>? PartitionTestResultPaths = null,
    int? PartitionReuseAttemptCount = null,
    bool? PartitionForcedFullRerun = null,
    PartitionWithinAttemptRetryReceipt? PartitionRetryReceipt = null,
    AcceptanceSharedApparatusInvalidation? SharedApparatusInvalidation = null);

internal sealed record PartitionWithinAttemptRetryReceipt(
    string PartitionId,
    string FailedPredicate,
    string OriginalInvocationId,
    string RetryInvocationId,
    int? ExitCode,
    int? DiscoveredTestCount,
    int? ExecutedTestCount,
    string TrxOutcome,
    string? PolicySignal,
    string DiagnosticPath,
    string DiagnosticSha256,
    int? NotExecutedTestCount = null,
    bool? FlakeConfirmed = null);

internal sealed record PartitionVerdictRecord(
    string GoalId,
    string AttemptId,
    string CandidateTreeSha,
    string MainSha,
    string PartitionFilterHash,
    string PartitionId,
    string CacheKey,
    bool Passed,
    string Verdict,
    IReadOnlyList<string> TestResultPaths,
    DateTimeOffset RecordedAt,
    string? ClosureHash = null,
    string? VerdictSource = null,
    bool ProbeRan = false,
    bool? ProbeConfirmedFlake = null);

internal sealed record PartitionVerdictReuseReceipt(
    string PartitionId,
    string SourceAttemptId,
    string CacheKey,
    string ReuseRule = "pair",
    string? ClosureHash = null);

internal sealed record PartitionVerdictExecutionReceipt(
    string PartitionId,
    string Verdict);

internal sealed record AcceptancePartitionVerdictCacheOptions(
    GoalId? GoalId,
    string WorktreePath,
    IReadOnlyList<AcceptanceManifestCheck> EffectiveChecks,
    int FullRerunEveryN,
    bool WithinAttemptRerunEnabled,
    Func<string, string?> ResolveCandidateTreeSha,
    Func<string, string?> ResolveMainSha,
    Func<string, string?> ResolveVerifyingCommitSha,
    Func<string> ResolveAttemptId,
    Func<string> ResolveManifestIdentity,
    Func<bool> EnforceStructuralCoverage,
    Func<IReadOnlyList<TempRootApparatusLossReceiptV1>>? ResolveApparatusLossReceipts = null,
    Func<AcceptanceManifestCheck, string?>? ResolveClosureHash = null,
    Func<string, string, string, IReadOnlyList<string>>? ResolveChangedFiles = null,
    bool DisableLaneReuseShadow = false);

internal sealed partial class AcceptancePartitionVerdictCache
{
    private const string PartitionVerdictJournalOperation = "acceptance:partition-verdict";
    private const string PartitionVerdictCacheJournalOperation = "acceptance:partition-verdict-cache";
    private const string SharedApparatusInvalidatedJournalOperation = "acceptance:shared-apparatus-invalidated";
    private static readonly JsonSerializerOptions PartitionVerdictJournalJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private static readonly object PartitionVerdictJournalGate = new();

    private readonly object _gate = new();
    private readonly PartitionVerdictJournalSnapshot _journal;
    private readonly bool _withinAttemptRerunEnabled;
    private readonly Func<bool> _enforceStructuralCoverage;
    private readonly List<PartitionVerdictReuseReceipt> _reused = [];
    private readonly List<PartitionVerdictExecutionReceipt> _executed = [];
    private readonly List<PartitionWithinAttemptRetryReceipt> _retries = [];
    private readonly List<PartitionVerdictRecord> _freshRecords = [];
    private readonly List<(string PartitionId, AcceptanceCheckResult Result)> _failedPartitions = [];
    private readonly Func<IReadOnlyList<TempRootApparatusLossReceiptV1>> _resolveApparatusLossReceipts;
    private readonly Func<AcceptanceManifestCheck, string?> _resolveClosureHash;
    private readonly AcceptanceClosureVerdictIndex _closureIndex;
    private readonly ConcurrentDictionary<string, Lazy<string?>> _closureHashes = new(StringComparer.OrdinalIgnoreCase);
    private AcceptanceSharedApparatusInvalidation? _sharedApparatusInvalidation;
    private AcceptanceTestReuseShadow? TestReuseShadow { get; init; }
    private AcceptanceLaneReuseShadow? LaneReuseShadow { get; init; }

    private AcceptancePartitionVerdictCache(
        string goalId,
        string candidateTreeSha,
        string mainSha,
        string manifestIdentity,
        string verifyingCommitSha,
        string attemptId,
        string pairKey,
        string journalPath,
        PartitionVerdictJournalSnapshot journal,
        int partitionCount,
        int priorReuseAttemptCount,
        bool forceFullRerun,
        bool withinAttemptRerunEnabled,
        Func<bool> enforceStructuralCoverage,
        Func<IReadOnlyList<TempRootApparatusLossReceiptV1>> resolveApparatusLossReceipts,
        Func<AcceptanceManifestCheck, string?> resolveClosureHash,
        AcceptanceClosureVerdictIndex closureIndex)
    {
        GoalId = goalId;
        CandidateTreeSha = candidateTreeSha;
        MainSha = mainSha;
        ManifestIdentity = manifestIdentity;
        VerifyingCommitSha = verifyingCommitSha;
        AttemptId = attemptId;
        PairKey = pairKey;
        JournalPath = journalPath;
        _journal = journal;
        PartitionCount = partitionCount;
        PriorReuseAttemptCount = priorReuseAttemptCount;
        ForceFullRerun = forceFullRerun;
        _withinAttemptRerunEnabled = withinAttemptRerunEnabled;
        _enforceStructuralCoverage = enforceStructuralCoverage;
        _resolveApparatusLossReceipts = resolveApparatusLossReceipts;
        _resolveClosureHash = resolveClosureHash;
        _closureIndex = closureIndex;
    }

    internal string GoalId { get; }
    internal string CandidateTreeSha { get; }
    internal string MainSha { get; }
    internal string ManifestIdentity { get; }
    internal string VerifyingCommitSha { get; }
    internal string AttemptId { get; }
    internal string PairKey { get; }
    internal string JournalPath { get; }
    internal int PartitionCount { get; }
    internal int PriorReuseAttemptCount { get; }
    internal bool ForceFullRerun { get; }
    internal AcceptanceSharedApparatusInvalidation? SharedApparatusInvalidation
    {
        get { lock (_gate) return _sharedApparatusInvalidation; }
    }

    internal static AcceptancePartitionVerdictCache? Create(AcceptancePartitionVerdictCacheOptions options)
    {
        if (options.GoalId is null)
            return null;

        var partitionCount = options.EffectiveChecks.Count(check =>
            GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out _, out _));
        if (partitionCount == 0)
            return null;

        var candidateTreeSha = options.ResolveCandidateTreeSha(options.WorktreePath);
        var mainSha = options.ResolveMainSha(options.WorktreePath);
        var verifyingCommitSha = options.ResolveVerifyingCommitSha(options.WorktreePath);
        if (string.IsNullOrWhiteSpace(candidateTreeSha) ||
            string.IsNullOrWhiteSpace(mainSha) ||
            string.IsNullOrWhiteSpace(verifyingCommitSha))
        {
            return null;
        }

        var manifestIdentity = options.ResolveManifestIdentity();
        var journalPath = PartitionVerdictJournalPath(options.WorktreePath, options.GoalId.Value);
        var journal = ReadPartitionVerdictJournal(journalPath);
        var pairKey = BuildPairKey(options.GoalId.Value, candidateTreeSha, mainSha, manifestIdentity);
        var priorReuseAttemptCount = LatestPartitionReuseAttemptCount(journal, pairKey);
        var reusableGreenExists = options.EffectiveChecks.Any(check =>
            TryBuildCacheKey(
                options.GoalId.Value,
                candidateTreeSha,
                mainSha,
                manifestIdentity,
                check,
                out _,
                out _,
                out var cacheKey) &&
            LatestGreenVerdict(journal, options.GoalId.Value, cacheKey) is not null);
        var forceFullRerun = reusableGreenExists &&
            priorReuseAttemptCount + 1 >= options.FullRerunEveryN;
        var attemptId = options.ResolveAttemptId();

        return new AcceptancePartitionVerdictCache(
            options.GoalId.Value,
            GoalAcceptanceVerifier.NormalizeShaToken(candidateTreeSha),
            GoalAcceptanceVerifier.NormalizeShaToken(mainSha),
            manifestIdentity,
            GoalAcceptanceVerifier.NormalizeShaToken(verifyingCommitSha),
            attemptId,
            pairKey,
            journalPath,
            journal,
            partitionCount,
            priorReuseAttemptCount,
            forceFullRerun,
            options.WithinAttemptRerunEnabled,
            options.EnforceStructuralCoverage,
            options.ResolveApparatusLossReceipts ?? (() => []),
            options.ResolveClosureHash ?? (check => AcceptanceLaneClosureHasher.TryCompute(options.WorktreePath, check)),
            new AcceptanceClosureVerdictIndex(options.WorktreePath))
        {
            TestReuseShadow = new AcceptanceTestReuseShadow(
                options.WorktreePath, options.GoalId.Value, attemptId,
                GoalAcceptanceVerifier.NormalizeShaToken(mainSha),
                GoalAcceptanceVerifier.NormalizeShaToken(candidateTreeSha),
                GoalAcceptanceVerifier.NormalizeShaToken(verifyingCommitSha),
                options.ResolveChangedFiles ?? AcceptanceTestReuseShadow.ResolveChangedFilesFromGit),
            LaneReuseShadow = options.DisableLaneReuseShadow ? null : new AcceptanceLaneReuseShadow(
                options.WorktreePath, options.GoalId.Value, attemptId,
                GoalAcceptanceVerifier.NormalizeShaToken(mainSha),
                GoalAcceptanceVerifier.NormalizeShaToken(candidateTreeSha),
                GoalAcceptanceVerifier.NormalizeShaToken(verifyingCommitSha), options.EffectiveChecks)
        };
    }

    internal AcceptanceCheckResult? TryReuse(AcceptanceManifestCheck check)
    {
        if (!TryBuildCacheKey(check, out var partitionId, out var filterHash, out var cacheKey))
        {
            RecordMiss(check.Name, PartitionVerdictMissReasons.CacheKeyUnavailable);
            return null;
        }
        if (ForceFullRerun)
        {
            RecordMiss(partitionId, PartitionVerdictMissReasons.ForcedFullRerun);
            return null;
        }

        var cached = LatestGreenVerdict(_journal, GoalId, cacheKey);
        var reuseRule = "pair";
        string? closureHash = null;
        if (cached is null)
        {
            closureHash = ResolveClosureHash(check);
            if (string.IsNullOrWhiteSpace(closureHash))
            {
                RecordMiss(partitionId, PartitionVerdictMissReasons.ClosureHashUnavailable);
                return null;
            }
            if (_closureIndex.FindLatest(ManifestIdentity, filterHash, closureHash) is not { } contentVerdict)
            {
                RecordMiss(partitionId, PartitionVerdictMissReasons.NoGreenVerdictForClosure, closureHash);
                return null;
            }
            cached = new PartitionVerdictRecord(
                GoalId, contentVerdict.SourceAttemptId, CandidateTreeSha, MainSha, filterHash,
                partitionId, cacheKey, true, "GREEN", contentVerdict.TestResultPaths,
                contentVerdict.RecordedAt, closureHash);
            reuseRule = "closure";
        }
        if (!HasReusableStructuralCoverageEvidence(cached))
        {
            RecordMiss(partitionId, PartitionVerdictMissReasons.MissingStructuralCoverageEvidence, closureHash);
            return null;
        }

        RecordReuse(new PartitionVerdictReuseReceipt(partitionId, cached.AttemptId, cacheKey, reuseRule, closureHash));
        return new AcceptanceCheckResult(
            check.Name,
            true,
            0,
            null,
            ResultSummary:
                $"partition-verdict-cache reused source_attempt_id={cached.AttemptId} cache_key={cacheKey} " +
                $"reuse_rule={reuseRule} closure_hash={closureHash ?? "not-applicable"}",
            TestResultPaths: cached.TestResultPaths,
            TestResultAttemptId: cached.AttemptId,
            TestResultIsExplicitCrossAttemptReuse: true);
    }

    internal bool ShouldRerunWithinAttempt(
        AcceptanceManifestCheck check,
        AcceptanceShardCompletionDecision? decision) =>
        _withinAttemptRerunEnabled &&
        SharedApparatusInvalidation is null &&
        decision is { Passed: false, FailedPredicate: { Length: > 0 } } &&
        IsRetryableWithinAttempt(decision.FailedPredicate) &&
        GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out _, out _);

    internal AcceptanceCheckResult ObserveSharedApparatusEvidence(
        AcceptanceManifestCheck check,
        AcceptanceCheckResult result)
    {
        if (result.Passed ||
            !TryBuildCacheKey(check, out var partitionId, out _, out _) ||
            result.ChildProcessId is not > 0 ||
            result.ChildProcessStartedAt is null)
        {
            return result;
        }

        lock (_gate)
        {
            _failedPartitions.RemoveAll(failure =>
                failure.PartitionId.Equals(partitionId, StringComparison.OrdinalIgnoreCase) &&
                failure.Result.ChildProcessId == result.ChildProcessId &&
                failure.Result.ChildProcessStartedAt == result.ChildProcessStartedAt);
            _failedPartitions.Add((partitionId, result));
            var observed = AcceptanceSharedApparatusInvalidationClassifier.Classify(
                AttemptId,
                _failedPartitions,
                ReadApparatusLossReceipts());
            if (_sharedApparatusInvalidation is null)
            {
                _sharedApparatusInvalidation = observed;
            }
            else if (observed is not null)
            {
                _sharedApparatusInvalidation = _sharedApparatusInvalidation with
                {
                    AffectedOwners = _sharedApparatusInvalidation.AffectedOwners
                        .Concat(observed.AffectedOwners)
                        .DistinctBy(owner => owner.PartitionId, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                };
            }

            return DecorateSharedApparatusFailure(result, _sharedApparatusInvalidation, partitionId);
        }
    }

    internal IReadOnlyList<AcceptanceCheckResult> ApplySharedApparatusInvalidation(
        IReadOnlyList<AcceptanceCheckResult> results)
    {
        lock (_gate)
        {
            if (_sharedApparatusInvalidation is null)
            {
                return results;
            }

            var collapsed = new List<AcceptanceCheckResult>(results.Count);
            var incidentEmitted = false;
            foreach (var result in results)
            {
                if (result.Passed)
                {
                    collapsed.Add(result);
                    continue;
                }

                var partitionId = _failedPartitions
                    .FirstOrDefault(failure => failure.Result.Name.Equals(result.Name, StringComparison.Ordinal))
                    .PartitionId;
                if (!IsAffectedBySharedApparatusLoss(result, _sharedApparatusInvalidation, partitionId))
                {
                    collapsed.Add(result);
                    continue;
                }

                if (!incidentEmitted)
                {
                    collapsed.Add(DecorateSharedApparatusFailure(result, _sharedApparatusInvalidation, partitionId));
                    incidentEmitted = true;
                }
            }

            return collapsed;
        }
    }

    private IReadOnlyList<TempRootApparatusLossReceiptV1> ReadApparatusLossReceipts()
    {
        try { return _resolveApparatusLossReceipts() ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static AcceptanceCheckResult DecorateSharedApparatusFailure(
        AcceptanceCheckResult result,
        AcceptanceSharedApparatusInvalidation? invalidation,
        string? partitionId = null)
    {
        if (!IsAffectedBySharedApparatusLoss(result, invalidation, partitionId))
        {
            return result;
        }

        var correlatedInvalidation = invalidation!;
        var evidence =
            $"receipt_id={correlatedInvalidation.FirstReceipt.ReceiptId} gate_invocation_id={correlatedInvalidation.FirstReceipt.GateInvocationId} " +
            $"affected_owners={JsonSerializer.Serialize(correlatedInvalidation.AffectedOwners, PartitionVerdictJournalJsonOptions)}";
        return result with
        {
            FailureClassification = AcceptanceFailureClassifications.SharedGateApparatusInvalidated,
            FailureCauseEvidence = new AcceptanceFailureCauseEvidence(
                AcceptanceFailureCause.EnvironmentalApparatus,
                evidence,
                result.Name,
                AcceptanceFailureClassifications.SharedGateApparatusInvalidated)
        };
    }

    private static bool IsAffectedBySharedApparatusLoss(
        AcceptanceCheckResult result,
        AcceptanceSharedApparatusInvalidation? invalidation,
        string? partitionId = null) =>
        invalidation is not null &&
        (result.FailureClassification == AcceptanceFailureClassifications.SharedGateApparatusInvalidated ||
         !string.IsNullOrWhiteSpace(partitionId) && invalidation.AffectedOwners.Any(owner =>
             owner.PartitionId.Equals(partitionId, StringComparison.OrdinalIgnoreCase)) ||
         result.ChildProcessId is { } processId &&
         result.ChildProcessStartedAt is { } processStartedAt &&
         invalidation.AffectedOwners.Any(owner =>
             owner.OwnerProcessId == processId &&
             owner.OwnerStartedAt == processStartedAt));
    private static bool IsRetryableWithinAttempt(string failedPredicate) =>
        failedPredicate is AcceptanceShardCompletionPredicates.TimedOut or
            AcceptanceShardCompletionPredicates.NonzeroExit or
            AcceptanceShardCompletionPredicates.MissingTrx or
            AcceptanceShardCompletionPredicates.MalformedTrx or
            AcceptanceShardCompletionPredicates.ZeroTests or
            AcceptanceShardCompletionPredicates.IncompleteExecution ||
        AcceptanceFailureClassifications.IsEnvironmentalApparatus(failedPredicate);

    internal bool ShouldRerunWithinAttempt(AcceptanceManifestCheck check, bool passed) =>
        ShouldRerunWithinAttempt(
            check,
            new AcceptanceShardCompletionDecision(
                passed,
                passed ? null : AcceptanceShardCompletionPredicates.NonzeroExit,
                false,
                passed ? 0 : 1,
                null,
                null,
                "test-compatibility"));

    internal void RecordWithinAttemptRetry(
        AcceptanceManifestCheck check,
        AcceptanceCheckResult original,
        string originalInvocationId,
        string retryInvocationId,
        AcceptanceRetainedDiagnostic diagnostic)
    {
        if (!TryBuildCacheKey(check, out var partitionId, out var filterHash, out var cacheKey) ||
            original.CompletionDecision is not { Passed: false, FailedPredicate: { Length: > 0 } } decision)
        {
            throw new InvalidOperationException(
                $"Within-attempt retry for '{check.Name}' lacks a typed failed shard-completion decision.");
        }

        var retryReceipt = new PartitionWithinAttemptRetryReceipt(
            partitionId,
            decision.FailedPredicate,
            originalInvocationId,
            retryInvocationId,
            original.ExitCode,
            decision.DiscoveredTestCount,
            decision.ExecutedTestCount,
            decision.TrxOutcome,
            decision.PolicySignal,
            diagnostic.Path,
            diagnostic.Sha256,
            decision.NotExecutedTestCount);
        lock (_gate)
        {
            _retries.Add(retryReceipt);
        }
        var detail =
            $"partition_id={partitionId} predicate={decision.FailedPredicate} " +
            $"original_invocation_id={originalInvocationId} retry_invocation_id={retryInvocationId} " +
            $"exit_code={original.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} " +
            $"discovered={decision.DiscoveredTestCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
            $"executed={decision.ExecutedTestCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
            $"not_executed={decision.NotExecutedTestCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
            $"trx_outcome={decision.TrxOutcome} diagnostic_path={diagnostic.Path} diagnostic_sha256={diagnostic.Sha256}";
        AppendPartitionVerdictJournalEntries(
            JournalPath,
            [new PartitionVerdictJournalEntry(
                $"{AttemptId}:retry:{partitionId}:{retryInvocationId}",
                new GoalId(GoalId),
                "acceptance:partition-within-attempt-retry",
                "recorded",
                DateTimeOffset.UtcNow,
                Detail: detail,
                BranchHeadSha: CandidateTreeSha,
                MainHeadSha: MainSha,
                PartitionVerdictCacheKey: cacheKey,
                PartitionPairKey: PairKey,
                PartitionId: partitionId,
                PartitionFilterHash: filterHash,
                PartitionAttemptId: AttemptId,
                PartitionRetryReceipt: retryReceipt)]);
        Console.WriteLine($"PARTITION_VERDICT_RETRY {detail}");
        Console.Out.Flush();
    }

    internal AcceptanceCheckResult SelectPartitionVerdict(
        AcceptanceManifestCheck check,
        AcceptanceCheckResult firstRun,
        AcceptanceCheckResult probe)
    {
        if (!TryBuildCacheKey(check, out var partitionId, out var filterHash, out var cacheKey) ||
            firstRun.CompletionDecision is not { Passed: false, FailedPredicate: { Length: > 0 } } decision)
        {
            throw new InvalidOperationException(
                $"Within-attempt probe for '{check.Name}' lacks a typed failed first-run decision.");
        }

        PartitionWithinAttemptRetryReceipt completedReceipt;
        AcceptanceCheckResult verdict;
        lock (_gate)
        {
            var retryIndex = _retries.FindLastIndex(receipt =>
                receipt.PartitionId.Equals(partitionId, StringComparison.OrdinalIgnoreCase));
            if (retryIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Within-attempt probe for '{check.Name}' has no recorded retry receipt.");
            }

            completedReceipt = _retries[retryIndex] with { FlakeConfirmed = probe.Passed };
            _retries[retryIndex] = completedReceipt;
            verdict = firstRun;
            if (IsApparatusInvalidatingPredicate(decision.FailedPredicate))
            {
                _sharedApparatusInvalidation ??= new AcceptanceSharedApparatusInvalidation(
                    new TempRootApparatusLossReceiptV1(
                        1,
                        AttemptId,
                        $"apparatus-predicate:{decision.FailedPredicate}",
                        string.Empty,
                        [],
                        DateTimeOffset.UtcNow),
                    []);
                verdict = DecorateSharedApparatusFailure(
                    firstRun with
                    {
                        FailureClassification = AcceptanceFailureClassifications.SharedGateApparatusInvalidated
                    },
                    _sharedApparatusInvalidation,
                    partitionId);
            }
            if (IsWithinAttemptRerunPass(firstRun, decision, probe))
            {
                verdict = verdict with
                {
                    WithinAttemptRerun = new AcceptanceWithinAttemptRerunEvidence(
                        partitionId,
                        decision.FailedPredicate,
                        completedReceipt.OriginalInvocationId,
                        completedReceipt.RetryInvocationId,
                        probe.CompletionDecision?.ExecutedTestCount ?? probe.ExecutedTestCount!.Value,
                        probe.TestResultPaths!)
                };
            }
        }

        var detail =
            $"partition_id={partitionId} predicate={decision.FailedPredicate} verdict_source=first_run " +
            $"probe_ran=true flake_confirmed={probe.Passed.ToString().ToLowerInvariant()}" +
            (verdict.WithinAttemptRerun is null ? string.Empty :
                $" apparatus_reason={AcceptanceWithinAttemptRerunEvidence.Reason}");
        AppendPartitionVerdictJournalEntries(
            JournalPath,
            [new PartitionVerdictJournalEntry(
                $"{AttemptId}:probe:{partitionId}:{completedReceipt.RetryInvocationId}",
                new GoalId(GoalId),
                "acceptance:partition-within-attempt-probe",
                "recorded",
                DateTimeOffset.UtcNow,
                Detail: detail,
                BranchHeadSha: CandidateTreeSha,
                MainHeadSha: MainSha,
                PartitionVerdictCacheKey: cacheKey,
                PartitionPairKey: PairKey,
                PartitionId: partitionId,
                PartitionFilterHash: filterHash,
                PartitionVerdict: firstRun.Passed ? "GREEN" : "RED",
                PartitionAttemptId: AttemptId,
                PartitionRetryReceipt: completedReceipt)]);
        return verdict;
    }

    private static bool IsWithinAttemptRerunPass(
        AcceptanceCheckResult firstRun,
        AcceptanceShardCompletionDecision decision,
        AcceptanceCheckResult probe) =>
        (decision.FailedPredicate is AcceptanceShardCompletionPredicates.MissingTrx or
            AcceptanceShardCompletionPredicates.MalformedTrx) &&
        firstRun.FailingTestIdentities?.Any(identity => !string.IsNullOrWhiteSpace(identity)) != true &&
        probe.Passed &&
        probe.CompletionDecision is { Passed: true } &&
        (probe.CompletionDecision.ExecutedTestCount ?? probe.ExecutedTestCount) > 0 &&
        probe.TestResultPaths is { Count: > 0 } &&
        probe.FailingTestIdentities?.Any(identity => !string.IsNullOrWhiteSpace(identity)) != true;

    private static bool IsApparatusInvalidatingPredicate(string failedPredicate) =>
        failedPredicate is AcceptanceShardCompletionPredicates.MissingTrx or
            AcceptanceShardCompletionPredicates.MalformedTrx ||
        AcceptanceFailureClassifications.IsEnvironmentalApparatus(failedPredicate);

    internal void RecordExecution(AcceptanceManifestCheck check, AcceptanceCheckResult result)
    {
        if (!TryBuildCacheKey(check, out var partitionId, out var filterHash, out var cacheKey))
        {
            return;
        }

        var closureHash = result.Passed ? ResolveClosureHash(check) : null;
        lock (_gate)
        {
            var probeReceipt = _retries.LastOrDefault(receipt =>
                receipt.PartitionId.Equals(partitionId, StringComparison.OrdinalIgnoreCase));
            _executed.Add(new PartitionVerdictExecutionReceipt(
                partitionId,
                result.Passed ? "GREEN" : "RED"));
            RecordExecutedDuration(result.DurationMilliseconds);
            _freshRecords.Add(new PartitionVerdictRecord(
                GoalId,
                AttemptId,
                CandidateTreeSha,
                MainSha,
                filterHash,
                partitionId,
                cacheKey,
                result.Passed,
                result.Passed ? "GREEN" : "RED",
                result.TestResultPaths ?? [],
                DateTimeOffset.UtcNow,
                closureHash,
                "first_run",
                probeReceipt is not null,
                probeReceipt?.FlakeConfirmed));
        }
        TestReuseShadow?.Observe(check, result);
        LaneReuseShadow?.Observe(check, result);
    }

    internal void RecordSemanticDeduplications(
        IReadOnlyList<GoalAcceptanceVerifier.SemanticExecutionDeduplicationReceipt> receipts)
    {
        if (receipts.Count == 0)
        {
            return;
        }

        AppendPartitionVerdictJournalEntries(
            JournalPath,
            receipts.Select(receipt =>
                new PartitionVerdictJournalEntry(
                    $"{AttemptId}:semantic-dedup:{receipt.PartitionId}:{receipt.DroppedPlanIndex}",
                    new GoalId(GoalId),
                    "acceptance:semantic-check-deduplication",
                    "Completed",
                    DateTimeOffset.UtcNow,
                    Detail:
                        $"partition_id={receipt.PartitionId} retained_check={receipt.RetainedCheckName} " +
                        $"dropped_check={receipt.DroppedCheckName} retained_plan_index={receipt.RetainedPlanIndex} " +
                        $"dropped_plan_index={receipt.DroppedPlanIndex} semantic_key_sha256={receipt.SemanticKeySha256}",
                    BranchHeadSha: CandidateTreeSha,
                    MainHeadSha: MainSha,
                    PartitionPairKey: PairKey,
                    PartitionId: receipt.PartitionId,
                    PartitionAttemptId: AttemptId))
                .ToArray());
    }

    internal AcceptanceCheckResult? CompleteAttempt()
    {
        if (_reused.Count == 0 && _executed.Count == 0)
        {
            return null;
        }

        var aggregateVerdict = _executed.Any(executed =>
            executed.Verdict.Equals("RED", StringComparison.OrdinalIgnoreCase))
                ? "RED"
                : "GREEN";
        var attemptCount = 0;
        if (ForceFullRerun ||
            (_reused.Count == 0 && _executed.Count >= PartitionCount))
        {
            attemptCount = 0;
        }
        else if (_reused.Count > 0)
        {
            attemptCount = PriorReuseAttemptCount + 1;
        }
        var summaryRecordedAt = DateTimeOffset.UtcNow;
        var receipt =
            $"partition-verdict-cache reused_partitions={FormatPartitionReuseReceipt(_reused)} " +
            $"executed_partitions={FormatPartitionExecutionReceipt(_executed)} " +
            $"within_attempt_retries={FormatPartitionRetryReceipt(_retries)} " +
            $"aggregate_verdict={aggregateVerdict} verifying_commit_sha={VerifyingCommitSha} " +
            $"effective_manifest_identity={ManifestIdentity} " +
            $"reroll_attempt_count={attemptCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"forced_full_rerun={ForceFullRerun.ToString().ToLowerInvariant()} " +
            FormatMeasurementTokens();
        AppendPartitionVerdictJournalEntries(
            JournalPath,
            BuildPartitionVerdictJournalEntries(this, receipt, aggregateVerdict, attemptCount, summaryRecordedAt));
        var retriedPartitionIds = _retries
            .Select(receipt => receipt.PartitionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var record in _freshRecords.Where(record =>
                     record.Passed &&
                     !string.IsNullOrWhiteSpace(record.ClosureHash) &&
                     !retriedPartitionIds.Contains(record.PartitionId)))
            _closureIndex.AppendGreen(ManifestIdentity, record.PartitionFilterHash, record.ClosureHash!, record.AttemptId, record.TestResultPaths);
        Console.WriteLine($"PARTITION_VERDICT_CACHE {receipt}");
        Console.Out.Flush();
        TestReuseShadow?.Complete();
        LaneReuseShadow?.Complete();
        return new AcceptanceCheckResult(
            "infrastructure partition verdict cache",
            true,
            0,
            null,
            ResultSummary: receipt,
            Advisory: true);
    }

    private void RecordReuse(PartitionVerdictReuseReceipt receipt)
    {
        lock (_gate)
        {
            _reused.Add(receipt);
        }
    }

    private string? ResolveClosureHash(AcceptanceManifestCheck check) =>
        _closureHashes.GetOrAdd(
            check.Project ?? check.Name,
            _ => new Lazy<string?>(() => _resolveClosureHash(check), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private bool TryBuildCacheKey(
        AcceptanceManifestCheck check,
        out string partitionId,
        out string filterHash,
        out string cacheKey) =>
        TryBuildCacheKey(
            GoalId,
            CandidateTreeSha,
            MainSha,
            ManifestIdentity,
            check,
            out partitionId,
            out filterHash,
            out cacheKey);

    private static bool TryBuildCacheKey(
        string goalId,
        string candidateTreeSha,
        string mainSha,
        string manifestIdentity,
        AcceptanceManifestCheck check,
        out string partitionId,
        out string filterHash,
        out string cacheKey)
    {
        partitionId = string.Empty;
        filterHash = string.Empty;
        cacheKey = string.Empty;
        if (!GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out partitionId, out var filter))
            return false;

        filterHash = GoalAcceptanceVerifier.ShortHash(filter);
        cacheKey = string.Join(
                ':',
                goalId,
                GoalAcceptanceVerifier.NormalizeShaToken(candidateTreeSha),
                GoalAcceptanceVerifier.NormalizeShaToken(mainSha),
                manifestIdentity,
                filterHash)
            .ToLowerInvariant();
        return true;
    }

    private static PartitionVerdictRecord? LatestGreenVerdict(
        PartitionVerdictJournalSnapshot journal,
        string goalId,
        string cacheKey)
    {
        var latest = journal.Records
            .Where(record =>
                record.GoalId.Equals(goalId, StringComparison.OrdinalIgnoreCase) &&
                record.CacheKey.Equals(cacheKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(record => record.RecordedAt)
            .LastOrDefault();
        return latest?.Passed == true ? latest : null;
    }

    private bool HasReusableStructuralCoverageEvidence(PartitionVerdictRecord cached) =>
        !_enforceStructuralCoverage() ||
        cached.TestResultPaths is { Count: > 0 } &&
        cached.TestResultPaths.All(path => GoalAcceptanceVerifier.TryGetFileLength(path) > 0);

    private static string FormatPartitionReuseReceipt(IReadOnlyList<PartitionVerdictReuseReceipt> reused) =>
        reused.Count == 0
            ? "[]"
            : "[" + string.Join("|", reused.Select(receipt =>
                $"{{partition_id={receipt.PartitionId},source_attempt_id={receipt.SourceAttemptId},cache_key={receipt.CacheKey}," +
                $"reuse_rule={receipt.ReuseRule},closure_hash={receipt.ClosureHash ?? "not-applicable"}}}")) + "]";

    private static string FormatPartitionExecutionReceipt(IReadOnlyList<PartitionVerdictExecutionReceipt> executed) =>
        executed.Count == 0
            ? "[]"
            : "[" + string.Join("|", executed.Select(receipt =>
                $"{{partition_id={receipt.PartitionId},verdict={receipt.Verdict}}}")) + "]";

    private static string FormatPartitionRetryReceipt(IReadOnlyList<PartitionWithinAttemptRetryReceipt> retries) =>
        retries.Count == 0
            ? "[]"
             : "[" + string.Join("|", retries.Select(receipt =>
                 $"{{partition_id={receipt.PartitionId},predicate={receipt.FailedPredicate}," +
                 $"original_invocation_id={receipt.OriginalInvocationId},retry_invocation_id={receipt.RetryInvocationId}," +
                 $"diagnostic_path={receipt.DiagnosticPath},diagnostic_sha256={receipt.DiagnosticSha256}," +
                 $"verdict_source=first_run,probe_ran=true," +
                 $"flake_confirmed={receipt.FlakeConfirmed?.ToString().ToLowerInvariant() ?? "unknown"}}}")) + "]";

    private static string PartitionVerdictJournalPath(string worktreePath, string goalId) =>
        Path.Combine(
            ResolveHostStateRoot(worktreePath),
            ".orchestrator",
            "goal-operations",
            $"{goalId}.jsonl");

    internal static string ResolveHostStateRoot(string worktreePath)
    {
        var fullPath = Path.GetFullPath(worktreePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = new DirectoryInfo(fullPath);
        if (directory.Parent?.Name.Equals(GoalWorktrees.DirectoryName, StringComparison.OrdinalIgnoreCase) == true &&
            directory.Parent.Parent is not null)
        {
            return directory.Parent.Parent.FullName;
        }

        return fullPath;
    }

    private static PartitionVerdictJournalSnapshot ReadPartitionVerdictJournal(string path)
    {
        lock (PartitionVerdictJournalGate)
        {
            if (!File.Exists(path))
                return new PartitionVerdictJournalSnapshot([], []);

            var records = new List<PartitionVerdictRecord>();
            var summaries = new List<PartitionVerdictJournalSummary>();
            foreach (var line in SharedJsonlFile.ReadAllLines(path))
            {
                if (TryDeserializePartitionVerdictJournalEntry(line) is not { } entry)
                    continue;

                if (entry.Operation.Equals(PartitionVerdictJournalOperation, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(entry.PartitionVerdictCacheKey) &&
                    !string.IsNullOrWhiteSpace(entry.PartitionId) &&
                    !string.IsNullOrWhiteSpace(entry.PartitionFilterHash) &&
                    !string.IsNullOrWhiteSpace(entry.PartitionVerdict) &&
                    !string.IsNullOrWhiteSpace(entry.PartitionAttemptId) &&
                    !string.IsNullOrWhiteSpace(entry.BranchHeadSha) &&
                    !string.IsNullOrWhiteSpace(entry.MainHeadSha))
                {
                    records.Add(new PartitionVerdictRecord(
                        entry.GoalId.Value,
                        entry.PartitionAttemptId,
                        GoalAcceptanceVerifier.NormalizeShaToken(entry.BranchHeadSha),
                        GoalAcceptanceVerifier.NormalizeShaToken(entry.MainHeadSha),
                        entry.PartitionFilterHash,
                        entry.PartitionId,
                        entry.PartitionVerdictCacheKey,
                        entry.PartitionVerdict.Equals("GREEN", StringComparison.OrdinalIgnoreCase),
                        entry.PartitionVerdict,
                        entry.PartitionTestResultPaths ?? [],
                        entry.At));
                }
                else if (entry.Operation.Equals(PartitionVerdictCacheJournalOperation, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(entry.PartitionPairKey) &&
                    entry.PartitionReuseAttemptCount is { } reuseAttemptCount)
                {
                    summaries.Add(new PartitionVerdictJournalSummary(
                        entry.PartitionPairKey,
                        reuseAttemptCount,
                        entry.PartitionForcedFullRerun ?? false,
                        entry.At));
                }
            }

            return new PartitionVerdictJournalSnapshot(records, summaries);
        }
    }

    private static PartitionVerdictJournalEntry? TryDeserializePartitionVerdictJournalEntry(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<PartitionVerdictJournalEntry>(line, PartitionVerdictJournalJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AppendPartitionVerdictJournalEntries(
        string path,
        IReadOnlyList<PartitionVerdictJournalEntry> entries)
    {
        if (entries.Count == 0)
            return;

        lock (PartitionVerdictJournalGate)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            SharedJsonlFile.AppendLines(
                path,
                entries.Select(entry => JsonSerializer.Serialize(entry, PartitionVerdictJournalJsonOptions)));
        }
    }

    private static IReadOnlyList<PartitionVerdictJournalEntry> BuildPartitionVerdictJournalEntries(
        AcceptancePartitionVerdictCache cache,
        string receipt,
        string aggregateVerdict,
        int attemptCount,
        DateTimeOffset summaryRecordedAt)
    {
        var entries = cache._freshRecords
            .Select(record => new PartitionVerdictJournalEntry(
                $"{record.CacheKey}:partition-verdict",
                new GoalId(record.GoalId),
                PartitionVerdictJournalOperation,
                record.Passed ? "Completed" : "Failed",
                record.RecordedAt,
                $"partition {record.PartitionId} verdict {record.Verdict} " +
                $"verdict_source={record.VerdictSource ?? "unknown-legacy"} " +
                $"probe_ran={record.ProbeRan.ToString().ToLowerInvariant()} " +
                $"flake_confirmed={record.ProbeConfirmedFlake?.ToString().ToLowerInvariant() ?? "not-applicable"}",
                record.CandidateTreeSha,
                record.MainSha,
                PartitionVerdictCacheKey: record.CacheKey,
                PartitionPairKey: cache.PairKey,
                PartitionId: record.PartitionId,
                PartitionFilterHash: record.PartitionFilterHash,
                PartitionVerdict: record.Verdict,
                PartitionAttemptId: record.AttemptId,
                PartitionTestResultPaths: record.TestResultPaths))
            .ToList();
        if (cache._sharedApparatusInvalidation is { } invalidation)
        {
            entries.Add(new PartitionVerdictJournalEntry(
                $"{cache.PairKey}:shared-apparatus:{cache.AttemptId}",
                new GoalId(cache.GoalId),
                SharedApparatusInvalidatedJournalOperation,
                "Completed",
                summaryRecordedAt,
                Detail:
                    $"receipt_id={invalidation.FirstReceipt.ReceiptId} " +
                    $"gate_invocation_id={invalidation.FirstReceipt.GateInvocationId} " +
                    $"affected_owner_count={invalidation.AffectedOwners.Count}",
                BranchHeadSha: cache.CandidateTreeSha,
                MainHeadSha: cache.MainSha,
                AcceptanceOutcome: AcceptanceFailureClassifications.SharedGateApparatusInvalidated,
                PartitionPairKey: cache.PairKey,
                PartitionAttemptId: cache.AttemptId,
                SharedApparatusInvalidation: invalidation));
        }
        entries.Add(new PartitionVerdictJournalEntry(
            $"{cache.PairKey}:partition-cache:{cache.AttemptId}",
            new GoalId(cache.GoalId),
            PartitionVerdictCacheJournalOperation,
            aggregateVerdict.Equals("GREEN", StringComparison.OrdinalIgnoreCase) ? "Completed" : "Failed",
            summaryRecordedAt,
            receipt,
            cache.CandidateTreeSha,
            cache.MainSha,
            PartitionPairKey: cache.PairKey,
            PartitionVerdict: aggregateVerdict,
            PartitionAttemptId: cache.AttemptId,
            PartitionReuseAttemptCount: attemptCount,
            PartitionForcedFullRerun: cache.ForceFullRerun));
        return entries;
    }

    private static int LatestPartitionReuseAttemptCount(
        PartitionVerdictJournalSnapshot journal,
        string pairKey) =>
        journal.Summaries
            .Where(summary => summary.PairKey.Equals(pairKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(summary => summary.RecordedAt)
            .LastOrDefault()
            ?.ReuseAttemptCount ?? 0;

    private static string BuildPairKey(
        string goalId,
        string candidateTreeSha,
        string mainSha,
        string manifestIdentity) =>
        string.Join(
                ':',
                goalId,
                "acceptance",
                GoalAcceptanceVerifier.NormalizeShaToken(candidateTreeSha),
                GoalAcceptanceVerifier.NormalizeShaToken(mainSha),
                manifestIdentity)
            .ToLowerInvariant();
}
