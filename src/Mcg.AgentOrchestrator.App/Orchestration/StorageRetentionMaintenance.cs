using System.Diagnostics;
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
    IReadOnlyCollection<string>? PromptPaths = null,
    bool IsStoreStandIn = false)
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
    IReadOnlyList<EvidenceRetentionDecision> Decisions,
    TimeSpan Duration = default)
{
    public bool Failed => Decisions.Any(decision => decision.Action == EvidenceRetentionAction.Failed);
}

internal static partial class StorageRetentionMaintenance
{
    internal static readonly TimeSpan WorkerCompressionAge = TimeSpan.FromDays(1);
    internal static readonly TimeSpan WorkerDeletionAge = TimeSpan.FromDays(14);
    internal static readonly TimeSpan AcceptanceArtifactMaxAge = TimeSpan.FromDays(14);
    internal static readonly TimeSpan MtpResultMaxAge = TimeSpan.FromDays(14);
    internal static readonly TimeSpan MtpUnattributedMaxAge = TimeSpan.FromHours(48);
    internal static readonly TimeSpan MtpUnattributedMinAge = TimeSpan.FromMinutes(30);
    internal const long AcceptanceArtifactMaxBytesPerGoal = 256L * 1024 * 1024;
    internal const long MtpUnattributedMaxBytes = 2L * 1024 * 1024 * 1024;
    internal const int MtpResultMaxRetainedDirectories = 100;
    internal const int MtpUnattributedReclaimsPerSweep = 400;

    [GeneratedRegex("^(?<dispatch>(?<goal>[0-9a-f]{8})-(?<task>[0-9a-f]{8})-[0-9]{14})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DispatchArtifactNameRegex();

    [GeneratedRegex("^p[0-9A-Fa-f]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MtpProcessRootNameRegex();

    public static StorageRetentionResult Run(
        OrchestratorWorkspace workspace,
        IEnumerable<Goal> goals,
        DateTimeOffset now)
    {
        var retentionGoals = goals.Select(goal => new StorageRetentionGoal(
            goal.Id.Value,
            goal.Status,
            goal.Tasks.ToDictionary(task => task.Id.Value, task => task.Status, StringComparer.OrdinalIgnoreCase),
            goal.Tasks
                .Select(task => task.LastDispatch?.PromptPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()))
            .ToArray();
        return Run(
            workspace.LogDirectory,
            workspace.OrchestratorDirectory,
            workspace.ExecutionDirectory,
            retentionGoals,
            now,
            mtpResultsRoot: DefaultMtpResultsRoot());
    }

    internal static IReadOnlyCollection<StorageRetentionGoal> LoadPersistedGoals(
        ITransactionalOrchestratorStateRepository stateRepository)
    {
        GoalId[] goalIds;
        try
        {
            goalIds = stateRepository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult()
                .Select(summary => new GoalId(summary.Id)).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
        var goals = new List<StorageRetentionGoal>(goalIds.Length);
        foreach (var goalId in goalIds)
        {
            GoalSnapshot? snapshot;
            try { snapshot = stateRepository.LoadGoalAsync(goalId).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                snapshot = null;
            }
            if (snapshot is not null)
            {
                goals.Add(StorageRetentionGoal.FromSnapshot(snapshot));
            }
            else
            {
                goals.Add(new StorageRetentionGoal(goalId.Value, GoalStatus.Active,
                    new Dictionary<string, WorkTaskStatus>(), IsStoreStandIn: true));
            }
        }

        return goals;
    }

    internal static IReadOnlyCollection<string> SelectGoalOperationPrunableIds(
        IEnumerable<StorageRetentionGoal> goals) =>
        goals
            .Where(goal => goal.Status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded)
            .Select(goal => goal.GoalId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static StorageRetentionResult Run(
        string logDirectory,
        string orchestratorDirectory,
        string executionDirectory,
        IReadOnlyCollection<StorageRetentionGoal> goals,
        DateTimeOffset now,
        Action? beforeGoalJournalArchiveForTests = null,
        string? mtpResultsRoot = null,
        long? acceptanceArtifactMaxBytesForTests = null,
        Action<string>? beforeAttemptCandidateDeletionForTests = null,
        Action<string>? beforeMtpCandidateDeletionForTests = null,
        long? mtpUnattributedMaxBytesForTests = null,
        int? mtpUnattributedReclaimsPerSweepForTests = null,
        Func<int, bool>? mtpProcessHasExitedForTests = null,
        StorageRetentionReclaimOptions? reclaimOptions = null)
    {
        var decisions = new List<EvidenceRetentionDecision>();
        var sweepId = Guid.NewGuid().ToString("N");
        var startedTimestamp = Stopwatch.GetTimestamp();
        var reclaim = reclaimOptions ?? StorageRetentionReclaimOptions.Default;
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
                return new StorageRetentionResult(
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    sweepId,
                    decisions,
                    Stopwatch.GetElapsedTime(startedTimestamp));
            }

            try
            {
                if (!goals.Any(goal => !goal.IsStoreStandIn))
                {
                    decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.RunEvents,
                        EvidenceRetentionAction.Failed, orchestratorDirectory, null,
                        EvidenceOwnerResolution.Unrecorded, "goal-store-unavailable-goal-rules-skipped"));
                }
                SweepWorkerArtifacts(logDirectory, goals, now, decisions);
                SweepOperatorLogs(logDirectory, now, reclaim, decisions);
                var retainedMtpAttemptOwnerKeys = SweepMtpResults(
                    mtpResultsRoot,
                    orchestratorDirectory,
                    goals,
                    now,
                    beforeMtpCandidateDeletionForTests,
                    mtpUnattributedMaxBytesForTests ?? MtpUnattributedMaxBytes,
                    mtpUnattributedReclaimsPerSweepForTests ?? MtpUnattributedReclaimsPerSweep,
                    mtpProcessHasExitedForTests,
                    decisions);
                SweepAcceptanceArtifacts(
                    orchestratorDirectory,
                    goals,
                    now,
                    sweepId,
                    acceptanceArtifactMaxBytesForTests ?? AcceptanceArtifactMaxBytesPerGoal,
                    retainedMtpAttemptOwnerKeys,
                    beforeAttemptCandidateDeletionForTests,
                    reclaim,
                    decisions);
                SweepPrompts(orchestratorDirectory, goals, now, decisions);
                RecordGoalEventPreservation(orchestratorDirectory, goals, decisions);
                beforeGoalJournalArchiveForTests?.Invoke();
                ArchiveGoalJournals(executionDirectory, goals, decisions);
                CompressTerminalGoalJournals(executionDirectory, goals, now, reclaim, decisions);
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

            return BuildResult(sweepId, decisions) with
            {
                Duration = Stopwatch.GetElapsedTime(startedTimestamp)
            };
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

    internal static string DefaultMtpResultsRoot()
    {
        var localApplicationData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        return Path.Combine(localApplicationData, "Temp", "Low", "mcg-tests");
    }

    internal static IDisposable AcquireAttemptWriterLease(
        string goalDirectory,
        TimeSpan? timeout = null,
        Action<string>? receipt = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var lease = TryAcquireAttemptWriterLease(goalDirectory, effectiveTimeout);
        if (lease is not null)
        {
            return lease;
        }

        var detail =
            $"ACCEPTANCE_ARTIFACT_LEASE_TIMEOUT goal_directory=\"{goalDirectory}\" timeout_ms={effectiveTimeout.TotalMilliseconds:F0}";
        receipt?.Invoke(detail);
        Console.Error.WriteLine(detail);
        throw new TimeoutException(detail);
    }

    internal static IDisposable? TryAcquireAttemptWriterLease(
        string goalDirectory,
        TimeSpan? timeout = null) =>
        MutexLease.TryAcquire(AttemptLeaseNameFor(goalDirectory), timeout ?? TimeSpan.Zero);

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
                EvidenceRetentionAction.Preserved,
                legacyDiagnostics,
                null,
                EvidenceOwnerResolution.Unrecorded,
                "global-diagnostics-preserved-by-policy"));
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
                    "non-terminal-evidence-unbounded-by-policy"));
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
        string sweepId,
        long acceptanceArtifactMaxBytes,
        IReadOnlySet<string> retainedMtpAttemptOwnerKeys,
        Action<string>? beforeAttemptCandidateDeletionForTests,
        StorageRetentionReclaimOptions reclaim,
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
                sweepId,
                acceptanceArtifactMaxBytes,
                retainedMtpAttemptOwnerKeys,
                beforeAttemptCandidateDeletionForTests,
                reclaim,
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
        string sweepId,
        long acceptanceArtifactMaxBytes,
        IReadOnlySet<string> retainedMtpAttemptOwnerKeys,
        Action<string>? beforeAttemptCandidateDeletionForTests,
        StorageRetentionReclaimOptions reclaim,
        List<EvidenceRetentionDecision> decisions)
    {
        if (!Directory.Exists(root))
        {
            return (0, 0);
        }

        var receipts = 0;
        var deleted = 0;
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

            var candidates = new List<(
                FileInfo File,
                RetentionAttemptIdentity? Owner,
                EvidenceOwnershipSource OwnershipSource,
                string GoalId,
                bool RequireOwner)>();
            var goalId = Path.GetFileName(goalDirectory);
            if (goalId.Equals("operator", StringComparison.OrdinalIgnoreCase))
            {
                deleted += SweepOperatorAttemptEntries(goalDirectory, family, now, reclaim, decisions);
                continue;
            }
            var owners = goals.Where(goal => goal.GoalId.Equals(goalId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (owners.Length != 1)
            {
                if (owners.Length == 0 && goals.Any(goal => !goal.IsStoreStandIn))
                {
                    deleted += TryReclaimUnknownGoalDirectory(goalDirectory, family, now, reclaim, decisions);
                    continue;
                }
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
            if (goal.IsStoreStandIn)
            {
                decisions.Add(new EvidenceRetentionDecision(family, EvidenceRetentionAction.RetainedUndecidable,
                    goalDirectory, goal.GoalId, EvidenceOwnerResolution.Unrecorded, "goal-snapshot-unavailable"));
                continue;
            }
            if (!goal.IsTerminal)
            {
                decisions.Add(new EvidenceRetentionDecision(
                    family,
                    EvidenceRetentionAction.Preserved,
                    goalDirectory,
                    goal.GoalId,
                    EvidenceOwnerResolution.NonTerminal,
                    "non-terminal-evidence-unbounded-by-policy"));
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

            if (IsReclaimableTerminalStatus(goal.Status))
            {
                deleted += ReclaimTerminalGoalAttempts(goalDirectory, family, goal, attempts, now, reclaim, decisions);
                if (!Directory.Exists(goalDirectory))
                {
                    continue;
                }
                if (!TryReadAttemptIdentities(goalDirectory, goal.GoalId, out attempts, out invalidReason))
                {
                    decisions.Add(new EvidenceRetentionDecision(family, EvidenceRetentionAction.RetainedUndecidable,
                        goalDirectory, goal.GoalId, EvidenceOwnerResolution.UniqueTerminal, invalidReason));
                    continue;
                }
            }

            var protectedAttemptIds = EvidenceRetentionPolicy.ProtectedAttemptIds(attempts);
            var countBoundAttemptIds = EvidenceRetentionPolicy.AttemptIdsPastCountBound(
                attempts,
                ConductorParallelAcceptanceAttemptCoordinator.RetainedAttemptCountPerGoal,
                protectedAttemptIds);
            var retainedTrxAttemptIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var trxPath in Directory.EnumerateFiles(goalDirectory, "*.trx", SearchOption.AllDirectories))
            {
                var owner = EvidenceRetentionPolicy.ResolveOwner(attempts, trxPath);
                if (owner.Attempt is null && !owner.Ambiguous)
                {
                    owner = EvidenceRetentionPolicy.ResolveLegacyOwner(attempts, trxPath);
                }

                if (owner.Attempt is not null && protectedAttemptIds.Contains(owner.Attempt.AttemptId))
                {
                    var identity = owner.Attempt;
                    decisions.Add(new EvidenceRetentionDecision(
                        family,
                        EvidenceRetentionAction.Preserved,
                        trxPath,
                        goal.GoalId,
                        EvidenceOwnerResolution.UniqueTerminal,
                        identity.Failed ? "last-failing-attempt" : "final-attempt",
                        identity.AttemptId,
                        identity.Ordinal,
                        OwnershipSource: owner.Source));
                    continue;
                }

                var attempt = owner.Attempt;
                if (attempt is null)
                {
                    decisions.Add(new EvidenceRetentionDecision(
                        family,
                        EvidenceRetentionAction.RetainedUndecidable,
                        trxPath,
                        goal.GoalId,
                        EvidenceOwnerResolution.UniqueTerminal,
                        owner.Ambiguous ? "trx-attempt-ownership-ambiguous" : "trx-attempt-ownership-unresolved",
                        OwnershipSource: owner.Source));
                    continue;
                }

                if (!TryWriteTrxReceipt(
                    trxPath,
                    sweepId,
                    goal.GoalId,
                    attempt.AttemptId,
                    now))
                {
                    retainedTrxAttemptIds.Add(attempt.AttemptId);
                    decisions.Add(new EvidenceRetentionDecision(
                        family,
                        EvidenceRetentionAction.Preserved,
                        trxPath,
                        goal.GoalId,
                        EvidenceOwnerResolution.UniqueTerminal,
                        "trx-is-unreadable-or-has-no-test-identities"));
                    continue;
                }

                receipts++;
                decisions.Add(new EvidenceRetentionDecision(
                    family,
                    EvidenceRetentionAction.ReceiptWritten,
                    trxPath + ".test-identities.json",
                    goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    "trx-identities-preserved",
                    attempt.AttemptId,
                    attempt.Ordinal,
                    OwnershipSource: owner.Source));
                var length = SafeLength(trxPath);
                var deletion = TryDeleteExclusive(trxPath);
                decisions.Add(new EvidenceRetentionDecision(
                    family,
                    deletion.Success ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.DeferredLocked,
                    trxPath,
                    goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    deletion.Success ? "trx-reduced-to-receipt" : "exclusive-delete-failed",
                    attempt.AttemptId,
                    attempt.Ordinal,
                    BytesAttempted: length,
                    BytesReclaimed: deletion.Success ? length : 0,
                    FailureExceptionType: deletion.ExceptionType,
                    OwnershipSource: owner.Source));
                if (deletion.Success)
                {
                    deleted++;
                }
                else
                {
                    retainedTrxAttemptIds.Add(attempt.AttemptId);
                }
            }

            foreach (var path in Directory.EnumerateFiles(goalDirectory, "*", SearchOption.AllDirectories))
            {
                var owner = EvidenceRetentionPolicy.ResolveOwner(attempts, path);
                if (owner.Attempt is not null && protectedAttemptIds.Contains(owner.Attempt.AttemptId))
                {
                    var identity = owner.Attempt;
                    decisions.Add(new EvidenceRetentionDecision(
                        family,
                        EvidenceRetentionAction.Preserved,
                        path,
                        goal.GoalId,
                        EvidenceOwnerResolution.UniqueTerminal,
                        identity.Failed ? "last-failing-attempt" : "final-attempt",
                        identity.AttemptId,
                        identity.Ordinal,
                        OwnershipSource: owner.Source));
                    continue;
                }

                if (IsAcceptanceSequenceIndex(path))
                {
                    decisions.Add(new EvidenceRetentionDecision(
                        family,
                        EvidenceRetentionAction.Preserved,
                        path,
                        goal.GoalId,
                        EvidenceOwnerResolution.UniqueTerminal,
                        "authoritative-ownership-index"));
                    continue;
                }

                if (path.EndsWith(".attempt.json", StringComparison.OrdinalIgnoreCase))
                {
                    var identity = owner.Attempt;
                    if (identity is not null &&
                        (retainedTrxAttemptIds.Contains(identity.AttemptId) ||
                         retainedMtpAttemptOwnerKeys.Contains(AttemptOwnerKey(goal.GoalId, identity.AttemptId))))
                    {
                        decisions.Add(new EvidenceRetentionDecision(
                            family,
                            EvidenceRetentionAction.Preserved,
                            path,
                            goal.GoalId,
                            EvidenceOwnerResolution.UniqueTerminal,
                            "retained-test-artifact-owner-metadata",
                            identity.AttemptId,
                            identity.Ordinal,
                            OwnershipSource: owner.Source));
                        continue;
                    }
                }

                if (!path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".test-identities.json", StringComparison.OrdinalIgnoreCase))
                {
                    if (owner.Attempt is null)
                    {
                        var legacyOwner = EvidenceRetentionPolicy.ResolveLegacyShadowOwner(attempts, path);
                        var legacyUnattributed = !owner.Ambiguous &&
                            legacyOwner.Attempt is null &&
                            !legacyOwner.Ambiguous;
                        if (!legacyUnattributed)
                        {
                            decisions.Add(new EvidenceRetentionDecision(
                                family,
                                EvidenceRetentionAction.RetainedUndecidable,
                                path,
                                goal.GoalId,
                                EvidenceOwnerResolution.UniqueTerminal,
                                owner.Ambiguous ? "artifact-owner-ambiguous" : "artifact-owner-unresolved",
                                OwnershipSource: owner.Source));
                            continue;
                        }
                    }

                    candidates.Add((
                        new FileInfo(path),
                        owner.Attempt,
                        owner.Source,
                        goal.GoalId,
                        RequireOwner: owner.Attempt is not null));
                }
            }

            var totalCandidateBytes = candidates.Sum(candidate => SafeLength(candidate.File.FullName));
            var expectedFactRevision = EvidenceRetentionPolicy.ComputeFactRevision(
                attempts,
                Directory.EnumerateFiles(goalDirectory, "*", SearchOption.AllDirectories));
            beforeAttemptCandidateDeletionForTests?.Invoke(goalDirectory);
            foreach (var candidate in candidates
                .OrderBy(item => item.File.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                    item.File.Name.EndsWith(".attempt.json", StringComparison.OrdinalIgnoreCase))
                .ThenBy(item => item.File.LastWriteTimeUtc)
                .ThenBy(item => item.File.FullName, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryReadAttemptIdentities(goalDirectory, goal.GoalId, out var refreshedAttempts, out var refreshReason))
                {
                    decisions.Add(new EvidenceRetentionDecision(
                        family,
                        EvidenceRetentionAction.RetainedUndecidable,
                        candidate.File.FullName,
                        candidate.GoalId,
                        EvidenceOwnerResolution.UniqueTerminal,
                        refreshReason,
                        candidate.Owner?.AttemptId,
                        candidate.Owner?.Ordinal,
                        OwnershipSource: candidate.OwnershipSource,
                        FactRevision: expectedFactRevision));
                    continue;
                }

                if (refreshedAttempts.Any(attempt => !attempt.Reconciled))
                {
                    decisions.Add(new EvidenceRetentionDecision(
                        family,
                        EvidenceRetentionAction.DeferredLive,
                        candidate.File.FullName,
                        candidate.GoalId,
                        EvidenceOwnerResolution.UniqueTerminal,
                        "attempt-became-unreconciled-before-effect",
                        candidate.Owner?.AttemptId,
                        candidate.Owner?.Ordinal,
                        OwnershipSource: candidate.OwnershipSource,
                        FactRevision: expectedFactRevision));
                    continue;
                }

                var observedFactRevision = EvidenceRetentionPolicy.ComputeFactRevision(
                    refreshedAttempts,
                    Directory.EnumerateFiles(goalDirectory, "*", SearchOption.AllDirectories));
                if (!expectedFactRevision.Equals(observedFactRevision, StringComparison.Ordinal))
                {
                    decisions.Add(new EvidenceRetentionDecision(
                        family,
                        EvidenceRetentionAction.RetainedUndecidable,
                        candidate.File.FullName,
                        candidate.GoalId,
                        EvidenceOwnerResolution.UniqueTerminal,
                        "retention-facts-changed-before-effect",
                        candidate.Owner?.AttemptId,
                        candidate.Owner?.Ordinal,
                        OwnershipSource: candidate.OwnershipSource,
                        FactRevision: observedFactRevision));
                    continue;
                }

                if (!IsPathContainedBy(goalDirectory, candidate.File.FullName))
                {
                    decisions.Add(new EvidenceRetentionDecision(
                        family,
                        EvidenceRetentionAction.RetainedUndecidable,
                        candidate.File.FullName,
                        candidate.GoalId,
                        EvidenceOwnerResolution.UniqueTerminal,
                        "candidate-path-left-goal-directory",
                        OwnershipSource: candidate.OwnershipSource,
                        FactRevision: observedFactRevision));
                    continue;
                }

                var refreshedOwner = EvidenceRetentionPolicy.ResolveOwner(refreshedAttempts, candidate.File.FullName);
                var refreshedLegacyOwner = EvidenceRetentionPolicy.ResolveLegacyShadowOwner(
                    refreshedAttempts,
                    candidate.File.FullName);
                var ownerChanged = candidate.Owner is null
                    ? refreshedOwner.Attempt is not null || refreshedOwner.Ambiguous ||
                      refreshedLegacyOwner.Attempt is not null || refreshedLegacyOwner.Ambiguous
                    : refreshedOwner.Attempt is null ||
                      !refreshedOwner.Attempt.AttemptId.Equals(candidate.Owner.AttemptId, StringComparison.OrdinalIgnoreCase);
                if (ownerChanged)
                {
                    decisions.Add(new EvidenceRetentionDecision(
                        family,
                        EvidenceRetentionAction.RetainedUndecidable,
                        candidate.File.FullName,
                        candidate.GoalId,
                        EvidenceOwnerResolution.UniqueTerminal,
                        "artifact-owner-changed-before-effect",
                        OwnershipSource: refreshedOwner.Source,
                        FactRevision: observedFactRevision));
                    continue;
                }

                var refreshedProtectedAttemptIds = EvidenceRetentionPolicy.ProtectedAttemptIds(refreshedAttempts);
                var refreshedCountBoundAttemptIds = EvidenceRetentionPolicy.AttemptIdsPastCountBound(
                    refreshedAttempts,
                    ConductorParallelAcceptanceAttemptCoordinator.RetainedAttemptCountPerGoal,
                    refreshedProtectedAttemptIds);
                var age = now - candidate.File.LastWriteTimeUtc;
                var aged = age > AcceptanceArtifactMaxAge;
                var byteBoundEligible =
                    totalCandidateBytes > acceptanceArtifactMaxBytes &&
                    age > WorkerCompressionAge;
                var referencedArtifact =
                    candidate.File.Name.EndsWith(".attempt.json", StringComparison.OrdinalIgnoreCase) &&
                    refreshedOwner.Attempt is not null &&
                    (retainedTrxAttemptIds.Contains(refreshedOwner.Attempt.AttemptId) ||
                     retainedMtpAttemptOwnerKeys.Contains(AttemptOwnerKey(goal.GoalId, refreshedOwner.Attempt.AttemptId)));
                var eligibilityFacts = new EvidenceRetentionFacts(
                    TerminalGoal: true,
                    refreshedOwner.Attempt,
                    refreshedOwner.Source,
                    refreshedOwner.Attempt is not null &&
                        refreshedProtectedAttemptIds.Contains(refreshedOwner.Attempt.AttemptId),
                    referencedArtifact,
                    refreshedOwner.Attempt is not null &&
                        refreshedCountBoundAttemptIds.Contains(refreshedOwner.Attempt.AttemptId),
                    aged,
                    byteBoundEligible,
                    observedFactRevision,
                    candidate.RequireOwner);
                var eligibility = EvidenceRetentionPolicy.EvaluatePath(eligibilityFacts);
                if (eligibility.Disposition != EvidenceEligibility.DeleteWhenSafe)
                {
                    decisions.Add(new EvidenceRetentionDecision(
                        family,
                        EvidenceRetentionAction.Preserved,
                        candidate.File.FullName,
                        candidate.GoalId,
                        EvidenceOwnerResolution.UniqueTerminal,
                        totalCandidateBytes > acceptanceArtifactMaxBytes &&
                            eligibility.Reason == "within-retention-bounds"
                                ? "byte-bound-unsatisfiable-fresh-evidence"
                                : eligibility.Reason,
                        refreshedOwner.Attempt?.AttemptId,
                        refreshedOwner.Attempt?.Ordinal,
                        OwnershipSource: refreshedOwner.Source,
                        FactRevision: observedFactRevision,
                        Eligibility: eligibility.Disposition));
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
                    deletion.Success ? eligibility.Reason : "exclusive-delete-failed",
                    refreshedOwner.Attempt?.AttemptId,
                    refreshedOwner.Attempt?.Ordinal,
                    BytesAttempted: length,
                    BytesReclaimed: deletion.Success ? length : 0,
                    FailureExceptionType: deletion.ExceptionType,
                    OwnershipSource: refreshedOwner.Source,
                    FactRevision: observedFactRevision,
                    Eligibility: eligibility.Disposition));
                if (deletion.Success)
                {
                    deleted++;
                    totalCandidateBytes = Math.Max(0, totalCandidateBytes - length);
                    if (!TryReadAttemptIdentities(
                        goalDirectory,
                        goal.GoalId,
                        out var postEffectAttempts,
                        out var postEffectReason))
                    {
                        decisions.Add(new EvidenceRetentionDecision(
                            family,
                            EvidenceRetentionAction.RetainedUndecidable,
                            goalDirectory,
                            goal.GoalId,
                            EvidenceOwnerResolution.UniqueTerminal,
                            $"post-effect-fact-refresh-failed:{postEffectReason}",
                            FactRevision: expectedFactRevision));
                        break;
                    }

                    if (postEffectAttempts.Any(attempt => !attempt.Reconciled))
                    {
                        decisions.Add(new EvidenceRetentionDecision(
                            family,
                            EvidenceRetentionAction.DeferredLive,
                            goalDirectory,
                            goal.GoalId,
                            EvidenceOwnerResolution.UniqueTerminal,
                            "attempt-became-unreconciled-after-effect",
                            FactRevision: expectedFactRevision));
                        break;
                    }

                    expectedFactRevision = EvidenceRetentionPolicy.ComputeFactRevision(
                        postEffectAttempts,
                        Directory.EnumerateFiles(goalDirectory, "*", SearchOption.AllDirectories));
                }
            }
        }

        return (receipts, deleted);
    }

    private static IReadOnlySet<string> SweepMtpResults(
        string? resultsRoot,
        string orchestratorDirectory,
        IReadOnlyCollection<StorageRetentionGoal> goals,
        DateTimeOffset now,
        Action<string>? beforeCandidateDeletionForTests,
        long unattributedMaxBytes,
        int unattributedReclaimsPerSweep,
        Func<int, bool>? processHasExitedForTests,
        List<EvidenceRetentionDecision> decisions)
    {
        var retainedAttemptOwnerKeys = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(resultsRoot) || !Directory.Exists(resultsRoot))
        {
            return retainedAttemptOwnerKeys;
        }

        var attemptOwners = BuildMtpAttemptOwnerIndex(orchestratorDirectory, goals);
        var candidates = new List<MtpRetentionCandidate>();
        var unattributed = new List<MtpUnattributedCandidate>();
        foreach (var directory in Directory.EnumerateDirectories(resultsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (MtpProcessRootNameRegex().IsMatch(Path.GetFileName(directory)))
            {
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.MtpTestRuns,
                    EvidenceRetentionAction.Preserved,
                    directory,
                    null,
                    EvidenceOwnerResolution.Unrecorded,
                    "mtp-process-temp-root-owned-by-reaper"));
                continue;
            }

            if (!TryReadMtpRunOwnership(directory, out var ownership, out var invalidReason))
            {
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.MtpTestRuns,
                    EvidenceRetentionAction.RetainedUndecidable,
                    directory,
                    null,
                    EvidenceOwnerResolution.Unrecorded,
                    invalidReason));
                continue;
            }

            if (!ownership.IsAttributed)
            {
                unattributed.Add(new MtpUnattributedCandidate(
                    directory,
                    ownership.CreatedAt,
                    ownership.OwnerProcessId));
                continue;
            }

            if (!attemptOwners.TryGetValue(ownership.AttemptId, out var owners) || owners.Count != 1)
            {
                if (owners is not null)
                {
                    foreach (var ambiguousOwner in owners)
                    {
                        retainedAttemptOwnerKeys.Add(AttemptOwnerKey(
                            ambiguousOwner.Goal.GoalId,
                            ambiguousOwner.Attempt.AttemptId));
                    }
                }
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.MtpTestRuns,
                    EvidenceRetentionAction.RetainedUndecidable,
                    directory,
                    null,
                    owners is null ? EvidenceOwnerResolution.Unmatched : EvidenceOwnerResolution.AmbiguousPrefix,
                    owners is null ? "mtp-attempt-owner-not-recorded" : "mtp-attempt-owner-is-ambiguous",
                    ownership.AttemptId));
                continue;
            }

            var owner = owners[0];
            if (ownership.CreatedAt > now.AddMinutes(5))
            {
                retainedAttemptOwnerKeys.Add(AttemptOwnerKey(owner.Goal.GoalId, owner.Attempt.AttemptId));
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.MtpTestRuns,
                    EvidenceRetentionAction.RetainedUndecidable,
                    directory,
                    owner.Goal.GoalId,
                    owner.Goal.IsTerminal ? EvidenceOwnerResolution.UniqueTerminal : EvidenceOwnerResolution.NonTerminal,
                    "mtp-created-at-is-in-the-future",
                    ownership.AttemptId,
                    owner.Attempt.Ordinal));
                continue;
            }

            if (!owner.Goal.IsTerminal)
            {
                retainedAttemptOwnerKeys.Add(AttemptOwnerKey(owner.Goal.GoalId, owner.Attempt.AttemptId));
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.MtpTestRuns,
                    EvidenceRetentionAction.Preserved,
                    directory,
                    owner.Goal.GoalId,
                    EvidenceOwnerResolution.NonTerminal,
                    "non-terminal-evidence-unbounded-by-policy",
                    ownership.AttemptId,
                    owner.Attempt.Ordinal));
                continue;
            }

            if (!owner.Attempt.Reconciled)
            {
                retainedAttemptOwnerKeys.Add(AttemptOwnerKey(owner.Goal.GoalId, owner.Attempt.AttemptId));
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.MtpTestRuns,
                    EvidenceRetentionAction.DeferredLive,
                    directory,
                    owner.Goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    "attempt-is-unreconciled",
                    ownership.AttemptId,
                    owner.Attempt.Ordinal));
                continue;
            }

            if (owner.ProtectedAttemptIds.Contains(ownership.AttemptId))
            {
                retainedAttemptOwnerKeys.Add(AttemptOwnerKey(owner.Goal.GoalId, owner.Attempt.AttemptId));
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.MtpTestRuns,
                    EvidenceRetentionAction.Preserved,
                    directory,
                    owner.Goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    owner.Attempt.Failed ? "last-failing-attempt" : "final-attempt",
                    ownership.AttemptId,
                    owner.Attempt.Ordinal));
                continue;
            }

            candidates.Add(new MtpRetentionCandidate(directory, ownership.CreatedAt, owner));
        }

        var ordered = candidates
            .OrderByDescending(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var candidate = ordered[index];
            var aged = now - candidate.CreatedAt >= MtpResultMaxAge;
            var countBound = index >= MtpResultMaxRetainedDirectories;
            if (!aged && !countBound)
            {
                retainedAttemptOwnerKeys.Add(AttemptOwnerKey(
                    candidate.Owner.Goal.GoalId,
                    candidate.Owner.Attempt.AttemptId));
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.MtpTestRuns,
                    EvidenceRetentionAction.Preserved,
                    candidate.Path,
                    candidate.Owner.Goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    "inside-age-and-count-bounds",
                    candidate.Owner.Attempt.AttemptId,
                    candidate.Owner.Attempt.Ordinal));
                continue;
            }

            beforeCandidateDeletionForTests?.Invoke(candidate.Path);
            using var attemptLease = MutexLease.TryAcquire(AttemptLeaseNameFor(candidate.Owner.GoalDirectory));
            if (attemptLease is null)
            {
                retainedAttemptOwnerKeys.Add(AttemptOwnerKey(
                    candidate.Owner.Goal.GoalId,
                    candidate.Owner.Attempt.AttemptId));
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.MtpTestRuns,
                    EvidenceRetentionAction.DeferredLease,
                    candidate.Path,
                    candidate.Owner.Goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    "attempt-writer-lease-unavailable",
                    candidate.Owner.Attempt.AttemptId,
                    candidate.Owner.Attempt.Ordinal));
                continue;
            }

            if (!TryRefreshMtpAttemptOwner(candidate.Owner, out var refreshedOwner, out var refreshReason))
            {
                retainedAttemptOwnerKeys.Add(AttemptOwnerKey(
                    candidate.Owner.Goal.GoalId,
                    candidate.Owner.Attempt.AttemptId));
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.MtpTestRuns,
                    EvidenceRetentionAction.RetainedUndecidable,
                    candidate.Path,
                    candidate.Owner.Goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    refreshReason,
                    candidate.Owner.Attempt.AttemptId,
                    candidate.Owner.Attempt.Ordinal));
                continue;
            }

            if (!refreshedOwner.Attempt.Reconciled)
            {
                retainedAttemptOwnerKeys.Add(AttemptOwnerKey(
                    refreshedOwner.Goal.GoalId,
                    refreshedOwner.Attempt.AttemptId));
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.MtpTestRuns,
                    EvidenceRetentionAction.DeferredLive,
                    candidate.Path,
                    refreshedOwner.Goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    "attempt-became-unreconciled",
                    refreshedOwner.Attempt.AttemptId,
                    refreshedOwner.Attempt.Ordinal));
                continue;
            }

            if (refreshedOwner.ProtectedAttemptIds.Contains(refreshedOwner.Attempt.AttemptId))
            {
                retainedAttemptOwnerKeys.Add(AttemptOwnerKey(
                    refreshedOwner.Goal.GoalId,
                    refreshedOwner.Attempt.AttemptId));
                decisions.Add(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.MtpTestRuns,
                    EvidenceRetentionAction.Preserved,
                    candidate.Path,
                    refreshedOwner.Goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal,
                    refreshedOwner.Attempt.Failed ? "last-failing-attempt" : "final-attempt",
                    refreshedOwner.Attempt.AttemptId,
                    refreshedOwner.Attempt.Ordinal));
                continue;
            }

            var deletion = TryDeleteDirectory(candidate.Path);
            if (!deletion.Success)
            {
                retainedAttemptOwnerKeys.Add(AttemptOwnerKey(
                    refreshedOwner.Goal.GoalId,
                    refreshedOwner.Attempt.AttemptId));
            }
            decisions.Add(new EvidenceRetentionDecision(
                EvidenceArtifactFamily.MtpTestRuns,
                deletion.Success ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.DeferredLocked,
                candidate.Path,
                refreshedOwner.Goal.GoalId,
                EvidenceOwnerResolution.UniqueTerminal,
                deletion.Success
                    ? countBound ? "past-count-bound" : "past-age-bound"
                    : "exclusive-delete-failed",
                refreshedOwner.Attempt.AttemptId,
                refreshedOwner.Attempt.Ordinal,
                deletion.BytesAttempted,
                deletion.Success ? deletion.BytesAttempted : 0,
                deletion.ExceptionType));
        }

        SweepUnattributedMtpRuns(
            unattributed,
            now,
            Math.Max(0, unattributedMaxBytes),
            Math.Max(0, unattributedReclaimsPerSweep),
            processHasExitedForTests,
            decisions);

        return retainedAttemptOwnerKeys;
    }

    private static void SweepUnattributedMtpRuns(
        IReadOnlyCollection<MtpUnattributedCandidate> candidates,
        DateTimeOffset now,
        long maxBytes,
        int maxReclaims,
        Func<int, bool>? processHasExitedForTests,
        List<EvidenceRetentionDecision> decisions)
    {
        var measurable = new List<MtpUnattributedMeasuredCandidate>(candidates.Count);
        foreach (var candidate in candidates
                     .OrderBy(item => item.CreatedAt)
                     .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (candidate.CreatedAt > now || now - candidate.CreatedAt < MtpUnattributedMinAge)
            {
                decisions.Add(UnattributedDecision(
                    candidate,
                    EvidenceRetentionAction.Preserved,
                    "mtp-unattributed-is-young"));
                continue;
            }

            if (candidate.OwnerProcessId is int ownerProcessId &&
                IsProcessAlive(ownerProcessId, processHasExitedForTests))
            {
                decisions.Add(UnattributedDecision(
                    candidate,
                    EvidenceRetentionAction.Preserved,
                    "mtp-unattributed-owner-is-live"));
                continue;
            }

            var measurement = TryMeasureDirectory(candidate.Path);
            if (!measurement.Success)
            {
                decisions.Add(UnattributedDecision(
                    candidate,
                    EvidenceRetentionAction.RetainedUndecidable,
                    "mtp-unattributed-size-is-unreadable",
                    failureExceptionType: measurement.ExceptionType));
                continue;
            }

            measurable.Add(new MtpUnattributedMeasuredCandidate(candidate, measurement.Bytes));
        }

        var retainedBytes = measurable.Aggregate(
            0L,
            (total, candidate) => candidate.Bytes > long.MaxValue - total
                ? long.MaxValue
                : total + candidate.Bytes);
        var reclaimed = 0;
        foreach (var measured in measurable)
        {
            var candidate = measured.Candidate;
            var pastAgeBound = now - candidate.CreatedAt >= MtpUnattributedMaxAge;
            var pastSizeBound = retainedBytes > maxBytes;
            if (!pastAgeBound && !pastSizeBound)
            {
                decisions.Add(UnattributedDecision(
                    candidate,
                    EvidenceRetentionAction.Preserved,
                    "mtp-unattributed-inside-age-and-size-bounds"));
                continue;
            }

            if (reclaimed >= maxReclaims)
            {
                decisions.Add(UnattributedDecision(
                    candidate,
                    EvidenceRetentionAction.Preserved,
                    "mtp-unattributed-deferred-to-next-sweep"));
                continue;
            }

            var deletion = TryDeleteDirectory(candidate.Path);
            decisions.Add(UnattributedDecision(
                candidate,
                deletion.Success ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.DeferredLocked,
                deletion.Success
                    ? pastAgeBound ? "mtp-unattributed-past-age-bound" : "mtp-unattributed-past-size-bound"
                    : "exclusive-delete-failed",
                deletion.BytesAttempted,
                deletion.Success ? deletion.BytesAttempted : 0,
                deletion.ExceptionType));
            if (deletion.Success)
            {
                reclaimed++;
                retainedBytes = Math.Max(0, retainedBytes - measured.Bytes);
            }
        }
    }

    private static EvidenceRetentionDecision UnattributedDecision(
        MtpUnattributedCandidate candidate,
        EvidenceRetentionAction action,
        string reason,
        long bytesAttempted = 0,
        long bytesReclaimed = 0,
        string? failureExceptionType = null) =>
        new(
            EvidenceArtifactFamily.MtpTestRuns,
            action,
            candidate.Path,
            null,
            EvidenceOwnerResolution.Unrecorded,
            reason,
            AttemptId: "unowned",
            BytesAttempted: bytesAttempted,
            BytesReclaimed: bytesReclaimed,
            FailureExceptionType: failureExceptionType);

    private static bool IsProcessAlive(int processId, Func<int, bool>? processHasExitedForTests)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            if (processHasExitedForTests is not null)
            {
                return !processHasExitedForTests(processId);
            }

            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static bool TryRefreshMtpAttemptOwner(
        MtpAttemptOwner owner,
        out MtpAttemptOwner refreshed,
        out string reason)
    {
        if (!TryReadAttemptIdentities(
                owner.GoalDirectory,
                owner.Goal.GoalId,
                out var attempts,
                out reason))
        {
            refreshed = default!;
            return false;
        }

        var matches = attempts
            .Where(attempt => attempt.AttemptId.Equals(owner.Attempt.AttemptId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            refreshed = default!;
            reason = matches.Length == 0
                ? "mtp-attempt-owner-disappeared-before-deletion"
                : "mtp-attempt-owner-became-ambiguous-before-deletion";
            return false;
        }

        refreshed = new MtpAttemptOwner(
            owner.Goal,
            matches[0],
            owner.GoalDirectory,
            EvidenceRetentionPolicy.ProtectedAttemptIds(attempts));
        reason = string.Empty;
        return true;
    }

    private static string AttemptOwnerKey(string goalId, string attemptId) =>
        $"{goalId.ToUpperInvariant()}\n{attemptId.ToUpperInvariant()}";

    private static Dictionary<string, List<MtpAttemptOwner>> BuildMtpAttemptOwnerIndex(
        string orchestratorDirectory,
        IReadOnlyCollection<StorageRetentionGoal> goals)
    {
        var index = new Dictionary<string, List<MtpAttemptOwner>>(StringComparer.OrdinalIgnoreCase);
        foreach (var familyDirectory in new[] { "acceptance-gate-attempts", "pre-review-evidence-attempts" })
        {
            var root = Path.Combine(orchestratorDirectory, familyDirectory);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var goalDirectory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                var goalId = Path.GetFileName(goalDirectory);
                var matchingGoals = goals
                    .Where(goal => goal.GoalId.Equals(goalId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matchingGoals.Length != 1 ||
                    !TryReadAttemptIdentities(goalDirectory, goalId, out var attempts, out _))
                {
                    continue;
                }

                var protectedIds = EvidenceRetentionPolicy.ProtectedAttemptIds(attempts);
                foreach (var attempt in attempts)
                {
                    if (!index.TryGetValue(attempt.AttemptId, out var owners))
                    {
                        owners = [];
                        index.Add(attempt.AttemptId, owners);
                    }

                    owners.Add(new MtpAttemptOwner(
                        matchingGoals[0],
                        attempt,
                        goalDirectory,
                        protectedIds));
                }
            }
        }

        return index;
    }

    private static bool TryReadMtpRunOwnership(
        string directory,
        out MtpRunOwnership ownership,
        out string reason)
    {
        var sidecarPath = Path.Combine(directory, ".mtp-run-ownership.json");
        try
        {
            if (!File.Exists(sidecarPath))
            {
                ownership = default!;
                reason = "mtp-ownership-sidecar-not-recorded";
                return false;
            }

            using var stream = new FileStream(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schemaVersion) ||
                !schemaVersion.TryGetInt32(out var version) ||
                version != 1 ||
                !root.TryGetProperty("attemptId", out var attemptIdValue) ||
                attemptIdValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(attemptIdValue.GetString()) ||
                !root.TryGetProperty("machineName", out var machineNameValue) ||
                machineNameValue.ValueKind != JsonValueKind.String ||
                !Environment.MachineName.Equals(machineNameValue.GetString(), StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("createdAt", out var createdAtValue) ||
                !createdAtValue.TryGetDateTimeOffset(out var createdAt))
            {
                ownership = default!;
                reason = "mtp-ownership-sidecar-is-invalid";
                return false;
            }

            var attemptId = attemptIdValue.GetString()!;
            if (!attemptId.Equals(Path.GetFileName(attemptId), StringComparison.Ordinal))
            {
                ownership = default!;
                reason = "mtp-ownership-attempt-is-unrecorded";
                return false;
            }

            int? ownerProcessId = null;
            if (root.TryGetProperty("ownerProcessId", out var ownerProcessIdValue) &&
                ownerProcessIdValue.ValueKind == JsonValueKind.Number &&
                ownerProcessIdValue.TryGetInt32(out var parsedOwnerProcessId) &&
                parsedOwnerProcessId > 0)
            {
                ownerProcessId = parsedOwnerProcessId;
            }

            ownership = new MtpRunOwnership(
                attemptId,
                createdAt,
                IsAttributed: !attemptId.Equals("unowned", StringComparison.OrdinalIgnoreCase),
                ownerProcessId);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ownership = default!;
            reason = "mtp-ownership-sidecar-is-unreadable";
            return false;
        }
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
                var declaredPaths = ReadDeclaredAttemptPaths(root, goalDirectory);
                var lifecycleRevision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(root.GetRawText()))).ToLowerInvariant();
                result.Add(new RetentionAttemptIdentity(
                    attemptId,
                    ordinal,
                    startedAt,
                    failed,
                    reconciled,
                    declaredPaths.Count == 0 ? null : declaredPaths,
                    lifecycleRevision));
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

    private static IReadOnlyList<string> ReadDeclaredAttemptPaths(JsonElement root, string goalDirectory)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var propertyName in new[]
        {
            "stdoutPath",
            "stderrPath",
            "exitCodePath",
            "heartbeatPath",
            "resultPath",
            "metadataPath",
            "executionDirectory"
        })
        {
            if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                AddDeclaredPath(paths, goalDirectory, value.GetString());
            }
        }

        if (root.TryGetProperty("testResultPaths", out var testResultPaths) &&
            testResultPaths.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in testResultPaths.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    AddDeclaredPath(paths, goalDirectory, value.GetString());
                }
            }
        }

        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddDeclaredPath(HashSet<string> paths, string goalDirectory, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        try
        {
            var goalRoot = Path.GetFullPath(goalDirectory);
            var fullPath = Path.GetFullPath(candidate, goalRoot);
            var relative = Path.GetRelativePath(goalRoot, fullPath);
            if (!Path.IsPathRooted(relative) &&
                !relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                paths.Add(fullPath);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Ignore an invalid optional declaration. Legacy inference is used only when no valid declaration remains.
        }
    }

    private static bool TryWriteTrxReceipt(
        string trxPath,
        string sweepId,
        string goalId,
        string attemptId,
        DateTimeOffset recordedAt)
    {
        var receiptPath = trxPath + ".test-identities.json";
        if (File.Exists(receiptPath))
        {
            try
            {
                using var existing = JsonDocument.Parse(File.ReadAllText(receiptPath));
                return existing.RootElement.TryGetProperty("source", out var source) &&
                    string.Equals(source.GetString(), Path.GetFileName(trxPath), StringComparison.OrdinalIgnoreCase) &&
                    existing.RootElement.TryGetProperty("testIdentities", out var identities) &&
                    identities.ValueKind == JsonValueKind.Array && identities.GetArrayLength() > 0 &&
                    identities.EnumerateArray().All(HasRequiredFailureSignature);
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
            var trxBytes = input.Length;
            var document = XDocument.Load(input, LoadOptions.None);
            var counters = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Counters");
            if (counters is null)
            {
                return false;
            }

            var identities = document.Descendants()
                .Where(element => element.Name.LocalName == "UnitTestResult")
                .Select(element =>
                {
                    var outcome = element.Attribute("outcome")?.Value;
                    var isFailure = string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase);
                    var errorInfo = element.Descendants()
                        .FirstOrDefault(descendant => descendant.Name.LocalName == "ErrorInfo");
                    var failureMessage = errorInfo?.Descendants()
                        .FirstOrDefault(descendant => descendant.Name.LocalName == "Message")?.Value;
                    var stackTrace = errorInfo?.Descendants()
                        .FirstOrDefault(descendant => descendant.Name.LocalName == "StackTrace")?.Value;
                    return new
                    {
                        name = element.Attribute("testName")?.Value,
                        outcome,
                        failureMessage = isFailure ? BoundTrxReceiptText(failureMessage) : null,
                        topStackFrame = isFailure ? FirstNonEmptyLine(stackTrace) : null
                    };
                })
                .Where(identity => !string.IsNullOrWhiteSpace(identity.name))
                .Distinct()
                .OrderBy(identity => identity.name, StringComparer.Ordinal)
                .ToArray();
            if (identities.Length == 0 ||
                identities.Any(identity =>
                    string.Equals(identity.outcome, "Failed", StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(identity.failureMessage) || string.IsNullOrWhiteSpace(identity.topStackFrame))))
            {
                return false;
            }
            var receipt = new
            {
                policyVersion = EvidenceRetentionPolicy.Version,
                sweepId,
                goalId,
                attemptId,
                source = Path.GetFileName(trxPath),
                trxBytes,
                recordedAt,
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

    private static bool IsAcceptanceSequenceIndex(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Equals("attempt-sequence.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathContainedBy(string directory, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
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
                    "non-terminal-evidence-unbounded-by-policy"));
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

    private static bool HasRequiredFailureSignature(JsonElement identity)
    {
        if (!identity.TryGetProperty("outcome", out var outcome) ||
            !string.Equals(outcome.GetString(), "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return identity.TryGetProperty("failureMessage", out var message) &&
            !string.IsNullOrWhiteSpace(message.GetString()) &&
            identity.TryGetProperty("topStackFrame", out var topStackFrame) &&
            !string.IsNullOrWhiteSpace(topStackFrame.GetString());
    }

    private static string? BoundTrxReceiptText(string? value)
    {
        const int maxLength = 4096;
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? null
            : trimmed.Length <= maxLength
                ? trimmed
                : trimmed[..maxLength];
    }

    private static string? FirstNonEmptyLine(string? value) =>
        BoundTrxReceiptText(value?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault());

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

    private static (bool Success, long Bytes, string? ExceptionType) TryMeasureDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return (false, 0, nameof(IOException));
            }

            long bytes = 0;
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                bytes = checked(bytes + file.Length);
            }

            return (true, bytes, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            return (false, 0, ex.GetType().Name);
        }
    }

    private static (bool Success, long BytesAttempted, string? ExceptionType) TryDeleteDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            var measurement = TryMeasureEntry(directory);
            if (!measurement.Success)
            {
                return (false, 0, measurement.ExceptionType);
            }

            Directory.Delete(path, recursive: true);
            return (true, measurement.Bytes, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            return (false, 0, ex.GetType().Name);
        }
    }

    private sealed record MtpRunOwnership(
        string AttemptId,
        DateTimeOffset CreatedAt,
        bool IsAttributed,
        int? OwnerProcessId);

    private sealed record MtpUnattributedCandidate(
        string Path,
        DateTimeOffset CreatedAt,
        int? OwnerProcessId);

    private sealed record MtpUnattributedMeasuredCandidate(
        MtpUnattributedCandidate Candidate,
        long Bytes);

    private sealed record MtpAttemptOwner(
        StorageRetentionGoal Goal,
        RetentionAttemptIdentity Attempt,
        string GoalDirectory,
        IReadOnlySet<string> ProtectedAttemptIds);

    private sealed record MtpRetentionCandidate(
        string Path,
        DateTimeOffset CreatedAt,
        MtpAttemptOwner Owner);

    private sealed class MutexLease : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _ownsMutex;

        private MutexLease(Mutex mutex, bool ownsMutex)
        {
            _mutex = mutex;
            _ownsMutex = ownsMutex;
        }

        public static MutexLease? TryAcquire(string name) => TryAcquire(name, TimeSpan.Zero);

        public static MutexLease? TryAcquire(string name, TimeSpan timeout)
        {
            var mutex = new Mutex(false, name);
            try
            {
                var ownsMutex = false;
                try
                {
                    ownsMutex = mutex.WaitOne(timeout);
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
