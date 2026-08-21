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
    bool? PartitionForcedFullRerun = null);

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
    DateTimeOffset RecordedAt);

internal sealed record PartitionVerdictReuseReceipt(
    string PartitionId,
    string SourceAttemptId,
    string CacheKey);

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
    Func<bool> EnforceStructuralCoverage);

internal sealed class AcceptancePartitionVerdictCache
{
    private const string PartitionVerdictJournalOperation = "acceptance:partition-verdict";
    private const string PartitionVerdictCacheJournalOperation = "acceptance:partition-verdict-cache";
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
    private readonly List<PartitionVerdictRecord> _freshRecords = [];

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
        Func<bool> enforceStructuralCoverage)
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

        return new AcceptancePartitionVerdictCache(
            options.GoalId.Value,
            GoalAcceptanceVerifier.NormalizeShaToken(candidateTreeSha),
            GoalAcceptanceVerifier.NormalizeShaToken(mainSha),
            manifestIdentity,
            GoalAcceptanceVerifier.NormalizeShaToken(verifyingCommitSha),
            options.ResolveAttemptId(),
            pairKey,
            journalPath,
            journal,
            partitionCount,
            priorReuseAttemptCount,
            forceFullRerun,
            options.WithinAttemptRerunEnabled,
            options.EnforceStructuralCoverage);
    }

    internal AcceptanceCheckResult? TryReuse(AcceptanceManifestCheck check)
    {
        if (!TryBuildCacheKey(check, out var partitionId, out _, out var cacheKey) ||
            ForceFullRerun ||
            LatestGreenVerdict(_journal, GoalId, cacheKey) is not { } cached ||
            !HasReusableStructuralCoverageEvidence(cached))
        {
            return null;
        }

        RecordReuse(new PartitionVerdictReuseReceipt(partitionId, cached.AttemptId, cacheKey));
        return new AcceptanceCheckResult(
            check.Name,
            true,
            0,
            null,
            ResultSummary:
                $"partition-verdict-cache reused source_attempt_id={cached.AttemptId} cache_key={cacheKey}",
            TestResultPaths: cached.TestResultPaths,
            TestResultAttemptId: cached.AttemptId,
            TestResultIsExplicitCrossAttemptReuse: true);
    }

    internal bool ShouldRerunWithinAttempt(AcceptanceManifestCheck check, bool passed) =>
        _withinAttemptRerunEnabled &&
        !passed &&
        GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out _, out _);

    internal void RecordExecution(AcceptanceManifestCheck check, AcceptanceCheckResult result)
    {
        if (!TryBuildCacheKey(check, out var partitionId, out var filterHash, out var cacheKey))
        {
            return;
        }

        lock (_gate)
        {
            _executed.Add(new PartitionVerdictExecutionReceipt(
                partitionId,
                result.Passed ? "GREEN" : "RED"));
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
                DateTimeOffset.UtcNow));
        }
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
            $"aggregate_verdict={aggregateVerdict} verifying_commit_sha={VerifyingCommitSha} " +
            $"effective_manifest_identity={ManifestIdentity} " +
            $"reroll_attempt_count={attemptCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"forced_full_rerun={ForceFullRerun.ToString().ToLowerInvariant()} " +
            "before_reroll_wall_time=20-25m after_reroll_wall_time=2-7m";
        AppendPartitionVerdictJournalEntries(
            JournalPath,
            BuildPartitionVerdictJournalEntries(this, receipt, aggregateVerdict, attemptCount, summaryRecordedAt));
        Console.WriteLine($"PARTITION_VERDICT_CACHE {receipt}");
        Console.Out.Flush();
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
                $"{{partition_id={receipt.PartitionId},source_attempt_id={receipt.SourceAttemptId},cache_key={receipt.CacheKey}}}")) + "]";

    private static string FormatPartitionExecutionReceipt(IReadOnlyList<PartitionVerdictExecutionReceipt> executed) =>
        executed.Count == 0
            ? "[]"
            : "[" + string.Join("|", executed.Select(receipt =>
                $"{{partition_id={receipt.PartitionId},verdict={receipt.Verdict}}}")) + "]";

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
                $"partition {record.PartitionId} verdict {record.Verdict}",
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
