namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owned by one loop generation. The tick never joins its background run; command exit joins.
internal sealed class RunEventMaintenanceCadenceRunner(
    string conductEventsLogPath,
    Action cadenceOperation,
    Func<bool>? shouldStart = null)
{
    private readonly object _gate = new();
    private Task? _currentRun;

    internal static RunEventMaintenanceCadenceRunner ForWorkspace(OrchestratorWorkspace workspace) =>
        new(workspace.ConductEventsLogPath,
            () => RunEventMaintenanceCadence.TryRunIfDue(
                workspace.RunEventStorePath, workspace.ConductEventsLogPath, workspace: workspace),
            () => !RunEventMaintenanceCadence.IsKnownFresh(workspace.RunEventStorePath, DateTimeOffset.UtcNow));

    internal Task? CurrentRun
    {
        get { lock (_gate) return _currentRun; }
    }

    internal async Task WaitForCurrentRunAsync()
    {
        // Command exit owns this join, after the loop has stopped scheduling new runs.
        // Do not hold the scheduling lock while waiting for background IO to finish.
        var run = CurrentRun;
        try
        {
            if (run is not null)
                await run.ConfigureAwait(false);
        }
        catch (Exception ex) { ReportFailure(ex); }
    }

    internal bool OnTick()
    {
        lock (_gate)
        {
            if (_currentRun is { IsCompleted: false })
                return false;

            // Observe every finished task before replacing it, including any unexpected fault.
            if (_currentRun is not null)
                _ = _currentRun.Exception;
            _currentRun = null;

            if (shouldStart is not null && !shouldStart())
                return false;

            _currentRun = Task.Run(() =>
            {
                TryAppendJournal("storage-retention-sweep-started", "STORAGE_RETENTION_SWEEP_STARTED");
                try
                {
                    cadenceOperation();
                }
                catch (Exception ex)
                {
                    ReportFailure(ex);
                }
            });
            return true;
        }
    }

    private void ReportFailure(Exception ex)
    {
        var message = string.IsNullOrWhiteSpace(ex.Message)
            ? "none"
            : ex.Message.ReplaceLineEndings(" ").Replace(' ', '_');
        var line = $"RUN_EVENTS_MAINTENANCE_FAILED exception={ex.GetType().Name} message={message}";
        TryAppendJournal("run-events-maintenance-failed", line);
        try { Console.WriteLine(line); }
        catch { /* A closed output stream must not fault the maintenance task. */ }
    }

    private void TryAppendJournal(string eventKind, string detail)
    {
        try
        {
            // Writer construction can perform IO too; keep it on the background thread.
            new ConductEventLogWriter(conductEventsLogPath).Append(eventKind, null, detail);
        }
        catch
        {
            // Match the cadence's best-effort journal contract.
        }
    }
}
