using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum GoalOperationStatus
{
    Begin,
    Completed,
    Failed,
    Skipped
}

internal sealed record GoalOperationJournalEntry(
    string IdempotencyKey,
    GoalId GoalId,
    string Operation,
    GoalOperationStatus Status,
    DateTimeOffset At,
    string? Detail,
    string? BranchHeadSha = null,
    string? MainHeadSha = null,
    string? AcceptanceOutcome = null,
    string? BaseBuildCacheMainSha = null,
    long? BuildPhaseMilliseconds = null,
    string? BaseBuildCacheProjects = null,
    string? BaseBuildCacheBuiltProjects = null,
    string? BaseBuildCacheEvictions = null,
    string? BaseBuildCacheReceipt = null,
    string? PartitionVerdictCacheKey = null,
    string? PartitionPairKey = null,
    string? PartitionId = null,
    string? PartitionFilterHash = null,
    string? PartitionVerdict = null,
    string? PartitionAttemptId = null,
    IReadOnlyList<string>? PartitionTestResultPaths = null,
    int? PartitionReuseAttemptCount = null,
    bool? PartitionForcedFullRerun = null,
    string? OperatorReason = null,
    string? PriorGateMainSha = null,
    string? CurrentHeadMainSha = null,
    int? OperatorRegateCount = null,
    IReadOnlyList<string>? FailedCheckNames = null)
{
    public bool HasCandidate(string? branchHeadSha, string? mainHeadSha) =>
        ShaEquals(BranchHeadSha, branchHeadSha) && ShaEquals(MainHeadSha, mainHeadSha);

    public bool HasCandidatePair =>
        !string.IsNullOrWhiteSpace(BranchHeadSha) && !string.IsNullOrWhiteSpace(MainHeadSha);

    private static bool ShaEquals(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record GoalAcceptanceAttemptReceipt(
    string? BaseBuildCacheMainSha,
    long? BuildPhaseMilliseconds,
    string? BaseBuildCacheProjects,
    string? BaseBuildCacheBuiltProjects,
    string? BaseBuildCacheEvictions,
    string? BaseBuildCacheReceipt);

internal sealed record AcceptanceRetryAuditPayload(
    string GoalId,
    string OperatorReason,
    string PriorGateMainSha,
    string CurrentHeadMainSha,
    int OperatorRegateCount,
    DateTimeOffset AcceptanceFailureOccurredAt);

internal sealed record GoalOperationJournalSummary(
    string Path,
    IReadOnlyList<GoalOperationJournalEntry> Entries,
    IReadOnlyList<GoalOperationJournalEntry> LatestByOperation,
    IReadOnlyList<GoalOperationJournalEntry> InterruptedOperations)
{
    public bool HasEntries => Entries.Count > 0;
}

internal enum GoalTerminalDispositionKind
{
    Landed,
    Retired
}

internal sealed record GoalTerminalDisposition(
    GoalTerminalDispositionKind Kind,
    string Detail);

internal sealed record GoalLandingIntent(
    string GoalId,
    string GoalBranch,
    string IntegrationBranch,
    string MergeCommitSha,
    DateTimeOffset RecordedAt,
    string Source);

internal sealed record GoalLifecycleJournalEntry(
    string IdempotencyKey,
    GoalId GoalId,
    string CommandName,
    string Objective,
    DateTimeOffset At);

internal static class GoalOperationJournal
{
    internal const string AcceptanceRetryAuditOutboxKind = "acceptance-retry-audit";
    public const string TerminalDispositionOperation = "conductor:terminal-disposition";
    public const string LandingIntentOperation = "conductor:landing-intent";
    internal static Action<GoalLandingIntent>? BeforeLandingIntentAppend { get; set; }
    internal static Action? BeforeAcceptanceRetryAppend { get; set; }
    private static readonly ConcurrentDictionary<string, Lazy<SqliteRunEventStore>> RunEventStores =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static GoalOperationJournal()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static string PathFor(string executionDirectory, GoalId goalId) =>
        System.IO.Path.Combine(
            System.IO.Path.GetFullPath(executionDirectory),
            ".orchestrator",
            "goal-operations",
            $"{goalId.Value}.jsonl");

    public static string LifecycleIndexPath(string executionDirectory) =>
        System.IO.Path.Combine(
            System.IO.Path.GetFullPath(executionDirectory),
            ".orchestrator",
            "goal-operations",
            "lifecycle-index.jsonl");

    public static string Key(GoalId goalId, string operation) =>
        $"{goalId.Value}:{operation}".ToLowerInvariant();

    public static string CandidateKey(GoalId goalId, string operation, string? branchHeadSha, string? mainHeadSha)
    {
        var branch = NormalizeSha(branchHeadSha) ?? "unknown-branch";
        var main = NormalizeSha(mainHeadSha) ?? "unknown-main";
        return $"{Key(goalId, operation)}:{branch}:{main}".ToLowerInvariant();
    }

    public static string LifecycleKey(string commandName, string objective)
    {
        var normalized = $"{commandName.Trim().ToLowerInvariant()}\n{objective.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public static GoalId? TryFindLifecycleGoal(string executionDirectory, string commandName, string objective)
    {
        var path = LifecycleIndexPath(executionDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        var key = LifecycleKey(commandName, objective);
        return File.ReadLines(path)
            .Select(TryDeserializeLifecycle)
            .Where(entry => entry is not null && entry.IdempotencyKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry!.GoalId)
            .LastOrDefault();
    }

    public static void RecordLifecycleGoal(string executionDirectory, Goal goal, string commandName, string objective)
    {
        var path = LifecycleIndexPath(executionDirectory);
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var entry = new GoalLifecycleJournalEntry(
            LifecycleKey(commandName, objective),
            goal.Id,
            commandName,
            objective,
            DateTimeOffset.UtcNow);
        File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
    }

    public static void Begin(
        string executionDirectory,
        Goal goal,
        string operation,
        string? detail = null,
        string? mainHeadSha = null) =>
        Append(
            executionDirectory,
            goal.Id,
            Key(goal.Id, operation),
            operation,
            GoalOperationStatus.Begin,
            detail,
            branchHeadSha: null,
            mainHeadSha: NormalizeSha(mainHeadSha),
            acceptanceOutcome: null);

    public static void Completed(
        string executionDirectory,
        Goal goal,
        string operation,
        string? detail = null,
        string? mainHeadSha = null) =>
        Append(
            executionDirectory,
            goal.Id,
            Key(goal.Id, operation),
            operation,
            GoalOperationStatus.Completed,
            detail,
            branchHeadSha: null,
            mainHeadSha: NormalizeSha(mainHeadSha),
            acceptanceOutcome: null);

    public static void Failed(
        string executionDirectory,
        Goal goal,
        string operation,
        string? detail = null,
        string? mainHeadSha = null) =>
        Append(
            executionDirectory,
            goal.Id,
            Key(goal.Id, operation),
            operation,
            GoalOperationStatus.Failed,
            detail,
            branchHeadSha: null,
            mainHeadSha: NormalizeSha(mainHeadSha),
            acceptanceOutcome: null);

    public static void RecordLandingIntent(
        string executionDirectory,
        Goal goal,
        string goalBranch,
        string integrationBranch,
        string mergeCommitSha,
        string source,
        DateTimeOffset? recordedAt = null)
    {
        var intent = new GoalLandingIntent(
            goal.Id.Value,
            goalBranch,
            integrationBranch,
            mergeCommitSha,
            recordedAt ?? DateTimeOffset.UtcNow,
            source);
        BeforeLandingIntentAppend?.Invoke(intent);
        Append(
            executionDirectory,
            goal.Id,
            Key(goal.Id, LandingIntentOperation),
            LandingIntentOperation,
            GoalOperationStatus.Completed,
            JsonSerializer.Serialize(intent, JsonOptions),
            branchHeadSha: NormalizeSha(mergeCommitSha),
            mainHeadSha: null,
            acceptanceOutcome: null,
            at: recordedAt);
    }

    public static void TombstoneLandingIntent(
        string executionDirectory,
        Goal goal,
        string detail) =>
        Append(
            executionDirectory,
            goal.Id,
            Key(goal.Id, LandingIntentOperation),
            LandingIntentOperation,
            GoalOperationStatus.Failed,
            detail,
            branchHeadSha: null,
            mainHeadSha: null,
            acceptanceOutcome: null);

    public static void AcceptancePassed(
        string executionDirectory,
        Goal goal,
        string operation,
        string? branchHeadSha,
        string? mainHeadSha,
        string? detail = null,
        DateTimeOffset? attemptStartedAt = null,
        GoalAcceptanceAttemptReceipt? attemptReceipt = null) =>
        AppendAcceptanceOutcome(executionDirectory, goal, operation, GoalOperationStatus.Completed, "passed", branchHeadSha, mainHeadSha, detail, attemptStartedAt, attemptReceipt, failedCheckNames: null);

    public static void AcceptanceGatePassed(
        string executionDirectory,
        Goal goal,
        string operation,
        string? branchHeadSha,
        string? mainHeadSha,
        string? detail = null,
        DateTimeOffset? attemptStartedAt = null,
        GoalAcceptanceAttemptReceipt? attemptReceipt = null) =>
        AppendAcceptanceOutcome(executionDirectory, goal, operation, GoalOperationStatus.Completed, "gate-passed", branchHeadSha, mainHeadSha, detail, attemptStartedAt, attemptReceipt, failedCheckNames: null);

    public static void AcceptanceFailed(
        string executionDirectory,
        Goal goal,
        string operation,
        string? branchHeadSha,
        string? mainHeadSha,
        string? detail = null,
        DateTimeOffset? attemptStartedAt = null,
        GoalAcceptanceAttemptReceipt? attemptReceipt = null,
        IReadOnlyList<string>? failedCheckNames = null) =>
        AppendAcceptanceOutcome(executionDirectory, goal, operation, GoalOperationStatus.Failed, "failed", branchHeadSha, mainHeadSha, detail, attemptStartedAt, attemptReceipt, failedCheckNames);

    public static void AcceptanceRetried(
        string executionDirectory,
        Goal goal,
        string operatorReason,
        string? priorGateMainSha,
        string? currentHeadMainSha,
        int operatorRegateCount)
    {
        var idempotencyKey = $"{Key(goal.Id, "acceptance-retry")}:{operatorRegateCount}";
        if (Read(executionDirectory, goal.Id).Entries.Any(entry =>
                entry.IdempotencyKey.Equals(idempotencyKey, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        BeforeAcceptanceRetryAppend?.Invoke();
        Append(
            executionDirectory,
            goal.Id,
            idempotencyKey,
            "acceptance-retry",
            GoalOperationStatus.Completed,
            $"Operator acceptance re-gate {operatorRegateCount}/{Goal.OperatorAcceptanceRegateCap}: {operatorReason}",
            branchHeadSha: null,
            mainHeadSha: NormalizeSha(currentHeadMainSha),
            acceptanceOutcome: "operator-regate",
            operatorReason: operatorReason,
            priorGateMainSha: NormalizeSha(priorGateMainSha),
            currentHeadMainSha: NormalizeSha(currentHeadMainSha),
            operatorRegateCount: operatorRegateCount);
    }

    public static OrchestratorStateOutboxMessage CreateAcceptanceRetryAuditMessage(
        Goal goal,
        string operatorReason,
        string priorGateMainSha,
        string currentHeadMainSha,
        int operatorRegateCount,
        DateTimeOffset acceptanceFailureOccurredAt)
    {
        var payload = new AcceptanceRetryAuditPayload(
            goal.Id.Value,
            operatorReason,
            priorGateMainSha,
            currentHeadMainSha,
            operatorRegateCount,
            acceptanceFailureOccurredAt);
        return new OrchestratorStateOutboxMessage(
            $"{AcceptanceRetryAuditOutboxKind}:{goal.Id.Value}:{operatorRegateCount}",
            AcceptanceRetryAuditOutboxKind,
            JsonSerializer.Serialize(payload, JsonOptions),
            DateTimeOffset.UtcNow);
    }

    public static OrchestratorStateOutboxProcessingResult ApplyAcceptanceRetryAuditMessage(
        OrchestratorWorkspace workspace,
        Goal goal,
        OrchestratorStateOutboxMessage message)
    {
        if (!message.Kind.Equals(AcceptanceRetryAuditOutboxKind, StringComparison.Ordinal))
        {
            return OrchestratorStateOutboxProcessingResult.Quarantined(
                $"Cannot apply message kind '{message.Kind}' as an acceptance-retry audit.");
        }

        var payload = DeserializeAcceptanceRetryAuditMessage(message);
        if (!payload.GoalId.Equals(goal.Id.Value, StringComparison.OrdinalIgnoreCase))
        {
            return OrchestratorStateOutboxProcessingResult.Quarantined(
                $"Message targets goal '{payload.GoalId}', not '{goal.Id.Value}'.");
        }

        var escalationResolution = OperatorInbox.ResolveLandingEscalationOccurrence(
            workspace,
            goal,
            payload.OperatorReason,
            payload.AcceptanceFailureOccurredAt);
        if (escalationResolution == OperatorInbox.LandingEscalationResolution.Missing)
        {
            return OrchestratorStateOutboxProcessingResult.Quarantined(
                $"No landing escalation exists for goal '{goal.Id.Value}' at occurrence " +
                $"{payload.AcceptanceFailureOccurredAt:O}.");
        }

        // A newer failure supersedes only the escalation-resolution side effect. The accepted
        // operator re-gate is still journaled, while the newer escalation remains untouched.
        AcceptanceRetried(
            workspace.ExecutionDirectory,
            goal,
            payload.OperatorReason,
            payload.PriorGateMainSha,
            payload.CurrentHeadMainSha,
            payload.OperatorRegateCount);
        return OrchestratorStateOutboxProcessingResult.Completed;
    }

    public static AcceptanceRetryAuditPayload DeserializeAcceptanceRetryAuditMessage(
        OrchestratorStateOutboxMessage message) =>
        JsonSerializer.Deserialize<AcceptanceRetryAuditPayload>(message.PayloadJson, JsonOptions)
        ?? throw new InvalidOperationException(
            $"Acceptance-retry audit outbox message '{message.Id}' has an empty payload.");

    public static void AcceptanceBlocked(
        string executionDirectory,
        Goal goal,
        string operation,
        string blockerKind,
        string? branchHeadSha,
        string? mainHeadSha,
        string? detail = null,
        DateTimeOffset? attemptStartedAt = null,
        GoalAcceptanceAttemptReceipt? attemptReceipt = null,
        IReadOnlyList<string>? failedCheckNames = null) =>
        AppendAcceptanceOutcome(
            executionDirectory,
            goal,
            operation,
            GoalOperationStatus.Failed,
            $"blocked:{NormalizeBlockerKind(blockerKind)}",
            branchHeadSha,
            mainHeadSha,
            detail,
            attemptStartedAt,
            attemptReceipt,
            failedCheckNames);

    public static void AcceptanceSkippedAlreadyMerged(
        string executionDirectory,
        Goal goal,
        string operation,
        string? branchHeadSha,
        string? mainHeadSha,
        string detail,
        DateTimeOffset? skippedAt = null) =>
        AppendAcceptanceOutcome(
            executionDirectory,
            goal,
            operation,
            GoalOperationStatus.Skipped,
            "skip-already-merged",
            branchHeadSha,
            mainHeadSha,
            detail,
            skippedAt,
            attemptReceipt: null,
            failedCheckNames: null);

    public static GoalAcceptanceAttemptReceipt? TryExtractBaseBuildCacheReceipt(AcceptanceVerificationResult? verification)
    {
        foreach (var check in verification?.Checks ?? [])
        {
            if (TryExtractBaseBuildCacheReceipt(check.ResultSummary) is { } receipt)
            {
                return receipt;
            }
        }

        return null;
    }

    public static GoalOperationJournalEntry? NewestAcceptanceOutcomeForCandidate(
        GoalOperationJournalSummary journal,
        string? branchHeadSha,
        string? mainHeadSha)
    {
        return AcceptanceOutcomesForCandidate(journal, branchHeadSha, mainHeadSha)
            .FirstOrDefault();
    }

    public static IReadOnlyList<GoalOperationJournalEntry> AcceptanceOutcomesForCandidate(
        GoalOperationJournalSummary journal,
        string? branchHeadSha,
        string? mainHeadSha)
    {
        return journal.Entries
            .Select((entry, index) => (Entry: entry, Index: index))
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Entry.AcceptanceOutcome) &&
                item.Entry.HasCandidate(branchHeadSha, mainHeadSha))
            .OrderByDescending(item => item.Entry.At)
            .ThenByDescending(item => item.Index)
            .Select(item => item.Entry)
            .ToArray();
    }

    public static IReadOnlyList<GoalOperationJournalEntry> SupersededAcceptanceOutcomes(
        GoalOperationJournalSummary journal,
        string? branchHeadSha,
        string? mainHeadSha)
    {
        return journal.Entries
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.AcceptanceOutcome) &&
                entry.HasCandidatePair &&
                !entry.HasCandidate(branchHeadSha, mainHeadSha))
            .OrderBy(entry => entry.At)
            .ToArray();
    }

    public static void RecordTerminalDisposition(
        string executionDirectory,
        Goal goal,
        GoalTerminalDisposition disposition)
    {
        Completed(executionDirectory, goal, TerminalDispositionOperation, JsonSerializer.Serialize(disposition, JsonOptions));
        Completed(executionDirectory, goal, "conductor:land", disposition.Detail);
        Completed(executionDirectory, goal, "conductor:record", disposition.Detail);
        Completed(executionDirectory, goal, "conductor:cleanup", disposition.Detail);
    }

    public static GoalOperationJournalSummary Read(string executionDirectory, GoalId goalId)
    {
        var path = PathFor(executionDirectory, goalId);
        if (!File.Exists(path))
        {
            return new GoalOperationJournalSummary(path, [], [], []);
        }

        var entries = ReadEntries(path);
        return BuildSummary(path, entries);
    }

    public static bool HasCompletedLandingEvidence(GoalOperationJournalSummary journal) =>
        HasRetiredTerminalDisposition(journal) ||
        journal.LatestByOperation.Any(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            (entry.Operation.Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
             entry.Operation.Equals("conductor:land", StringComparison.OrdinalIgnoreCase)));

    public static bool HasCompletedRecordEvidence(GoalOperationJournalSummary journal) =>
        HasRetiredTerminalDisposition(journal) ||
        journal.LatestByOperation.Any(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            (entry.Operation.Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
             entry.Operation.Equals("conductor:record", StringComparison.OrdinalIgnoreCase)));

    public static bool HasCompletedCleanupEvidence(GoalOperationJournalSummary journal) =>
        HasRetiredTerminalDisposition(journal) ||
        journal.LatestByOperation.Any(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            (entry.Operation.Equals("workspace:remove", StringComparison.OrdinalIgnoreCase) ||
             entry.Operation.Equals("conductor:cleanup", StringComparison.OrdinalIgnoreCase)));

    public static bool HasRetiredTerminalDisposition(GoalOperationJournalSummary journal)
    {
        if (TryGetLatestTerminalDisposition(journal) is { } latestTerminalDisposition)
        {
            return latestTerminalDisposition.Kind == GoalTerminalDispositionKind.Retired;
        }

        return journal.LatestByOperation.Any(IsLegacyRetiredTerminalDispositionEntry);
    }

    public static bool HasMergeEvidenceTerminalDisposition(GoalOperationJournalSummary journal) =>
        TryGetLatestTerminalDisposition(journal) is
        {
            Kind: GoalTerminalDispositionKind.Landed,
            Detail: var detail
        } && detail.StartsWith("Goal terminalized from merge evidence at ", StringComparison.Ordinal);

    public static bool HasDurableLandingIntent(GoalOperationJournalSummary journal)
    {
        var latestLandingIntent = journal.LatestByOperation
            .LastOrDefault(entry => entry.Operation.Equals(LandingIntentOperation, StringComparison.OrdinalIgnoreCase));
        if (latestLandingIntent?.Status == GoalOperationStatus.Completed)
        {
            return true;
        }

        if (TryGetLatestTerminalDisposition(journal) is { } latestTerminalDisposition)
        {
            return latestTerminalDisposition.Kind == GoalTerminalDispositionKind.Landed ||
                latestTerminalDisposition.Detail.Contains("goal-mark-landed", StringComparison.OrdinalIgnoreCase);
        }

        if (latestLandingIntent is not null)
        {
            return false;
        }

        return journal.LatestByOperation.Any(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            (entry.Operation.Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
             entry.Operation.Equals("conductor:land", StringComparison.OrdinalIgnoreCase)) &&
            !IsLegacyRetiredTerminalDispositionEntry(entry));
    }

    public static GoalLandingIntent? TryGetLatestLandingIntent(GoalOperationJournalSummary journal)
    {
        var latestLandingIntent = journal.LatestByOperation
            .LastOrDefault(entry => entry.Operation.Equals(LandingIntentOperation, StringComparison.OrdinalIgnoreCase));
        return latestLandingIntent is null || latestLandingIntent.Status != GoalOperationStatus.Completed
            ? null
            : TryDeserializeLandingIntent(latestLandingIntent.Detail);
    }

    public static IReadOnlyDictionary<GoalId, GoalOperationJournalSummary> ReadAll(string executionDirectory) =>
        ReadAll(executionDirectory, []);

    public static IReadOnlyDictionary<GoalId, GoalOperationJournalSummary> ReadAll(
        string executionDirectory,
        IEnumerable<GoalId> expectedGoalIds)
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(executionDirectory),
            ".orchestrator",
            "goal-operations");
        var summaries = new Dictionary<GoalId, GoalOperationJournalSummary>();
        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
                if (fileName.Equals("lifecycle-index", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var goalId = new GoalId(fileName);
                summaries[goalId] = BuildSummary(path, ReadEntries(path));
            }
        }

        foreach (var goalId in expectedGoalIds)
        {
            summaries.TryAdd(goalId, new GoalOperationJournalSummary(PathFor(executionDirectory, goalId), [], [], []));
        }

        return summaries;
    }

    private static GoalOperationJournalEntry[] ReadEntries(string path) =>
        File.ReadLines(path)
            .Select(TryDeserialize)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .OrderBy(entry => entry.At)
            .ToArray();

    private static GoalOperationJournalSummary BuildSummary(string path, GoalOperationJournalEntry[] entries)
    {
        var latest = entries
            .GroupBy(entry => entry.IdempotencyKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(entry => entry.At).Last())
            .OrderBy(entry => entry.At)
            .ToArray();
        var interrupted = latest
            .Where(entry => entry.Status == GoalOperationStatus.Begin)
            .ToArray();
        return new GoalOperationJournalSummary(path, entries, latest, interrupted);
    }

    private static bool IsRetiredTerminalDispositionEntry(GoalOperationJournalEntry entry)
    {
        if (entry.Status != GoalOperationStatus.Completed)
        {
            return false;
        }

        if (entry.Operation.Equals(TerminalDispositionOperation, StringComparison.OrdinalIgnoreCase))
        {
            return TryDeserializeTerminalDisposition(entry.Detail) is { Kind: GoalTerminalDispositionKind.Retired };
        }

        return IsLegacyRetiredTerminalDispositionEntry(entry);
    }

    private static bool IsLegacyRetiredTerminalDispositionEntry(GoalOperationJournalEntry entry) =>
        entry.Status == GoalOperationStatus.Completed &&
        (entry.Operation.Equals("conductor:land", StringComparison.OrdinalIgnoreCase) ||
         entry.Operation.Equals("conductor:record", StringComparison.OrdinalIgnoreCase) ||
         entry.Operation.Equals("conductor:cleanup", StringComparison.OrdinalIgnoreCase)) &&
        entry.Detail?.Contains("Terminal sweep retired missing goal artifact", StringComparison.OrdinalIgnoreCase) == true;

    private static GoalTerminalDisposition? TryDeserializeTerminalDisposition(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GoalTerminalDisposition>(detail, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static GoalLandingIntent? TryDeserializeLandingIntent(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GoalLandingIntent>(detail, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static GoalTerminalDisposition? TryGetLatestTerminalDisposition(GoalOperationJournalSummary journal)
    {
        var latestTerminalDisposition = journal.LatestByOperation
            .LastOrDefault(entry => entry.Operation.Equals(TerminalDispositionOperation, StringComparison.OrdinalIgnoreCase));
        return latestTerminalDisposition is null || latestTerminalDisposition.Status != GoalOperationStatus.Completed
            ? null
            : TryDeserializeTerminalDisposition(latestTerminalDisposition.Detail);
    }

    private static void Append(
        string executionDirectory,
        GoalId goalId,
        string operation,
        GoalOperationStatus status,
        string? detail)
    {
        Append(
            executionDirectory,
            goalId,
            Key(goalId, operation),
            operation,
            status,
            detail,
            branchHeadSha: null,
            mainHeadSha: null,
            acceptanceOutcome: null);
    }

    private static void AppendAcceptanceOutcome(
        string executionDirectory,
        Goal goal,
        string operation,
        GoalOperationStatus status,
        string acceptanceOutcome,
        string? branchHeadSha,
        string? mainHeadSha,
        string? detail,
        DateTimeOffset? attemptStartedAt,
        GoalAcceptanceAttemptReceipt? attemptReceipt,
        IReadOnlyList<string>? failedCheckNames)
    {
        Append(
            executionDirectory,
            goal.Id,
            CandidateKey(goal.Id, operation, branchHeadSha, mainHeadSha),
            operation,
            status,
            detail,
            NormalizeSha(branchHeadSha),
            NormalizeSha(mainHeadSha),
            acceptanceOutcome,
            attemptStartedAt,
            attemptReceipt,
            failedCheckNames: failedCheckNames);
    }

    private static void Append(
        string executionDirectory,
        GoalId goalId,
        string idempotencyKey,
        string operation,
        GoalOperationStatus status,
        string? detail,
        string? branchHeadSha,
        string? mainHeadSha,
        string? acceptanceOutcome,
        DateTimeOffset? at = null,
        GoalAcceptanceAttemptReceipt? attemptReceipt = null,
        string? operatorReason = null,
        string? priorGateMainSha = null,
        string? currentHeadMainSha = null,
        int? operatorRegateCount = null,
        IReadOnlyList<string>? failedCheckNames = null)
    {
        var path = PathFor(executionDirectory, goalId);
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var entry = new GoalOperationJournalEntry(
            idempotencyKey,
            goalId,
            operation,
            status,
            at ?? DateTimeOffset.UtcNow,
            detail,
            branchHeadSha,
            mainHeadSha,
            acceptanceOutcome,
            attemptReceipt?.BaseBuildCacheMainSha,
            attemptReceipt?.BuildPhaseMilliseconds,
            attemptReceipt?.BaseBuildCacheProjects,
            attemptReceipt?.BaseBuildCacheBuiltProjects,
            attemptReceipt?.BaseBuildCacheEvictions,
            attemptReceipt?.BaseBuildCacheReceipt,
            OperatorReason: operatorReason,
            PriorGateMainSha: priorGateMainSha,
            CurrentHeadMainSha: currentHeadMainSha,
            OperatorRegateCount: operatorRegateCount,
            FailedCheckNames: failedCheckNames);
        File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
        TryAppendRunEvent(executionDirectory, entry);
    }

    private static GoalAcceptanceAttemptReceipt? TryExtractBaseBuildCacheReceipt(string? resultSummary)
    {
        if (string.IsNullOrWhiteSpace(resultSummary))
        {
            return null;
        }

        const string prefix = "base-build-cache ";
        var prefixIndex = resultSummary.IndexOf(prefix, StringComparison.Ordinal);
        if (prefixIndex < 0)
        {
            return null;
        }

        var start = prefixIndex + prefix.Length;
        var end = resultSummary.IndexOf(';', start);
        var receipt = (end < 0 ? resultSummary[start..] : resultSummary[start..end]).Trim();
        var fields = receipt
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

        fields.TryGetValue("main_sha", out var mainSha);
        fields.TryGetValue("projects", out var projects);
        fields.TryGetValue("built_projects", out var builtProjects);
        fields.TryGetValue("evictions", out var evictions);
        long buildPhaseMilliseconds = 0;
        var hasBuildPhase = fields.TryGetValue("build_phase_ms", out var buildPhaseText) &&
            long.TryParse(
                buildPhaseText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out buildPhaseMilliseconds);

        return string.IsNullOrWhiteSpace(mainSha) &&
            !hasBuildPhase &&
            string.IsNullOrWhiteSpace(projects) &&
            string.IsNullOrWhiteSpace(builtProjects) &&
            string.IsNullOrWhiteSpace(evictions)
                ? null
                : new GoalAcceptanceAttemptReceipt(
                    NormalizeSha(mainSha),
                    hasBuildPhase ? buildPhaseMilliseconds : null,
                    string.IsNullOrWhiteSpace(projects) ? null : projects,
                    string.IsNullOrWhiteSpace(builtProjects) ? null : builtProjects,
                    string.IsNullOrWhiteSpace(evictions) ? null : evictions,
                    $"{prefix}{receipt}");
    }

    private static string? NormalizeSha(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeBlockerKind(string value)
    {
        var normalized = value.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static void TryAppendRunEvent(string executionDirectory, GoalOperationJournalEntry entry)
    {
        try
        {
            var storePath = System.IO.Path.Combine(
                System.IO.Path.GetFullPath(executionDirectory),
                ".orchestrator",
                "run-events.db");
            var store = GetRunEventStore(storePath);
            store.AppendAsync(new RunEventAppend(
                RunEventTypes.GoalOperation,
                entry.GoalId.Value,
                entry.Operation,
                entry.Status.ToString(),
                entry.Detail,
                JsonSerializer.Serialize(entry, JsonOptions),
                entry.At))
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // The journal is still the command's primary lifecycle side effect; observability must not
            // make dispatch or acceptance fail.
        }
    }

    internal static SqliteRunEventStore GetRunEventStore(string storePath)
    {
        var normalizedPath = Path.GetFullPath(storePath);
        var lazy = RunEventStores.GetOrAdd(
            normalizedPath,
            static path => new Lazy<SqliteRunEventStore>(
                () => new SqliteRunEventStore(path),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            if (RunEventStores.TryGetValue(normalizedPath, out var current) &&
                ReferenceEquals(current, lazy))
            {
                RunEventStores.TryRemove(normalizedPath, out _);
            }

            throw;
        }
    }

    private static GoalOperationJournalEntry? TryDeserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<GoalOperationJournalEntry>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GoalLifecycleJournalEntry? TryDeserializeLifecycle(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<GoalLifecycleJournalEntry>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
