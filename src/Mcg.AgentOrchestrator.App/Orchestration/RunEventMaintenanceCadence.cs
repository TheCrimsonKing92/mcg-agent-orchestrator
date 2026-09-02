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
    internal static readonly TimeSpan VacuumInterval = TimeSpan.FromDays(7);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> NextDueByStorePath =
        new(StringComparer.OrdinalIgnoreCase);

    public static RunEventMaintenanceCadenceResult TryRunIfDue(
        string runEventStorePath,
        string conductEventsLogPath,
        Func<DateTimeOffset>? utcNow = null,
        Func<SqliteRunEventStore, RunEventMaintenanceOptions, RunEventMaintenanceResult>? maintenanceOperation = null,
        OrchestratorWorkspace? workspace = null,
        string? mtpResultsRootOverride = null)
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
            var latest = LatestMaintenanceMarker(store, conductEventsLogPath);
            var latestVacuum = LatestVacuumMarker(store);
            var artifactFailureDue = HasUnresolvedArtifactRetentionFailure(store, latest);
            var vacuumDue = IsOffPeakVacuumWindow(now) &&
                (latestVacuum is null || now - latestVacuum.Value >= VacuumInterval);
            if (latest is not null && now - latest.Value < Interval && !vacuumDue && !artifactFailureDue)
            {
                NextDueByStorePath[cadenceKey] = NextDue(latest.Value);
                return SkippedResult();
            }

            var retentionGoals = workspace is null
                ? null
                : StorageRetentionMaintenance.LoadPersistedGoals(
                    new SqliteOrchestratorStateRepository(workspace.SqliteStatePath));
            var terminalGoalIds = retentionGoals?
                .Select(goal => goal.GoalId)
                .ToArray();
            var options = RunEventMaintenanceOptions.Default with
            {
                UtcNow = now,
                Vacuum = vacuumDue,
                TerminalGoalIds = terminalGoalIds
            };
            var result = maintenanceOperation is null
                ? store.MaintainAsync(options).GetAwaiter().GetResult()
                : maintenanceOperation(store, options);
            artifactRetention = workspace is null || retentionGoals is null
                ? null
                : StorageRetentionMaintenance.Run(
                    workspace.LogDirectory,
                    workspace.OrchestratorDirectory,
                    workspace.ExecutionDirectory,
                    retentionGoals,
                    now,
                    mtpResultsRoot: mtpResultsRootOverride ?? StorageRetentionMaintenance.DefaultMtpResultsRoot());
            var receipt = FormatReceipt("cadence", options, result);
            Console.WriteLine(receipt);
            TryAppendJournal(journal, "run-events-maintenance", receipt, now);
            if (artifactRetention is not null)
            {
                var artifactReceipt = FormatArtifactRetentionReceipt(artifactRetention);
                Console.WriteLine(artifactReceipt);
                journal.AppendRequired("storage-retention-sweep", null, artifactReceipt, now);
                AppendArtifactRetentionReceipt(store, artifactRetention, now);
            }
            if (!result.Deferred)
            {
                TryAppendRunEventReceipt(store, "cadence", options, result, now);
                if (result.VacuumCompleted)
                {
                    TryAppendVacuumReceipt(store, result, now);
                }
                if (artifactRetention?.Failed == true)
                {
                    NextDueByStorePath.TryRemove(cadenceKey, out _);
                }
                else
                {
                    NextDueByStorePath[cadenceKey] = NextDue(now);
                }
            }

            return new RunEventMaintenanceCadenceResult(
                Attempted: true,
                Skipped: false,
                Deferred: result.Deferred,
                Failed: artifactRetention?.Failed == true,
                Reason: artifactRetention?.Failed == true ? "storage-retention-failed" : result.DeferredReason,
                Maintenance: result,
                ArtifactRetention: artifactRetention);
        }
        catch (Exception ex)
        {
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

    internal static DateTimeOffset NextDue(DateTimeOffset now)
    {
        var dailyDue = now.Add(Interval);
        var daysUntilSunday = ((int)DayOfWeek.Sunday - (int)now.DayOfWeek + 7) % 7;
        var vacuumDue = new DateTimeOffset(
            now.Year,
            now.Month,
            now.Day,
            2,
            0,
            0,
            TimeSpan.Zero).AddDays(daysUntilSunday);
        if (vacuumDue <= now)
        {
            vacuumDue = vacuumDue.AddDays(7);
        }

        return vacuumDue < dailyDue ? vacuumDue : dailyDue;
    }

    public static string FormatReceipt(
        string mode,
        RunEventMaintenanceOptions options,
        RunEventMaintenanceResult result)
    {
        var status = result.Deferred ? "deferred" : "completed";
        return string.Create(CultureInfo.InvariantCulture,
            $"RUN_EVENTS_MAINTENANCE mode={mode} status={status} agedDeleted={result.AgedConductorTickRowsDeleted} oversizedDeleted={result.OversizedConductorTickRowsDeleted} totalDeleted={result.ConductorTickRowsDeleted} terminalGoalOperationsDeleted={result.TerminalGoalOperationRowsDeleted} payloadBytesEstimate={result.DeletedPayloadBytesEstimate} maxRowsPerTransaction={result.MaxRowsDeletedInTransaction} durationMs={(long)result.Duration.TotalMilliseconds} tickMaxAgeDays={options.ConductorTickMaxAge.TotalDays:0.###} terminalGoalOperationMaxAgeDays={options.EffectiveTerminalGoalOperationMaxAge.TotalDays:0.###} keepTickRows={options.MinConductorTickRowsToKeep} payloadMaxBytes={options.MaxConductorTickPayloadBytes} batchSize={Math.Clamp(options.DeleteBatchSize, 1, 1000)} bytesBefore={result.BytesBefore} bytesAfter={result.BytesAfter} vacuumRequested={result.VacuumRequested} vacuumCompleted={result.VacuumCompleted} vacuumDeferred={result.VacuumDeferred}{(string.IsNullOrWhiteSpace(result.DeferredReason) ? "" : $" deferredReason={Sanitize(result.DeferredReason)}")}");
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
                Status: result.Deferred ? "Deferred" : "Completed",
                Detail: FormatReceipt(mode, options, result),
                PayloadJson: JsonSerializer.Serialize(new
                {
                    mode,
                    result.Deferred,
                    result.DeferredReason,
                    result.ConductorTickRowsDeleted,
                    result.AgedConductorTickRowsDeleted,
                    result.OversizedConductorTickRowsDeleted,
                    result.DeletedPayloadBytesEstimate,
                    result.MaxRowsDeletedInTransaction,
                    durationMs = (long)result.Duration.TotalMilliseconds,
                    result.BytesBefore,
                    result.BytesAfter,
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
        var deferred = result.Decisions.Count(decision => decision.Action is
            EvidenceRetentionAction.DeferredLease or
            EvidenceRetentionAction.DeferredLive or
            EvidenceRetentionAction.DeferredLocked);
        var status = ArtifactRetentionStatus(result, reclaimedBytes, deferred);
        var actions = FormatDecisionCounts(result.Decisions, decision => decision.Action.ToString());
        var reasons = FormatDecisionCounts(result.Decisions, decision => decision.Reason);
        return $"STORAGE_RETENTION policyVersion={EvidenceRetentionPolicy.Version} sweepId={result.SweepId} status={status} decisions={result.Decisions.Count} reclaimedBytes={reclaimedBytes} deferred={deferred} actions={actions} reasons={reasons}";
    }

    internal static void AppendArtifactRetentionReceipt(
        SqliteRunEventStore store,
        StorageRetentionResult result,
        DateTimeOffset occurredAt)
    {
        const int decisionLimit = 256;
        var decisions = result.Decisions.Take(decisionLimit).ToArray();
        var truncated = result.Decisions.Count > decisions.Length;
        var deferred = result.Decisions.Any(decision => decision.Action is
            EvidenceRetentionAction.DeferredLease or
            EvidenceRetentionAction.DeferredLive or
            EvidenceRetentionAction.DeferredLocked);
        var reclaimedBytes = result.Decisions.Sum(decision => decision.BytesReclaimed);
        var status = ArtifactRetentionStatus(result, reclaimedBytes, deferred ? 1 : 0);
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
                decisionsTruncated = truncated,
                totalDecisionCount = result.Decisions.Count,
                decisions
            }),
            OccurredAt: occurredAt))
            .GetAwaiter()
            .GetResult();
    }

    private static string ArtifactRetentionStatus(
        StorageRetentionResult result,
        long reclaimedBytes,
        int deferred)
    {
        if (result.Failed)
        {
            return reclaimedBytes > 0 ? "partial-failed" : "failed";
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

    private static bool HasUnresolvedArtifactRetentionFailure(
        SqliteRunEventStore store,
        DateTimeOffset? latestMaintenance)
    {
        try
        {
            var latestArtifact = store.ReadLatestAsync(
                    RunEventTypes.EvidenceRetention,
                    ArtifactRetentionOperation)
                .GetAwaiter()
                .GetResult();
            return latestArtifact is not null &&
                string.Equals(latestArtifact.Status, "Failed", StringComparison.OrdinalIgnoreCase) &&
                (latestMaintenance is null || latestArtifact.OccurredAt >= latestMaintenance.Value);
        }
        catch
        {
            return false;
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

internal sealed record RunEventMaintenanceCadenceResult(
    bool Attempted,
    bool Skipped,
    bool Deferred,
    bool Failed,
    string? Reason,
    RunEventMaintenanceResult? Maintenance,
    StorageRetentionResult? ArtifactRetention = null);
