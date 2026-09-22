using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal const int DefaultMaintenanceLeaseStartupAttempts = 3;
    internal static readonly TimeSpan DefaultMaintenanceLeaseStartupRetryDelay = TimeSpan.FromSeconds(5);

    private ActiveDatabaseLeaseAcquisition AcquireActiveDatabaseLeases(
        string leaseDirectory,
        Action<TimeSpan>? retryDelay)
    {
        var stateDatabasePath = _workspace?.SqliteStatePath ?? Path.Combine(leaseDirectory, "state.db");
        var runEventDatabasePath = _workspace?.RunEventStorePath ?? Path.Combine(leaseDirectory, "run-events.db");
        var previousWriter = CurrentConductEventLogWriter.Value;
        CurrentConductEventLogWriter.Value = _conductEventLogWriter;
        var stateLeasePath = SqliteMaintenanceLease.ForDatabase(stateDatabasePath);
        var runEventLeasePath = SqliteMaintenanceLease.ForDatabase(runEventDatabasePath);
        SqliteMaintenanceLease? stateLease = null;
        SqliteMaintenanceLease? runEventLease = null;
        try
        {
            for (var attempt = 1; attempt <= DefaultMaintenanceLeaseStartupAttempts; attempt++)
            {
                var blockedLeasePath = stateLeasePath;
                if (SqliteMaintenanceLease.TryAcquireShared(stateLeasePath, out stateLease))
                {
                    blockedLeasePath = runEventLeasePath;
                    if (SqliteMaintenanceLease.TryAcquireShared(runEventLeasePath, out runEventLease))
                    {
                        return new ActiveDatabaseLeaseAcquisition(
                            stateLease, runEventLease, previousWriter, DeferredSummary: null);
                    }

                    stateLease.Dispose();
                    stateLease = null;
                }

                var retryScheduled = attempt < DefaultMaintenanceLeaseStartupAttempts;
                EmitProgress(
                    $"LOOP_START_DEFERRED reason=sqlite-maintenance-active lease={blockedLeasePath} " +
                    $"attempt={attempt} maxAttempts={DefaultMaintenanceLeaseStartupAttempts} retryScheduled={retryScheduled}");
                if (retryScheduled)
                    (retryDelay ?? Thread.Sleep)(DefaultMaintenanceLeaseStartupRetryDelay);
            }

            CurrentConductEventLogWriter.Value = previousWriter;
            return new ActiveDatabaseLeaseAcquisition(
                StateLease: null,
                RunEventLease: null,
                previousWriter,
                new BatchLoopSummary(0, 0, 0, 0, 0, 0, false, StopReason: "sqlite-maintenance-active"));
        }
        catch
        {
            stateLease?.Dispose();
            runEventLease?.Dispose();
            CurrentConductEventLogWriter.Value = previousWriter;
            throw;
        }
    }

    private sealed record ActiveDatabaseLeaseAcquisition(
        SqliteMaintenanceLease? StateLease,
        SqliteMaintenanceLease? RunEventLease,
        ConductEventLogWriter? PreviousConductEventLogWriter,
        BatchLoopSummary? DeferredSummary)
    {
        public bool Succeeded => StateLease is not null && RunEventLease is not null;
    }
}
