namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owned by one loop generation. Stop and handoff deliberately never join its background run.
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
                    var message = string.IsNullOrWhiteSpace(ex.Message)
                        ? "none"
                        : ex.Message.ReplaceLineEndings(" ").Replace(' ', '_');
                    var line = $"RUN_EVENTS_MAINTENANCE_FAILED exception={ex.GetType().Name} message={message}";
                    TryAppendJournal("run-events-maintenance-failed", line);
                    try { Console.WriteLine(line); }
                    catch { /* A closed output stream must not fault the maintenance task. */ }
                }
            });
            return true;
        }
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
