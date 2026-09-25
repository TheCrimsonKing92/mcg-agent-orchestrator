namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private GateShardPermitPool? _shardPermitPool;

    private async Task<CommandResult> RunLaneTestHostWithShardPermitAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan timeout,
        GateHeartbeatContext heartbeatContext,
        CancellationToken cancellationToken,
        bool isMtpLane = false)
    {
        if ((!_requiresTestTelemetryReceipt && _testOverrides.ShardPermitRootForTests is null) ||
            (_testOverrides.ShardPermitRootForTests is null &&
             !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_GATE_INVOCATION_ID"))) ||
            (!isMtpLane && !IsDotnetTestCommand(arguments)))
        {
            return await RunWithGateHeartbeatAsync(
                arguments, workingDirectory, timeout, heartbeatContext, cancellationToken).ConfigureAwait(false);
        }

        var pool = _shardPermitPool ??= GateShardPermitPool.ForRoot(
            _testOverrides.ShardPermitRootForTests ?? _storageRoot.RootPath,
            _testOverrides.ResolveGateShardBudgetForTests?.Invoke() ??
                GateShardPermitPool.ResolveBudget(Environment.GetEnvironmentVariable(GateShardPermitPool.GateShardBudgetVariable)));
        AcceptanceGatePhaseAccountant.RecordCurrentShardPermitBudget(pool.Budget);
        var started = _timeProvider.GetTimestamp();
        var waiting = false;
        var nextProgress = _timeProvider.GetUtcNow();
        var progressInterval = _testOverrides.ProgressInterval ?? TimeSpan.FromSeconds(30);
        GateShardPermit permit;
        try
        {
            permit = await pool.AcquireAsync(() =>
            {
                waiting = true;
                _testOverrides.OnShardPermitWaitingForTests?.Invoke(heartbeatContext.CurrentTarget);
                var now = _timeProvider.GetUtcNow();
                if (now < nextProgress)
                    return;
                var elapsed = _timeProvider.GetElapsedTime(started);
                GateHeartbeatArtifacts.TryWrite(heartbeatContext.HeartbeatPath,
                    new GateHeartbeatSnapshot(heartbeatContext.GoalId, "shard-permit-wait",
                        heartbeatContext.CurrentTarget, heartbeatContext.SlotIndex,
                        Environment.ProcessId, null, "running", now - elapsed, now, now, 0, 0, 0));
                EmitGateProgress(new AcceptanceGateProgress(
                    heartbeatContext.GoalId, "shard-permit-wait", heartbeatContext.CurrentTarget,
                    heartbeatContext.SlotIndex, Environment.ProcessId, null,
                    now - elapsed, now, now, elapsed, 0, heartbeatContext.HeartbeatPath));
                nextProgress = now + progressInterval;
            }, _testOverrides.ShardPermitPollInterval ?? TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (waiting)
                AcceptanceGatePhaseAccountant.RecordCurrentShardPermitWait(_timeProvider.GetElapsedTime(started));
        }
        try
        {
            _testOverrides.OnShardPermitAcquiredForTests?.Invoke(heartbeatContext.CurrentTarget);
            return await RunWithGateHeartbeatAsync(
                arguments, workingDirectory, timeout, heartbeatContext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            permit.Dispose();
            _testOverrides.OnShardPermitReleasedForTests?.Invoke(heartbeatContext.CurrentTarget);
        }
    }
}
