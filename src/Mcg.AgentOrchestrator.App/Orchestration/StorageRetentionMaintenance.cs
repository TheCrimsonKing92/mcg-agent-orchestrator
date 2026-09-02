using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record StorageRetentionGoal(
    string GoalId,
    GoalStatus Status,
    IReadOnlyDictionary<string, WorkTaskStatus> TaskStatuses,
    IReadOnlyCollection<string>? PromptPaths = null)
{
    public bool IsTerminal => Status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;

    public static StorageRetentionGoal FromSnapshot(GoalSnapshot snapshot)
    {
        var promptPaths = snapshot.Tasks
            .SelectMany(task => (task.DispatchHistory ?? []).Append(task.LastDispatch))
            .Where(dispatch => !string.IsNullOrWhiteSpace(dispatch?.PromptPath))
            .Select(dispatch => Path.GetFullPath(dispatch!.PromptPath!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(
            snapshot.Id,
            snapshot.Status,
            snapshot.Tasks.ToDictionary(task => task.Id, task => task.Status, StringComparer.OrdinalIgnoreCase),
            promptPaths);
    }
}

internal sealed record StorageRetentionResult(
    int WorkerArtifactsDeleted,
    int WorkerLogsCompressed,
    int SuccessfulTrxReceiptsWritten,
    int AcceptanceArtifactsDeleted,
    int PromptArtifactsDeleted,
    int GoalJournalsArchived,
    string SweepId,
    IReadOnlyList<EvidenceRetentionDecision> Decisions)
{
    public bool Failed => Decisions.Any(decision => decision.Action == EvidenceRetentionAction.Failed);
}

internal static partial class StorageRetentionMaintenance
{
    internal static readonly TimeSpan WorkerCompressionAge = TimeSpan.FromDays(1);
    internal static readonly TimeSpan WorkerDeletionAge = TimeSpan.FromDays(14);
    internal static readonly TimeSpan AcceptanceArtifactMaxAge = TimeSpan.FromDays(14);
    internal const long AcceptanceArtifactMaxBytes = 256L * 1024 * 1024;

    [GeneratedRegex("^(?<dispatch>(?<goal>[0-9a-f]{8})-(?<task>[0-9a-f]{8})-[0-9]{14})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DispatchArtifactNameRegex();

    public static StorageRetentionResult Run(
        OrchestratorWorkspace workspace,
        IEnumerable<Goal> goals,
        DateTimeOffset now)
    {
        var retentionGoals = goals.Select(goal => new StorageRetentionGoal(
            goal.Id.Value,
            goal.Status,
            goal.Tasks.ToDictionary(task => task.Id.Value, task => task.Status, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
        return Run(workspace.LogDirectory, workspace.OrchestratorDirectory, workspace.ExecutionDirectory, retentionGoals, now);
    }

    internal static IReadOnlyCollection<StorageRetentionGoal> LoadPersistedGoals(
        ITransactionalOrchestratorStateRepository stateRepository)
    {
        var goalIds = stateRepository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult()
            .Select(summary => new GoalId(summary.Id))
            .ToArray();
        var goals = new List<StorageRetentionGoal>(goalIds.Length);
        foreach (var goalId in goalIds)
        {
            var snapshot = stateRepository.LoadGoalAsync(goalId).GetAwaiter().GetResult();
            if (snapshot is not null)
            {
                goals.Add(StorageRetentionGoal.FromSnapshot(snapshot));
            }
        }

        return goals;
    }

    internal static StorageRetentionResult Run(
        string logDirectory,
        string orchestratorDirectory,
        string executionDirectory,
        IReadOnlyCollection<StorageRetentionGoal> goals,
        DateTimeOffset now,
        Action? beforeGoalJournalArchiveForTests = null)
    {
        var decisions = new List<EvidenceRetentionDecision>();
        var sweepId = Guid.NewGuid().ToString("N");
        using var lease = new Mutex(false, LeaseNameFor(orchestratorDirectory));
        var ownsLease = false;
        try
        {
            try
            {
                ownsLease = lease.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                ownsLease = true;
            }

            if (!ownsLease)
            {
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.RunEvents,
                    EvidenceRetentionAction.DeferredLease,
                    orchestratorDirectory,
                    null,
                    EvidenceOwnerResolution.Unrecorded,
                    "sweep-lease-unavailable"));
                return new StorageRetentionResult(0, 0, 0, 0, 0, 0, sweepId, decisions);
            }

            try
            {
                SweepWorkerArtifacts(logDirectory, goals, now, decisions);
                SweepAcceptanceArtifacts(
                    orchestratorDirectory,
                    goals,
                    now,
                    decisions);
                SweepPrompts(orchestratorDirectory, goals, now, decisions);
                RecordGoalEventPreservation(orchestratorDirectory, goals, decisions);
                beforeGoalJournalArchiveForTests?.Invoke();
                ArchiveGoalJournals(executionDirectory, goals, decisions);
            }
            catch (Exception ex)
            {
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.RunEvents,
                    EvidenceRetentionAction.Failed,
                    orchestratorDirectory,
                    null,
                    EvidenceOwnerResolution.Unrecorded,
                    "sweep-failed",
                    FailureExceptionType: ex.GetType().Name));
            }

            return BuildResult(sweepId, decisions);
        }
        finally
        {
            if (ownsLease)
            {
                lease.ReleaseMutex();
            }
        }
    }

    internal static string LeaseNameFor(string orchestratorDirectory)
    {
        var normalized = Path.GetFullPath(orchestratorDirectory).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return $"Local\\mcg-storage-retention-{hash[..24]}";
    }

    internal static string AttemptLeaseNameFor(string goalDirectory)
    {
        var normalized = Path.GetFullPath(goalDirectory).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return $"Local\\mcg-acceptance-artifacts-{hash[..24]}";
    }

    internal static IDisposable AcquireAttemptWriterLease(string goalDirectory) =>
        MutexLease.Acquire(AttemptLeaseNameFor(goalDirectory));

    private static StorageRetentionResult BuildResult(
        string sweepId,
        IReadOnlyList<EvidenceRetentionDecision> decisions) =>
        new(
            decisions.Count(decision =>
                decision.Family == EvidenceArtifactFamily.DispatchLogs &&
                decision.Action == EvidenceRetentionAction.Deleted),
            decisions.Count(decision =>
                decision.Family == EvidenceArtifactFamily.DispatchLogs &&
                decision.Action == EvidenceRetentionAction.Compressed),
            decisions.Count(decision => decision.Action == EvidenceRetentionAction.ReceiptWritten),
            decisions.Count(decision =>
                (decision.Family is EvidenceArtifactFamily.AcceptanceGateAttempts or EvidenceArtifactFamily.PreReviewEvidenceAttempts) &&
                decision.Action == EvidenceRetentionAction.Deleted),
            decisions.Count(decision =>
                decision.Family == EvidenceArtifactFamily.Prompts &&
                decision.Action == EvidenceRetentionAction.Deleted),
            decisions.Count(decision =>
                decision.Family == EvidenceArtifactFamily.GoalOperationJournals &&
                decision.Reason == "retired-journal-archived"),
            sweepId,
            decisions);

    private static (int Deleted, int Compressed) SweepWorkerArtifacts(
        string logDirectory,
        IReadOnlyCollection<StorageRetentionGoal> goals,
        DateTimeOffset now,
        List<EvidenceRetentionDecision> decisions)
    {
        if (!Directory.Exists(logDirectory))
        {
            return (0, 0);
        }

        var byPrefix = goals
            .GroupBy(goal => goal.GoalId[..Math.Min(8, goal.GoalId.Length)], StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var artifacts = Directory.EnumerateFiles(logDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Match: DispatchArtifactNameRegex().Match(Path.GetFileName(path))))
            .ToArray();
        var dispatchFailures = artifacts
            .Where(artifact =>
                artifact.Match.Success &&
                byPrefix.TryGetValue(artifact.Match.Groups["goal"].Value, out var goal) &&
                goal.IsTerminal)
            .Select(artifact => artifact.Match.Groups["dispatch"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                dispatchPrefix => dispatchPrefix,
                dispatchPrefix => DispatchFailed(logDirectory, dispatchPrefix),
                StringComparer.OrdinalIgnoreCase);
        var lastFailedDispatches = artifacts
            .Where(artifact =>
                artifact.Match.Success &&
                byPrefix.TryGetValue(artifact.Match.Groups["goal"].Value, out var goal) &&
                goal.IsTerminal &&
                goal.TaskStatuses.Count(pair => pair.Key.StartsWith(
                    artifact.Match.Groups["task"].Value,
                    StringComparison.OrdinalIgnoreCase)) == 1)
            .GroupBy(
                artifact => $"{artifact.Match.Groups["goal"].Value}:{artifact.Match.Groups["task"].Value}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .Select(artifact => artifact.Match.Groups["dispatch"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(dispatch => dispatchFailures[dispatch])
                .OrderByDescending(dispatch => dispatch, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault())
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var legacyDiagnostics = Path.Combine(logDirectory, "dispatch-diagnostics.jsonl");
        if (File.Exists(legacyDiagnostics))
        {
            decisions.Add(new EvidenceRetentionDecision(
                EvidenceArtifactFamily.DispatchLogs,
                EvidenceRetentionAction.RetainedUndecidable,
                legacyDiagnostics,
                null,
                EvidenceOwnerResolution.Unrecorded,
                "global-diagnostics-have-no-unique-goal-owner"));
        }

        var deleted = 0;
        var compressed = 0;
        foreach (var artifact in artifacts)
        {
            var (path, match) = artifact;
            if (!match.Success)
            {
                continue;
            }

            if (!byPrefix.TryGetValue(match.Groups["goal"].Value, out var goal))
            {
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.DispatchLogs,
                    EvidenceRetentionAction.RetainedUndecidable,
                    path,
                    null,
                    EvidenceOwnerResolution.AmbiguousPrefix,
                    "goal-prefix-is-unmatched-or-ambiguous"));
                continue;
            }

            if (!goal.IsTerminal)
            {
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.DispatchLogs,
                    EvidenceRetentionAction.Preserved,
                    path,
                    goal.GoalId,
                    EvidenceOwnerResolution.NonTerminal,
                    "goal-is-non-terminal"));
                continue;
            }

            var taskPrefix = match.Groups["task"].Value;
            var matchingTasks = goal.TaskStatuses
                .Where(pair => pair.Key.StartsWith(taskPrefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matchingTasks.Length != 1)
            {
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.DispatchLogs,
                    EvidenceRetentionAction.RetainedUndecidable,
                    path,
                    goal.GoalId,
                    matchingTasks.Length == 0
                        ? EvidenceOwnerResolution.Unmatched
                        : EvidenceOwnerResolution.AmbiguousPrefix,
                    matchingTasks.Length == 0 ? "task-prefix-unmatched" : "task-prefix-ambiguous"));
                continue;
            }

            var preserveRaw = lastFailedDispatches.Contains(match.Groups["dispatch"].Value);
            if (preserveRaw)
            {
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.DispatchLogs,
                    EvidenceRetentionAction.Preserved,
                    path,
                    goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    "failure-evidence"));
                continue;
            }

            var age = now - File.GetLastWriteTimeUtc(path);
            if (age > WorkerDeletionAge)
            {
                var length = SafeLength(path);
                var deletion = TryDeleteExclusive(path);
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.DispatchLogs,
                    deletion.Success ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.DeferredLocked,
                    path,
                    goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    deletion.Success ? "past-deletion-age" : "exclusive-delete-failed",
                    BytesAttempted: length,
                    BytesReclaimed: deletion.Success ? length : 0,
                    FailureExceptionType: deletion.ExceptionType));
                if (deletion.Success)
                {
                    deleted++;
                }
                continue;
            }

            if (age < WorkerCompressionAge || !IsRawWorkerLog(path))
            {
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.DispatchLogs,
                    EvidenceRetentionAction.Preserved,
                    path,
                    goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    "within-grace-period"));
                continue;
            }

            var compression = TryCompressExclusive(path);
            decisions.Add(new EvidenceRetentionDecision(
                EvidenceArtifactFamily.DispatchLogs,
                compression.Success ? EvidenceRetentionAction.Compressed : EvidenceRetentionAction.DeferredLocked,
                path,
                goal.GoalId,
                EvidenceOwnerResolution.UniqueTerminal,
                compression.Success ? "past-compression-age" : "exclusive-compression-failed",
                FailureExceptionType: compression.ExceptionType));
            if (compression.Success)
            {
                compressed++;
            }
        }

        return (deleted, compressed);
    }

    private static bool DispatchFailed(string logDirectory, string dispatchPrefix)
    {
        var exitPath = Path.Combine(logDirectory, dispatchPrefix + ".exit.txt");
        if (DispatchExitArtifacts.TryRead(exitPath, out var exitArtifact))
        {
            return exitArtifact.ExitCode != 0;
        }

        var childExitPath = Path.Combine(logDirectory, dispatchPrefix + ".child-exit.json");
        try
        {
            if (!File.Exists(childExitPath))
            {
                // A terminal task without any durable exit artifact is the killed/hung/reaped case.
                // Preserve its raw logs because successful completion has not been positively established.
                return true;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(childExitPath));
            return document.RootElement.TryGetProperty("exitCode", out var exitCode) &&
                exitCode.ValueKind == JsonValueKind.Number &&
                exitCode.GetInt32() != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    private static bool IsRawWorkerLog(string path) =>
        !path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) &&
        (path.EndsWith(".out.log", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".err.log", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(path).Contains(".log.part-", StringComparison.OrdinalIgnoreCase));

    private static (bool Success, string? ExceptionType) TryCompressExclusive(string path)
    {
        var gzipPath = path + ".gz";
        var temporaryPath = gzipPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
            {
                input.CopyTo(gzip);
            }

            File.Move(temporaryPath, gzipPath, overwrite: true);
            File.Delete(path);
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporaryPath);
            return (false, ex.GetType().Name);
        }
    }

    private static (int Receipts, int Deleted) SweepAcceptanceArtifacts(
        string orchestratorDirectory,
        IReadOnlyCollection<StorageRetentionGoal> goals,
        DateTimeOffset now,
        List<EvidenceRetentionDecision> decisions)
    {
        var totals = (Receipts: 0, Deleted: 0);
        foreach (var family in new[]
        {
            (Directory: "acceptance-gate-attempts", Family: EvidenceArtifactFamily.AcceptanceGateAttempts),
            (Directory: "pre-review-evidence-attempts", Family: EvidenceArtifactFamily.PreReviewEvidenceAttempts)
        })
        {
            var result = SweepAttemptRoot(
                Path.Combine(orchestratorDirectory, family.Directory),
                family.Family,
                goals,
                now,
                decisions);
            totals.Receipts += result.Receipts;
            totals.Deleted += result.Deleted;
        }

        return totals;
    }

    private static (int Receipts, int Deleted) SweepAttemptRoot(
        string root,
        EvidenceArtifactFamily family,
        IReadOnlyCollection<StorageRetentionGoal> goals,
        DateTimeOffset now,
        List<EvidenceRetentionDecision> decisions)
    {
        if (!Directory.Exists(root))
        {
            return (0, 0);
        }

        var receipts = 0;
        var deleted = 0;
        var candidates = new List<(FileInfo File, bool CountBound, string GoalId)>();
        foreach (var goalDirectory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            using var attemptLease = MutexLease.TryAcquire(AttemptLeaseNameFor(goalDirectory));
            if (attemptLease is null)
            {
                decisions.Add(new EvidenceRetentionDecision(
                    family,
                    EvidenceRetentionAction.DeferredLease,
                    goalDirectory,
                    null,
                    EvidenceOwnerResolution.Unrecorded,
                    "attempt-writer-lease-unavailable"));
                continue;
            }

            var goalId = Path.GetFileName(goalDirectory);
            var owners = goals.Where(goal => goal.GoalId.Equals(goalId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (owners.Length != 1)
            {
                decisions.Add(new EvidenceRetentionDecision(
                    family,
                    EvidenceRetentionAction.RetainedUndecidable,
                    goalDirectory,
                    null,
                    owners.Length == 0 ? EvidenceOwnerResolution.Unmatched : EvidenceOwnerResolution.AmbiguousPrefix,
                    "goal-directory-has-no-unique-full-id-owner"));
                continue;
            }

            var goal = owners[0];
            if (!goal.IsTerminal)
            {
                decisions.Add(new EvidenceRetentionDecision(
                    family,
                    EvidenceRetentionAction.Preserved,
                    goalDirectory,
                    goal.GoalId,
                    EvidenceOwnerResolution.NonTerminal,
                    "goal-is-non-terminal"));
                continue;
            }

            if (!TryReadAttemptIdentities(goalDirectory, goal.GoalId, out var attempts, out var invalidReason))
            {
                decisions.Add(new EvidenceRetentionDecision(
                    family,
                    EvidenceRetentionAction.RetainedUndecidable,
                    goalDirectory,
                    goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    invalidReason));
                continue;
            }

            var live = attempts.FirstOrDefault(attempt => !attempt.Reconciled);
            if (live is not null)
            {
                decisions.Add(new EvidenceRetentionDecision(
                    family,
                    EvidenceRetentionAction.DeferredLive,
                    goalDirectory,
                    goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    "attempt-is-unreconciled",
                    live.AttemptId,
                    live.Ordinal));
                continue;
            }

            var protectedAttemptIds = EvidenceRetentionPolicy.ProtectedAttemptIds(attempts);
            var countBoundAttemptIds = EvidenceRetentionPolicy.AttemptIdsPastCountBound(
                attempts,
                ConductorParallelAcceptanceAttemptCoordinator.RetainedAttemptCountPerGoal,
                protectedAttemptIds);
            foreach (var trxPath in Directory.EnumerateFiles(goalDirectory, "*.trx", SearchOption.AllDirectories))
            {
                var protectedAttempt = protectedAttemptIds.FirstOrDefault(id => BelongsToAttempt(trxPath, id));
                if (protectedAttempt is not null)
                {
                    var identity = attempts.Single(attempt => attempt.AttemptId.Equals(protectedAttempt, StringComparison.OrdinalIgnoreCase));
                    decisions.Add(new EvidenceRetentionDecision(
                        family,
                        EvidenceRetentionAction.Preserved,
                        trxPath,
                        goal.GoalId,
                        EvidenceOwnerResolution.UniqueTerminal,
                        identity.Failed ? "last-failing-attempt" : "final-attempt",
                        identity.AttemptId,
                        identity.Ordinal));
                    continue;
                }

                if (!TryWriteSuccessfulTrxReceipt(trxPath))
                {
                    decisions.Add(new EvidenceRetentionDecision(
                        family,
                        EvidenceRetentionAction.Preserved,
                        trxPath,
                        goal.GoalId,
                        EvidenceOwnerResolution.UniqueTerminal,
                        "trx-is-failing-or-unreadable"));
                    continue;
                }

                receipts++;
                decisions.Add(new EvidenceRetentionDecision(
                    family,
                    EvidenceRetentionAction.ReceiptWritten,
                    trxPath + ".test-identities.json",
                    goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    "successful-trx-identities-preserved"));
                var length = SafeLength(trxPath);
                var deletion = TryDeleteExclusive(trxPath);
                decisions.Add(new EvidenceRetentionDecision(
                    family,
                    deletion.Success ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.DeferredLocked,
                    trxPath,
                    goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    deletion.Success ? "successful-trx-reduced-to-receipt" : "exclusive-delete-failed",
                    BytesAttempted: length,
                    BytesReclaimed: deletion.Success ? length : 0,
                    FailureExceptionType: deletion.ExceptionType));
                if (deletion.Success)
                {
                    deleted++;
                }
            }

            foreach (var path in Directory.EnumerateFiles(goalDirectory, "*", SearchOption.AllDirectories))
            {
                var protectedAttempt = protectedAttemptIds.FirstOrDefault(id => BelongsToAttempt(path, id));
                if (protectedAttempt is not null)
                {
                    var identity = attempts.Single(attempt => attempt.AttemptId.Equals(protectedAttempt, StringComparison.OrdinalIgnoreCase));
                    decisions.Add(new EvidenceRetentionDecision(
                        family,
                        EvidenceRetentionAction.Preserved,
                        path,
                        goal.GoalId,
                        EvidenceOwnerResolution.UniqueTerminal,
                        identity.Failed ? "last-failing-attempt" : "final-attempt",
                        identity.AttemptId,
                        identity.Ordinal));
                    continue;
                }

                if (!IsAcceptanceIndex(path) && !path.EndsWith(".test-identities.json", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add((
                        new FileInfo(path),
                        countBoundAttemptIds.Any(id => BelongsToAttempt(path, id)),
                        goal.GoalId));
                }
            }
        }

        var totalBytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(SafeLength)
            .Sum();
        foreach (var candidate in candidates
            .OrderBy(item => item.File.LastWriteTimeUtc)
            .ThenBy(item => item.File.FullName, StringComparer.OrdinalIgnoreCase))
        {
            var aged = now - candidate.File.LastWriteTimeUtc > AcceptanceArtifactMaxAge;
            if (!candidate.CountBound && !aged && totalBytes <= AcceptanceArtifactMaxBytes)
            {
                continue;
            }

            var length = SafeLength(candidate.File.FullName);
            var deletion = TryDeleteExclusive(candidate.File.FullName);
            decisions.Add(new EvidenceRetentionDecision(
                family,
                deletion.Success ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.DeferredLocked,
                candidate.File.FullName,
                candidate.GoalId,
                EvidenceOwnerResolution.UniqueTerminal,
                deletion.Success
                    ? candidate.CountBound ? "past-count-bound" : aged ? "past-age-bound" : "past-byte-bound"
                    : "exclusive-delete-failed",
                BytesAttempted: length,
                BytesReclaimed: deletion.Success ? length : 0,
                FailureExceptionType: deletion.ExceptionType));
            if (deletion.Success)
            {
                deleted++;
                totalBytes = Math.Max(0, totalBytes - length);
            }
        }

        return (receipts, deleted);
    }

    private static bool TryReadAttemptIdentities(
        string goalDirectory,
        string expectedGoalId,
        out IReadOnlyCollection<RetentionAttemptIdentity> attempts,
        out string reason)
    {
        var result = new List<RetentionAttemptIdentity>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(goalDirectory, "*.attempt.json", SearchOption.TopDirectoryOnly))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;
                if (!root.TryGetProperty("attemptId", out var attemptIdValue) ||
                    attemptIdValue.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("goalId", out var goalIdValue) ||
                    goalIdValue.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("ordinal", out var ordinalValue) ||
                    !ordinalValue.TryGetInt32(out var ordinal) ||
                    !root.TryGetProperty("startedAt", out var startedAtValue) ||
                    !startedAtValue.TryGetDateTimeOffset(out var startedAt) ||
                    !root.TryGetProperty("outcome", out var outcomeValue))
                {
                    attempts = [];
                    reason = "attempt-metadata-missing-authoritative-identity";
                    return false;
                }

                var attemptId = attemptIdValue.GetString()!;
                var serializedGoalId = goalIdValue.GetString();
                var expectedAttemptId = Path.GetFileName(path)[..^".attempt.json".Length];
                if (!expectedGoalId.Equals(Path.GetFileName(goalDirectory), StringComparison.OrdinalIgnoreCase) ||
                    !expectedGoalId.Equals(serializedGoalId, StringComparison.OrdinalIgnoreCase) ||
                    !expectedAttemptId.Equals(attemptId, StringComparison.OrdinalIgnoreCase))
                {
                    attempts = [];
                    reason = "attempt-metadata-canonical-identity-mismatch";
                    return false;
                }

                var failed = outcomeValue.ValueKind == JsonValueKind.Number && outcomeValue.TryGetInt32(out var numeric)
                    ? numeric == (int)ConductorParallelAcceptanceAttemptOutcome.Failed
                    : outcomeValue.ValueKind == JsonValueKind.String &&
                      outcomeValue.GetString()?.Equals("failed", StringComparison.OrdinalIgnoreCase) == true;
                var reconciled = root.TryGetProperty("reconciledAt", out var reconciledAt) &&
                    reconciledAt.ValueKind == JsonValueKind.String &&
                    reconciledAt.TryGetDateTimeOffset(out _);
                result.Add(new RetentionAttemptIdentity(attemptId, ordinal, startedAt, failed, reconciled));
            }

            if (result.Count == 0)
            {
                attempts = [];
                reason = "goal-directory-has-no-attempt-metadata";
                return false;
            }

            attempts = result;
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            attempts = [];
            reason = $"attempt-metadata-unreadable:{ex.GetType().Name}";
            return false;
        }
    }

    private static bool TryWriteSuccessfulTrxReceipt(string trxPath)
    {
        var receiptPath = trxPath + ".test-identities.json";
        if (File.Exists(receiptPath))
        {
            try
            {
                using var existing = JsonDocument.Parse(File.ReadAllText(receiptPath));
                return existing.RootElement.TryGetProperty("testIdentities", out var identities) &&
                    identities.ValueKind == JsonValueKind.Array && identities.GetArrayLength() > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
        }

        var temporaryPath = receiptPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using var input = new FileStream(trxPath, FileMode.Open, FileAccess.Read, FileShare.None);
            var document = XDocument.Load(input, LoadOptions.None);
            var counters = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Counters");
            if (counters is null ||
                !long.TryParse(counters.Attribute("failed")?.Value, out var failed) ||
                failed != 0)
            {
                return false;
            }

            var identities = document.Descendants()
                .Where(element => element.Name.LocalName == "UnitTestResult")
                .Select(element => new
                {
                    name = element.Attribute("testName")?.Value,
                    outcome = element.Attribute("outcome")?.Value
                })
                .Where(identity => !string.IsNullOrWhiteSpace(identity.name))
                .Distinct()
                .OrderBy(identity => identity.name, StringComparer.Ordinal)
                .ToArray();
            if (identities.Length == 0)
            {
                return false;
            }
            var receipt = new
            {
                source = Path.GetFileName(trxPath),
                total = counters.Attribute("total")?.Value,
                executed = counters.Attribute("executed")?.Value,
                passed = counters.Attribute("passed")?.Value,
                failed = counters.Attribute("failed")?.Value,
                testIdentities = identities
            };
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(receipt));
            File.Move(temporaryPath, receiptPath, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or JsonException)
        {
            TryDelete(temporaryPath);
            return false;
        }
    }

    private static bool IsAcceptanceIndex(string path) =>
        Path.GetFileName(path).Equals("attempt-sequence.txt", StringComparison.OrdinalIgnoreCase);

    private static bool BelongsToAttempt(string path, string? attemptId)
    {
        if (string.IsNullOrWhiteSpace(attemptId))
        {
            return false;
        }

        return Path.GetFileName(path).StartsWith(attemptId + ".", StringComparison.OrdinalIgnoreCase) ||
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals(attemptId + ".receipts", StringComparison.OrdinalIgnoreCase));
    }

    private static int SweepPrompts(
        string orchestratorDirectory,
        IReadOnlyCollection<StorageRetentionGoal> goals,
        DateTimeOffset now,
        List<EvidenceRetentionDecision> decisions)
    {
        var root = Path.Combine(orchestratorDirectory, "prompts");
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var ownersByPath = goals
            .SelectMany(goal => (goal.PromptPaths ?? []).Select(path => (Path: Path.GetFullPath(path), Goal: goal)))
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Goal).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            var fullPath = Path.GetFullPath(path);
            if (!ownersByPath.TryGetValue(fullPath, out var owners) || owners.Length != 1)
            {
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.Prompts,
                    EvidenceRetentionAction.RetainedUndecidable,
                    path,
                    null,
                    owners is { Length: > 1 } ? EvidenceOwnerResolution.AmbiguousPrefix : EvidenceOwnerResolution.Unrecorded,
                    owners is { Length: > 1 } ? "prompt-path-has-multiple-recorded-owners" : "prompt-path-is-unrecorded"));
                continue;
            }

            var goal = owners[0];
            if (!goal.IsTerminal)
            {
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.Prompts,
                    EvidenceRetentionAction.Preserved,
                    path,
                    goal.GoalId,
                    EvidenceOwnerResolution.NonTerminal,
                    "goal-is-non-terminal"));
                continue;
            }

            if (now - File.GetLastWriteTimeUtc(path) <= WorkerDeletionAge)
            {
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.Prompts,
                    EvidenceRetentionAction.Preserved,
                    path,
                    goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    "within-grace-period"));
                continue;
            }

            var length = SafeLength(path);
            var deletion = TryDeleteExclusive(path);
            decisions.Add(new EvidenceRetentionDecision(
                EvidenceArtifactFamily.Prompts,
                deletion.Success ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.DeferredLocked,
                path,
                goal.GoalId,
                EvidenceOwnerResolution.UniqueTerminal,
                deletion.Success ? "past-deletion-age" : "exclusive-delete-failed",
                BytesAttempted: length,
                BytesReclaimed: deletion.Success ? length : 0,
                FailureExceptionType: deletion.ExceptionType));
            if (deletion.Success)
            {
                deleted++;
            }
        }

        return deleted;
    }

    private static void RecordGoalEventPreservation(
        string orchestratorDirectory,
        IReadOnlyCollection<StorageRetentionGoal> goals,
        List<EvidenceRetentionDecision> decisions)
    {
        var root = Path.Combine(orchestratorDirectory, "goal-events");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var goalId = Path.GetFileNameWithoutExtension(path);
            var owners = goals.Where(goal => goal.GoalId.Equals(goalId, StringComparison.OrdinalIgnoreCase)).ToArray();
            decisions.Add(new EvidenceRetentionDecision(
                EvidenceArtifactFamily.GoalEvents,
                owners.Length == 1 ? EvidenceRetentionAction.Preserved : EvidenceRetentionAction.RetainedUndecidable,
                path,
                owners.Length == 1 ? owners[0].GoalId : null,
                owners.Length == 1
                    ? owners[0].IsTerminal ? EvidenceOwnerResolution.UniqueTerminal : EvidenceOwnerResolution.NonTerminal
                    : EvidenceOwnerResolution.Unmatched,
                owners.Length == 1 ? "replay-contract-requires-raw-jsonl" : "goal-event-owner-unmatched"));
        }
    }

    private static int ArchiveGoalJournals(
        string executionDirectory,
        IReadOnlyCollection<StorageRetentionGoal> goals,
        List<EvidenceRetentionDecision> decisions)
    {
        var archived = 0;
        foreach (var goal in goals.Where(goal => goal.IsTerminal))
        {
            var source = GoalOperationJournal.PathFor(executionDirectory, new GoalId(goal.GoalId));
            if (!File.Exists(source))
            {
                continue;
            }

            var journal = GoalOperationJournal.Read(executionDirectory, new GoalId(goal.GoalId));
            if (!GoalOperationJournal.HasRetiredTerminalDisposition(journal))
            {
                continue;
            }

            var destination = GoalOperationJournal.ArchivePathFor(executionDirectory, new GoalId(goal.GoalId));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    destination = Path.Combine(
                        Path.GetDirectoryName(destination)!,
                        $"{goal.GoalId}-{Guid.NewGuid():N}.jsonl");
                }
                File.Move(source, destination);
                archived++;
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.GoalOperationJournals,
                    EvidenceRetentionAction.Preserved,
                    destination,
                    goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    "retired-journal-archived"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.GoalOperationJournals,
                    EvidenceRetentionAction.DeferredLocked,
                    source,
                    goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    "journal-archive-failed",
                    FailureExceptionType: ex.GetType().Name));
            }
        }

        return archived;
    }

    private static (bool Success, string? ExceptionType) TryDeleteExclusive(string path)
    {
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
            }
            File.Delete(path);
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, ex.GetType().Name);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private sealed class MutexLease : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _ownsMutex;

        private MutexLease(Mutex mutex, bool ownsMutex)
        {
            _mutex = mutex;
            _ownsMutex = ownsMutex;
        }

        public static MutexLease Acquire(string name)
        {
            var mutex = new Mutex(false, name);
            try
            {
                try
                {
                    mutex.WaitOne();
                }
                catch (AbandonedMutexException)
                {
                }

                return new MutexLease(mutex, ownsMutex: true);
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }

        public static MutexLease? TryAcquire(string name)
        {
            var mutex = new Mutex(false, name);
            try
            {
                var ownsMutex = false;
                try
                {
                    ownsMutex = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (!ownsMutex)
                {
                    mutex.Dispose();
                    return null;
                }

                return new MutexLease(mutex, ownsMutex: true);
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_ownsMutex)
            {
                _mutex.ReleaseMutex();
                _ownsMutex = false;
            }

            _mutex.Dispose();
        }
    }
}
