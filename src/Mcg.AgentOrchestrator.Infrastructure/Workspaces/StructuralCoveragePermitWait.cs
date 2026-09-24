using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class StructuralCoveragePermitWait
{
    internal const string PhaseName = "structural-coverage-permit-wait";
    internal const string UnavailableReasonCode = "structural-coverage-permit-unavailable";
    internal static readonly TimeSpan DefaultBound = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(30);

    internal static DotnetBuildEnvironmentLease Acquire(
        DotnetBuildEnvironment environment,
        GoalId? goalId,
        DotnetBuildStorageRoot storageRoot,
        GoalAcceptanceVerifierTestOverrides overrides,
        Action<AcceptanceGateProgress> reportProgress,
        TimeProvider clock,
        Action<TimeSpan> sleep,
        AcceptanceAttemptArtifactCustodyContext? artifactCustody,
        CancellationToken cancellationToken)
    {
        var bound = overrides.StructuralCoveragePermitWaitBound ?? DefaultBound;
        var interval = overrides.StructuralCoveragePermitWaitHeartbeatInterval ?? DefaultHeartbeatInterval;
        if (bound <= TimeSpan.Zero || bound > DefaultBound || interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(bound), "Permit wait must be positive and at most three minutes.");

        var started = clock.GetUtcNow();
        var nextHeartbeat = started;
        var slot = environment.BuildPermitIndex;
        var heartbeatPath = slot.HasValue
            ? GateHeartbeatArtifacts.GetRunScopedStableSlotPath(slot.Value, $"{environment.LeaseId}-{Guid.NewGuid():N}", storageRoot)
            : GateHeartbeatArtifacts.GetManualPath(environment.RootPath);
        try
        {
            return DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(
                environment,
                cancellationToken,
                clock,
                duration =>
                {
                    var now = clock.GetUtcNow();
                    if (now >= nextHeartbeat)
                    {
                        var target = $"permit=build-{slot?.ToString() ?? "unknown"} lease={environment.LeaseId}";
                        var elapsed = now - started;
                        var snapshot = new GateHeartbeatSnapshot(
                            goalId?.Value, PhaseName, target, slot, Environment.ProcessId, null,
                            "running", started, now, now, 0, 0, 0);
                        GateHeartbeatArtifacts.TryWrite(heartbeatPath, snapshot);
                        var progress = new AcceptanceGateProgress(
                            goalId?.Value, PhaseName, target, slot, Environment.ProcessId, null,
                            started, now, now, elapsed, 0, heartbeatPath);
                        reportProgress(progress);
                        overrides.OnStructuralCoveragePermitWaitForTests?.Invoke(progress);
                        nextHeartbeat = now + interval;
                    }
                    sleep(duration);
                },
                artifactCustody,
                timeout: bound);
        }
        catch (DotnetBuildSlotsBusyException ex)
        {
            var holder = ex.SlotsBusy.BusySlots.FirstOrDefault(wait => wait.SlotIndex == slot)?.OwnerProcessId;
            throw new AcceptanceInfrastructureDeferredException(
                UnavailableReasonCode,
                exitCode: null,
                outputTail: $"permit=build-{slot?.ToString() ?? "unknown"} path={environment.ExecutionLockPath} " +
                    $"bound={bound} holder=pid-{holder?.ToString() ?? "unknown"}");
        }
        finally
        {
            try { File.Delete(heartbeatPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
