using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private AcceptanceEngineMainSuspectRelease? _mainSuspectRelease;
    private Task<PostLandingCanaryDisposition>? _mainSuspectReleaseTask;

    internal ConductorBatchLoop WithMainSuspectRelease(AcceptanceEngineMainSuspectRelease release)
    {
        _mainSuspectRelease = release;
        return this;
    }

    private bool ServiceMainSuspectRelease(ConductorDriver driver,
        List<Task<PostLandingCanaryDisposition>> tasks, object gate)
    {
        if (_mainSuspectReleaseTask is { IsCompleted: false }) return false;
        if (_mainSuspectRelease is null)
        {
            if (_postLandingCanary is null || _workspace is null || _acceptanceEngineCircuit is null) return false;
            var events = new PostLandingCanaryEventStore(new SqliteRunEventStore(_workspace.RunEventStorePath),
                _workspace.RunEventStorePath);
            _mainSuspectRelease = new(events, _acceptanceEngineCircuit,
                new LocalMainTipReader(_workspace.ExecutionDirectory, _workspace.IntegrationBranch), driver.CreateMainSuspectReleaseProbe(), LogReleaseProgress);
        }
        // Eligibility and the potentially minutes-long probe run off the tick thread.
        // The tracked wrapper never faults, so existing canary drains also cover release work.
        _mainSuspectReleaseTask = Task.Run(async () =>
        {
            try { await _mainSuspectRelease.CheckAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                try { LogReleaseProgress($"CANARY_GATE sha=unknown result=release-probe-deferred reason=check-{ex.GetType().Name}"); }
                catch { /* A logging fault must not fault the tracked drain task. */ }
            }
            return PostLandingCanaryDisposition.NotTriggered;
        });
        lock (gate) tasks.Add(_mainSuspectReleaseTask);
        return true;
    }

    private void LogReleaseProgress(string line)
    {
        EmitProgress(line);
        if (_workspace is not null)
            new ConductEventLogWriter(_workspace.ConductEventsLogPath).Append("canary-gate", null, line);
    }

    private sealed class LocalMainTipReader(string root, string integrationBranch) : IMainTipReader
    {
        public string? ReadMainTip() => GoalWorktrees.ResolveRequiredRef(root, $"refs/heads/{integrationBranch}");
    }
}
