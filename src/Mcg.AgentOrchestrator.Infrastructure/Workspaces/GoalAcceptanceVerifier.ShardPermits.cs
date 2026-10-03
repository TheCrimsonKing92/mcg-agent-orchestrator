namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private GateShardPermitPool? _shardPermitPool;

    private GateShardLaneClass ShardPermitLaneClass => ClassifyShardPermitLaneClass(_executionContext);

    internal GateShardLaneClass ShardPermitLaneClassForTests => ShardPermitLaneClass;

    internal static GateShardLaneClass ClassifyShardPermitLaneClass(IAcceptanceRunExecutionContext? context) =>
        GateHeartbeatRunClass.Classify(context) == GateHeartbeatRunClass.FocusedEvidence
            ? GateShardLaneClass.Evidence
            : GateShardLaneClass.Gate;

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
                GateShardPermitPool.ResolveBudget(Environment.GetEnvironmentVariable(GateShardPermitPool.GateShardBudgetVariable)),
            GateShardProcessFacts.System, _timeProvider);
        AcceptanceGatePhaseAccountant.RecordCurrentShardPermitBudget(pool.Budget);
        var started = _timeProvider.GetTimestamp();
        var waiting = false;
        var nextProgress = _timeProvider.GetUtcNow();
        var progressInterval = _testOverrides.ProgressInterval ?? TimeSpan.FromSeconds(30);
        GateShardPermit permit;
        try
        {
            permit = await pool.AcquireAsync(ShardPermitLaneClass, outcome =>
            {
                if (outcome != GateShardPollOutcome.AcquiredAfterForcedYield)
                {
                    waiting = true;
                    _testOverrides.OnShardPermitWaitingForTests?.Invoke(heartbeatContext.CurrentTarget);
                }
                var now = _timeProvider.GetUtcNow();
                if (now < nextProgress && outcome != GateShardPollOutcome.AcquiredAfterForcedYield)
                    return;
                var elapsed = _timeProvider.GetElapsedTime(started);
                GateHeartbeatArtifacts.TryWrite(heartbeatContext.HeartbeatPath,
                    new GateHeartbeatSnapshot(heartbeatContext.GoalId, "shard-permit-wait",
                        heartbeatContext.CurrentTarget, heartbeatContext.SlotIndex,
                        Environment.ProcessId, null, "running", now - elapsed, now, now, 0, 0, 0,
                        RunId: heartbeatContext.RunId));
                EmitGateProgress(new AcceptanceGateProgress(
                    heartbeatContext.GoalId, "shard-permit-wait", heartbeatContext.CurrentTarget,
                    heartbeatContext.SlotIndex, Environment.ProcessId, null,
                    now - elapsed, now, now, elapsed, 0, heartbeatContext.HeartbeatPath),
                    GateShardLanePriorityProgress.Format(ShardPermitLaneClass, outcome));
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
