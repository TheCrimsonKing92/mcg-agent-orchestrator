using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class RunEventMaintenanceCadence
{
    internal const string Operation = "run-events:maintenance";
    internal const string VacuumOperation = "run-events:vacuum";
    internal const string ArtifactRetentionOperation = "storage-retention:sweep";
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    internal static readonly TimeSpan ArtifactFailureRetryInterval = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> NextDueByStorePath =
        new(StringComparer.OrdinalIgnoreCase);

    public static RunEventMaintenanceCadenceResult TryRunIfDue(
        string runEventStorePath,
        string conductEventsLogPath,
        Func<DateTimeOffset>? utcNow = null,
        Func<SqliteRunEventStore, RunEventMaintenanceOptions, RunEventMaintenanceResult>? maintenanceOperation = null,
        OrchestratorWorkspace? workspace = null,
        string? mtpResultsRootOverride = null,
        Func<OrchestratorWorkspace, IReadOnlyCollection<StorageRetentionGoal>, DateTimeOffset, string?, StorageRetentionResult>?
            artifactRetentionOperation = null)
    {
        var now = (utcNow ?? (() => DateTimeOffset.UtcNow))();
        var cadenceKey = Path.GetFullPath(runEventStorePath);
        if (NextDueByStorePath.TryGetValue(cadenceKey, out var nextDue) && now < nextDue)
        {
            return SkippedResult();
        }

        var journal = new ConductEventLogWriter(conductEventsLogPath);
        StorageRetentionResult? artifactRetention = null;
        try
        {
            var store = new SqliteRunEventStore(
                runEventStorePath,
                ensureSchema: !File.Exists(runEventStorePath));
            var latestArtifact = LatestArtifactRetentionState(store);
            var artifactFailureRetryDue = latestArtifact is { Failed: true }
                ? latestArtifact.OccurredAt.Add(ArtifactFailureRetryInterval)
                : (DateTimeOffset?)null;
            var artifactRetentionDue = latestArtifact switch
            {
                { Failed: true } => now >= artifactFailureRetryDue,
                { Failed: false } when workspace is not null => now - latestArtifact.OccurredAt >= Interval,
                null when workspace is not null => true,
                _ => false
            };
            var latestState = LatestMaintenanceState(store);
            if (latestState?.Disposition is SqliteMaintenanceDisposition.Incomplete or SqliteMaintenanceDisposition.Deferred &&
                latestState.NextAttemptAt is { } continuationDue &&
                now < continuationDue &&
                !artifactRetentionDue)
            {
                NextDueByStorePath[cadenceKey] = artifactFailureRetryDue is { } retryDue && retryDue < continuationDue
                    ? retryDue
                    : continuationDue;
                return SkippedResult();
            }
            var latest = LatestMaintenanceMarker(store, conductEventsLogPath);
            var continuationDueNow = latestState?.Disposition is
                SqliteMaintenanceDisposition.Incomplete or SqliteMaintenanceDisposition.Deferred &&
                (latestState.NextAttemptAt is null || now >= latestState.NextAttemptAt);
            if (latest is not null &&
                now - latest.Value < Interval &&
                !continuationDueNow &&
                !artifactRetentionDue)
            {
                var scheduledDue = NextDue(latest.Value);
                if (artifactFailureRetryDue is not null && artifactFailureRetryDue.Value < scheduledDue)
                {
                    scheduledDue = artifactFailureRetryDue.Value;
                }
                NextDueByStorePath[cadenceKey] = scheduledDue;
                return SkippedResult();
            }

            var retentionGoals = workspace is null
                ? null
                : StorageRetentionMaintenance.LoadPersistedGoals(
                    new SqliteOrchestratorStateRepository(workspace.SqliteStatePath));
            var terminalGoalIds = retentionGoals is null
                ? null
                : StorageRetentionMaintenance.SelectGoalOperationPrunableIds(retentionGoals);
            var options = RunEventMaintenanceOptions.Default with
            {
                UtcNow = now,
                // Full VACUUM is an explicitly offline operator action. The live conductor
                // performs only bounded deletion, checkpointing, and incremental reclamation.
                Vacuum = false,
                TerminalGoalIds = terminalGoalIds,
                ConsecutiveNoProgressAttempts = latestState?.ConsecutiveNoProgressAttempts ?? 0
            };
            var result = maintenanceOperation is null
                ? store.MaintainAsync(options).GetAwaiter().GetResult()
                : maintenanceOperation(store, options);
            result = BoundDeferredResult(result, options, now);
            artifactRetention = workspace is null || retentionGoals is null || !artifactRetentionDue
                ? null
                : artifactRetentionOperation is null
                    ? StorageRetentionMaintenance.Run(
                        workspace.LogDirectory,
                        workspace.OrchestratorDirectory,
                        workspace.ExecutionDirectory,
                        retentionGoals,
                        now,
                        mtpResultsRoot: mtpResultsRootOverride ?? StorageRetentionMaintenance.DefaultMtpResultsRoot())
                    : artifactRetentionOperation(workspace, retentionGoals, now, mtpResultsRootOverride);
            var receipt = FormatReceipt("cadence", options, result);
            Console.WriteLine(receipt);
            TryAppendJournal(journal, "run-events-maintenance", receipt, now);
            TryAppendRunEventReceipt(store, "cadence", options, result, now);
            if (artifactRetention is not null)
            {
                var artifactReceipt = FormatArtifactRetentionReceipt(artifactRetention);
                Console.WriteLine(artifactReceipt);
                journal.AppendRequired("storage-retention-sweep", null, artifactReceipt, now);
                try
                {
                    AppendArtifactRetentionReceipt(store, artifactRetention, now);
                }
                catch (Exception ex)
                {
                    artifactRetention = WithReceiptPersistenceFailure(
                        artifactRetention,
                        runEventStorePath,
                        ex);
                    var failedReceipt = FormatArtifactRetentionReceipt(artifactRetention);
                    Console.WriteLine(failedReceipt);
                    journal.AppendRequired("storage-retention-sweep", null, failedReceipt, now);
                    throw;
                }
            }
            if (result.Disposition != SqliteMaintenanceDisposition.Deferred)
            {
                if (result.VacuumCompleted && result.Disposition == SqliteMaintenanceDisposition.Completed)
                {
                    TryAppendVacuumReceipt(store, result, now);
                }
            }
            var scheduledNextDue = result.NextAttemptAt ?? NextDue(now);
            var nextArtifactDue = artifactRetention switch
            {
                { Failed: true } => now.Add(ArtifactFailureRetryInterval),
                null when latestArtifact is { Failed: true } =>
                    artifactFailureRetryDue > now ? artifactFailureRetryDue : now.Add(ArtifactFailureRetryInterval),
                null when latestArtifact is { Failed: false } && workspace is not null =>
                    latestArtifact.OccurredAt.Add(Interval),
                _ => (DateTimeOffset?)null
            };
            if (nextArtifactDue is { } artifactDue && artifactDue < scheduledNextDue)
            {
                scheduledNextDue = artifactDue;
            }
            NextDueByStorePath[cadenceKey] = scheduledNextDue;

            return new RunEventMaintenanceCadenceResult(
                Attempted: true,
                Skipped: false,
                Deferred: result.Deferred,
                Failed: artifactRetention?.Failed == true,
                Reason: artifactRetention?.Failed == true
                    ? "storage-retention-failed"
                    : result.Deferred
                        ? result.DeferredReason ?? result.Reason.ToString()
                        : result.Disposition == SqliteMaintenanceDisposition.Completed
                            ? null
                            : result.Reason.ToString(),
                Maintenance: result,
                ArtifactRetention: artifactRetention);
        }
        catch (Exception ex)
        {
            NextDueByStorePath[cadenceKey] = now.Add(ArtifactFailureRetryInterval);
            var line = $"RUN_EVENTS_MAINTENANCE_FAILED exception={ex.GetType().Name} message={Sanitize(ex.Message)}";
            Console.WriteLine(line);
            TryAppendJournal(journal, "run-events-maintenance-failed", line, now);
            return new RunEventMaintenanceCadenceResult(
                Attempted: true,
                Skipped: false,
                Deferred: false,
                Failed: true,
                Reason: ex.GetType().Name,
                Maintenance: null,
                ArtifactRetention: artifactRetention);
        }
    }

    private static RunEventMaintenanceCadenceResult SkippedResult() =>
        new(
            Attempted: false,
            Skipped: true,
            Deferred: false,
            Failed: false,
            Reason: "fresh",
            Maintenance: null,
            ArtifactRetention: null);

    internal static bool IsOffPeakVacuumWindow(DateTimeOffset now) =>
        now.DayOfWeek == DayOfWeek.Sunday && now.Hour >= 2 && now.Hour < 5;

    internal static DateTimeOffset NextDue(DateTimeOffset now) => now.Add(Interval);

    private static RunEventMaintenanceResult BoundDeferredResult(
        RunEventMaintenanceResult result,
        RunEventMaintenanceOptions options,
        DateTimeOffset now)
    {
        if (result.Disposition != SqliteMaintenanceDisposition.Deferred)
            return result;

        var attempts = Math.Max(
            result.ConsecutiveNoProgressAttempts,
            options.ConsecutiveNoProgressAttempts + 1);
        if (attempts >= options.MaxNoProgressAttempts)
        {
            return result with
            {
                Deferred = false,
                DeferredReason = "database-busy-limit",
                Disposition = SqliteMaintenanceDisposition.Stalled,
                Reason = SqliteMaintenanceReason.DatabaseBusyLimit,
                NextAttemptAt = null,
                ConsecutiveNoProgressAttempts = attempts
            };
        }

        var multiplier = 1L << Math.Clamp(attempts - 1, 0, 6);
        var delayTicks = Math.Min(
            Interval.Ticks,
            checked(options.EffectiveContinuationDelay.Ticks * multiplier));
        return result with
        {
            NextAttemptAt = now.Add(TimeSpan.FromTicks(delayTicks)),
            ConsecutiveNoProgressAttempts = attempts
        };
    }

    public static string FormatReceipt(
        string mode,
        RunEventMaintenanceOptions options,
        RunEventMaintenanceResult result)
    {
        var status = result.Disposition.ToString().ToLowerInvariant();
        var before = result.StorageBefore ?? new SqliteStorageSnapshot(result.BytesBefore, 0, 0, 0);
        var mutation = result.StorageAfterMutation ?? new SqliteStorageSnapshot(result.BytesAfter, 0, 0, 0);
        var after = result.StorageAfterConvergence ?? new SqliteStorageSnapshot(result.BytesAfter, 0, 0, 0);
        return string.Create(CultureInfo.InvariantCulture,
            $"RUN_EVENTS_MAINTENANCE mode={mode} status={status} reason={result.Reason} agedDeleted={result.AgedConductorTickRowsDeleted} oversizedDeleted={result.OversizedConductorTickRowsDeleted} totalDeleted={result.ConductorTickRowsDeleted} terminalGoalOperationsDeleted={result.TerminalGoalOperationRowsDeleted} payloadBytesEstimate={result.DeletedPayloadBytesEstimate} maxRowsPerTransaction={result.MaxRowsDeletedInTransaction} batchesCompleted={result.DeleteBatchesCompleted} remainingEligibleRows={result.RemainingEligibleRows} remainingBytesOverBudget={result.RemainingBytesOverBudget} durationMs={(long)result.Duration.TotalMilliseconds} tickMaxAgeDays={options.ConductorTickMaxAge.TotalDays:0.###} terminalGoalOperationMaxAgeDays={options.EffectiveTerminalGoalOperationMaxAge.TotalDays:0.###} recentTerminalProtectionDays={options.EffectiveRecentTerminalGoalProtectionAge.TotalDays:0.###} keepTickRows={options.MinConductorTickRowsToKeep} payloadMaxBytes={options.MaxConductorTickPayloadBytes} batchSize={Math.Clamp(options.DeleteBatchSize, 1, 1000)} maxBatchesPerPass={options.MaxDeleteBatchesPerPass} beforeMain={before.MainDatabaseBytes} beforeWal={before.WalBytes} beforeShm={before.ShmBytes} beforeOther={before.OtherTransientBytes} beforeTotal={before.TotalBytes} mutationMain={mutation.MainDatabaseBytes} mutationWal={mutation.WalBytes} mutationShm={mutation.ShmBytes} mutationOther={mutation.OtherTransientBytes} mutationTotal={mutation.TotalBytes} afterMain={after.MainDatabaseBytes} afterWal={after.WalBytes} afterShm={after.ShmBytes} afterOther={after.OtherTransientBytes} afterTotal={after.TotalBytes} checkpointBusy={result.Checkpoint?.Busy ?? -1} checkpointLogPages={result.Checkpoint?.LogPages ?? -1} checkpointedPages={result.Checkpoint?.CheckpointedPages ?? -1} nextAttemptAt={result.NextAttemptAt?.ToString("O", CultureInfo.InvariantCulture) ?? "none"} noProgressAttempts={result.ConsecutiveNoProgressAttempts} vacuumRequested={result.VacuumRequested} vacuumCompleted={result.VacuumCompleted} vacuumDeferred={result.VacuumDeferred}{(string.IsNullOrWhiteSpace(result.DeferredReason) ? "" : $" deferredReason={Sanitize(result.DeferredReason)}")}");
    }

    public static void TryAppendRunEventReceipt(
        SqliteRunEventStore store,
        string mode,
        RunEventMaintenanceOptions options,
        RunEventMaintenanceResult result,
        DateTimeOffset occurredAt)
    {
        try
        {
            store.AppendAsync(new RunEventAppend(
                RunEventTypes.RunEventMaintenance,
                GoalId: null,
                Operation: Operation,
                Status: result.Disposition.ToString(),
                Detail: FormatReceipt(mode, options, result),
                PayloadJson: JsonSerializer.Serialize(new
                {
                    mode,
                    result.Deferred,
                    result.DeferredReason,
                    disposition = result.Disposition.ToString(),
                    reason = result.Reason.ToString(),
                    result.ConductorTickRowsDeleted,
                    result.AgedConductorTickRowsDeleted,
                    result.OversizedConductorTickRowsDeleted,
                    result.DeletedPayloadBytesEstimate,
                    result.MaxRowsDeletedInTransaction,
                    durationMs = (long)result.Duration.TotalMilliseconds,
                    result.BytesBefore,
                    result.BytesAfter,
                    result.StorageBefore,
                    result.StorageAfterMutation,
                    result.StorageAfterConvergence,
                    result.Checkpoint,
                    result.RemainingEligibleRows,
                    result.RemainingBytesOverBudget,
                    nextAttemptAt = result.NextAttemptAt,
                    consecutiveNoProgressAttempts = result.ConsecutiveNoProgressAttempts,
                    result.DeleteBatchesCompleted,
                    result.VacuumRequested,
                    result.VacuumCompleted,
                    result.VacuumDeferred,
                    result.TerminalGoalOperationRowsDeleted,
                    options.MinConductorTickRowsToKeep,
                    options.MaxConductorTickPayloadBytes,
                    deleteBatchSize = Math.Clamp(options.DeleteBatchSize, 1, 1000),
                    conductorTickMaxAgeDays = options.ConductorTickMaxAge.TotalDays,
                    options.LegacyOversizedConductorTickPurge
                }),
                OccurredAt: occurredAt))
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Maintenance receipts are observability; a failed receipt write must not fail maintenance.
        }
    }

    internal static string FormatArtifactRetentionReceipt(StorageRetentionResult result)
    {
        var reclaimedBytes = result.Decisions.Sum(decision => decision.BytesReclaimed);
        var mtpUnreclaimableDirectoriesUnmeasured = result.Decisions.Count(decision =>
            decision.Family == EvidenceArtifactFamily.MtpTestRuns &&
            decision.Action == EvidenceRetentionAction.RetainedUndecidable);
        var retainedUndecidable = result.Decisions.Count(decision =>
            decision.Action == EvidenceRetentionAction.RetainedUndecidable);
        var nonTerminalUnbounded = result.Decisions.Count(decision =>
            decision.Reason == "non-terminal-evidence-unbounded-by-policy");
        var deferred = result.Decisions.Count(decision => decision.Action is
            EvidenceRetentionAction.DeferredLease or
            EvidenceRetentionAction.DeferredLive or
            EvidenceRetentionAction.DeferredLocked);
        var status = ArtifactRetentionStatus(result, reclaimedBytes, deferred, retainedUndecidable);
        var actions = FormatDecisionCounts(result.Decisions, decision => decision.Action.ToString());
        var reasons = FormatDecisionCounts(result.Decisions, decision => decision.Reason);
        return $"STORAGE_RETENTION policyVersion={EvidenceRetentionPolicy.Version} sweepId={result.SweepId} status={status} durationMs={result.Duration.TotalMilliseconds:F0} decisions={result.Decisions.Count} reclaimedBytes={reclaimedBytes} retainedUndecidable={retainedUndecidable} nonTerminalUnbounded={nonTerminalUnbounded} mtpUnreclaimableDirectoriesUnmeasured={mtpUnreclaimableDirectoriesUnmeasured} deferred={deferred} actions={actions} reasons={reasons}";
    }

    internal static void AppendArtifactRetentionReceipt(
        SqliteRunEventStore store,
        StorageRetentionResult result,
        DateTimeOffset occurredAt)
    {
        const int decisionLimit = 256;
        var destructive = result.Decisions
            .Where(decision => decision.Action is
                EvidenceRetentionAction.Deleted or
                EvidenceRetentionAction.Compressed)
            .ToArray();
        var keptDestructive = destructive.Take(decisionLimit).ToArray();
        var decisions = keptDestructive
            .Concat(result.Decisions
                .Where(decision => decision.Action is not (
                    EvidenceRetentionAction.Deleted or
                    EvidenceRetentionAction.Compressed))
                .Take(Math.Max(0, decisionLimit - keptDestructive.Length)))
            .ToArray();
        var truncated = result.Decisions.Count > decisions.Length;
        var deferred = result.Decisions.Any(decision => decision.Action is
            EvidenceRetentionAction.DeferredLease or
            EvidenceRetentionAction.DeferredLive or
            EvidenceRetentionAction.DeferredLocked);
        var reclaimedBytes = result.Decisions.Sum(decision => decision.BytesReclaimed);
        var mtpUnreclaimableDirectoriesUnmeasured = result.Decisions.Count(decision =>
            decision.Family == EvidenceArtifactFamily.MtpTestRuns &&
            decision.Action == EvidenceRetentionAction.RetainedUndecidable);
        var retainedUndecidable = result.Decisions.Count(decision =>
            decision.Action == EvidenceRetentionAction.RetainedUndecidable);
        var nonTerminalUnbounded = result.Decisions.Count(decision =>
            decision.Reason == "non-terminal-evidence-unbounded-by-policy");
        var status = ArtifactRetentionStatus(result, reclaimedBytes, deferred ? 1 : 0, retainedUndecidable);
        store.AppendAsync(new RunEventAppend(
            RunEventTypes.EvidenceRetention,
            GoalId: null,
            Operation: ArtifactRetentionOperation,
            Status: status switch
            {
                "failed" => "Failed",
                "partial-failed" => "Failed",
                "partial" or "deferred" => "Partial",
                _ => "Completed"
            },
            Detail: FormatArtifactRetentionReceipt(result),
            PayloadJson: JsonSerializer.Serialize(new
            {
                policyVersion = EvidenceRetentionPolicy.Version,
                result.SweepId,
                result.WorkerArtifactsDeleted,
                result.WorkerLogsCompressed,
                result.SuccessfulTrxReceiptsWritten,
                result.AcceptanceArtifactsDeleted,
                result.PromptArtifactsDeleted,
                result.GoalJournalsArchived,
                reclaimedBytes,
                retainedUndecidable,
                nonTerminalUnbounded,
                mtpUnreclaimableDirectoriesUnmeasured,
                durationMs = result.Duration.TotalMilliseconds,
                decisionsTruncated = truncated,
                destructiveDecisionsDropped = destructive.Length - keptDestructive.Length,
                totalDecisionCount = result.Decisions.Count,
                decisions
            }),
            OccurredAt: occurredAt))
            .GetAwaiter()
            .GetResult();
    }

    private static StorageRetentionResult WithReceiptPersistenceFailure(
        StorageRetentionResult result,
        string runEventStorePath,
        Exception exception) =>
        result with
        {
            Decisions = result.Decisions.Append(new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.RunEvents,
                    EvidenceRetentionAction.Failed,
                    Path.GetFullPath(runEventStorePath),
                    null,
                    EvidenceOwnerResolution.Unrecorded,
                    "receipt-persistence-failed",
                    FailureExceptionType: exception.GetType().Name))
                .ToArray()
        };

    private static string ArtifactRetentionStatus(
        StorageRetentionResult result,
        long reclaimedBytes,
        int deferred,
        int retainedUndecidable)
    {
        if (result.Failed)
        {
            return reclaimedBytes > 0 ? "partial-failed" : "failed";
        }

        if (retainedUndecidable > 0)
        {
            return "partial";
        }

        if (result.Decisions.Any(decision => decision.Action == EvidenceRetentionAction.DeferredLease))
        {
            return "deferred";
        }

        return deferred > 0 ? "partial" : "completed";
    }

    private static string FormatDecisionCounts(
        IReadOnlyList<EvidenceRetentionDecision> decisions,
        Func<EvidenceRetentionDecision, string> selector) =>
        string.Join(",", decisions
            .GroupBy(selector, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{Sanitize(group.Key)}:{group.Count()}"));

    private static LatestArtifactRetentionReceipt? LatestArtifactRetentionState(
        SqliteRunEventStore store)
    {
        try
        {
            var latestArtifact = store.ReadLatestAsync(
                    RunEventTypes.EvidenceRetention,
                    ArtifactRetentionOperation)
                .GetAwaiter()
                .GetResult();
            return latestArtifact is null
                ? null
                : new LatestArtifactRetentionReceipt(
                    latestArtifact.OccurredAt,
                    string.Equals(latestArtifact.Status, "Failed", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? LatestVacuumMarker(SqliteRunEventStore store)
    {
        try
        {
            return store.ReadLatestAsync(RunEventTypes.RunEventMaintenance, VacuumOperation)
                .GetAwaiter()
                .GetResult()
                ?.OccurredAt;
        }
        catch
        {
            return null;
        }
    }

    private static LatestMaintenanceReceipt? LatestMaintenanceState(SqliteRunEventStore store)
    {
        try
        {
            var record = store.ReadLatestAsync(RunEventTypes.RunEventMaintenance, Operation)
                .GetAwaiter()
                .GetResult();
            if (record is null || string.IsNullOrWhiteSpace(record.PayloadJson))
                return null;

            using var document = JsonDocument.Parse(record.PayloadJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("disposition", out var dispositionProperty) ||
                !Enum.TryParse<SqliteMaintenanceDisposition>(
                    dispositionProperty.GetString(), ignoreCase: true, out var disposition))
            {
                return null;
            }

            DateTimeOffset? nextAttemptAt = null;
            if (root.TryGetProperty("nextAttemptAt", out var nextProperty) &&
                nextProperty.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    nextProperty.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                nextAttemptAt = parsed;
            }

            var noProgressAttempts = root.TryGetProperty("consecutiveNoProgressAttempts", out var attemptsProperty) &&
                attemptsProperty.TryGetInt32(out var parsedAttempts)
                ? Math.Max(0, parsedAttempts)
                : 0;

            return new LatestMaintenanceReceipt(record.OccurredAt, disposition, nextAttemptAt, noProgressAttempts);
        }
        catch
        {
            return null;
        }
    }

    private static void TryAppendVacuumReceipt(
        SqliteRunEventStore store,
        RunEventMaintenanceResult result,
        DateTimeOffset occurredAt)
    {
        try
        {
            store.AppendAsync(new RunEventAppend(
                RunEventTypes.RunEventMaintenance,
                GoalId: null,
                Operation: VacuumOperation,
                Status: "Completed",
                Detail: $"RUN_EVENTS_VACUUM status=completed bytesBefore={result.BytesBefore} bytesAfter={result.BytesAfter}",
                PayloadJson: JsonSerializer.Serialize(new
                {
                    result.BytesBefore,
                    result.BytesAfter,
                    result.VacuumCompleted
                }),
                OccurredAt: occurredAt))
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Vacuum receipts are scheduling hints; the next off-peak window safely retries if one is lost.
        }
    }

    private static DateTimeOffset? LatestMaintenanceMarker(SqliteRunEventStore store, string conductEventsLogPath)
    {
        DateTimeOffset? latest = null;
        try
        {
            latest = store.ReadLatestAsync(RunEventTypes.RunEventMaintenance, Operation)
                .GetAwaiter()
                .GetResult()
                ?.OccurredAt;
        }
        catch
        {
        }

        var journalLatest = TryReadLatestJournalMaintenance(conductEventsLogPath);
        if (journalLatest is not null && (latest is null || journalLatest > latest))
        {
            latest = journalLatest;
        }

        return latest;
    }

    private static DateTimeOffset? TryReadLatestJournalMaintenance(string conductEventsLogPath)
    {
        try
        {
            if (!File.Exists(conductEventsLogPath))
            {
                return null;
            }

            DateTimeOffset? latest = null;
            foreach (var line in File.ReadLines(conductEventsLogPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("eventKind", out var kind) ||
                    !string.Equals(kind.GetString(), "run-events-maintenance", StringComparison.Ordinal))
                {
                    continue;
                }

                if (root.TryGetProperty("timestamp", out var timestamp) &&
                    DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) &&
                    (latest is null || parsed > latest))
                {
                    latest = parsed;
                }
            }

            return latest;
        }
        catch
        {
            return null;
        }
    }

    private static void TryAppendJournal(
        ConductEventLogWriter journal,
        string eventKind,
        string detail,
        DateTimeOffset timestamp)
    {
        try
        {
            journal.Append(eventKind, null, detail, timestamp);
        }
        catch
        {
        }
    }

    private static string Sanitize(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.ReplaceLineEndings(" ").Replace(' ', '_');
}

internal sealed record LatestMaintenanceReceipt(
    DateTimeOffset OccurredAt,
    SqliteMaintenanceDisposition Disposition,
    DateTimeOffset? NextAttemptAt,
    int ConsecutiveNoProgressAttempts);

internal sealed record LatestArtifactRetentionReceipt(
    DateTimeOffset OccurredAt,
    bool Failed);

internal sealed record RunEventMaintenanceCadenceResult(
    bool Attempted,
    bool Skipped,
    bool Deferred,
    bool Failed,
    string? Reason,
    RunEventMaintenanceResult? Maintenance,
    StorageRetentionResult? ArtifactRetention = null);
