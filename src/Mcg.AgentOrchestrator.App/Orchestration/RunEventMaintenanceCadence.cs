using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class RunEventMaintenanceCadence
{
    internal const string Operation = "run-events:maintenance";
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> NextDueByStorePath =
        new(StringComparer.OrdinalIgnoreCase);

    public static RunEventMaintenanceCadenceResult TryRunIfDue(
        string runEventStorePath,
        string conductEventsLogPath,
        Func<DateTimeOffset>? utcNow = null,
        Func<SqliteRunEventStore, RunEventMaintenanceOptions, RunEventMaintenanceResult>? maintenanceOperation = null,
        OrchestratorWorkspace? workspace = null,
        IReadOnlyCollection<Goal>? goals = null)
    {
        var now = (utcNow ?? (() => DateTimeOffset.UtcNow))();
        var cadenceKey = Path.GetFullPath(runEventStorePath);
        if (NextDueByStorePath.TryGetValue(cadenceKey, out var nextDue) && now < nextDue)
        {
            return SkippedResult();
        }

        var journal = new ConductEventLogWriter(conductEventsLogPath);
        try
        {
            var store = new SqliteRunEventStore(
                runEventStorePath,
                ensureSchema: !File.Exists(runEventStorePath));
            var latest = LatestMaintenanceMarker(store, conductEventsLogPath);
            if (latest is not null && now - latest.Value < Interval)
            {
                NextDueByStorePath[cadenceKey] = latest.Value.Add(Interval);
                return SkippedResult();
            }

            var terminalGoalIds = goals?
                .Where(goal => goal.IsTerminal)
                .Select(goal => goal.Id.Value)
                .ToArray();
            var options = RunEventMaintenanceOptions.Default with
            {
                UtcNow = now,
                Vacuum = IsOffPeakVacuumWindow(now),
                TerminalGoalIds = terminalGoalIds
            };
            var result = maintenanceOperation is null
                ? store.MaintainAsync(options).GetAwaiter().GetResult()
                : maintenanceOperation(store, options);
            var artifactRetention = workspace is null || goals is null
                ? null
                : StorageRetentionMaintenance.Run(workspace, goals, now);
            var receipt = FormatReceipt("cadence", options, result);
            Console.WriteLine(receipt);
            TryAppendJournal(journal, "run-events-maintenance", receipt, now);
            if (!result.Deferred)
            {
                TryAppendRunEventReceipt(store, "cadence", options, result, now);
                NextDueByStorePath[cadenceKey] = now.Add(Interval);
            }

            return new RunEventMaintenanceCadenceResult(
                Attempted: true,
                Skipped: false,
                Deferred: result.Deferred,
                Failed: false,
                Reason: result.DeferredReason,
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
                ArtifactRetention: null);
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
