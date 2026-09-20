using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsHeartbeatFailsafe
    : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact(
        DisplayName = "GoalAcceptanceVerifier_gate_heartbeat_failsafe_cancels_and_awaits_child_when_heartbeat_write_is_suppressed",
        Timeout = 150_000)]
    public async Task GateHeartbeatFailsafeCancelsAndAwaitsChildWhenHeartbeatWriteIsSuppressed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string missingTarget = "suppressed heartbeat receipt";
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "suppressed heartbeat receipt", "type": "command", "command": "powershell", "arguments": ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"], "timeoutMinutes": 1 }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var previousHeartbeat = TestOverrides.HeartbeatInterval;
        var previousProgress = TestOverrides.ProgressInterval;
        var targetSignal = new TaskCompletionSource<AcceptanceGateProgress>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var triggerFailsafe = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task<AcceptanceVerificationResult>? run = null;
        try
        {
            TryDeleteStableSlotHeartbeat(0);
            TestOverrides.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
            TestOverrides.ProgressInterval = TimeSpan.FromMilliseconds(200);
            Action<AcceptanceGateProgress> suppressHeartbeatWrite = item =>
            {
                if (item.CurrentTarget == missingTarget && item.ChildProcessId.HasValue)
                {
                    // Negative control: the target heartbeat occurred, but its completion signal is
                    // deliberately suppressed so the failsafe branch must own cancellation and drain.
                    triggerFailsafe.TrySetResult();
                }
            };
            var verifier = new GoalAcceptanceVerifier(TestOverrides);
            run = verifier.RunOwnedAsync(
                root,
                new GoalId("dad1fadedad1fadedad1fadedad1fade"),
                changedFiles: null,
                stableSlotIndex: 0,
                stableSlotLease: null,
                cancellationToken: cancellation.Token,
                executionOptions: new AcceptanceRunExecutionOptions(ProgressSink: suppressHeartbeatWrite));

            var error = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() =>
                GateHeartbeatProgressFailsafe.WaitForTargetAsync(
                    targetSignal.Task,
                    missingTarget,
                    cancellation,
                    run,
                    triggerFailsafe.Task));

            Assert.Contains(missingTarget, error.Message, StringComparison.Ordinal);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.True(run.IsCompleted, "The verifier run returned before its child exit was awaited.");
            DeleteDirectoryWithRetry(root);
            Assert.False(Directory.Exists(root), $"Failsafe retained temp root '{root}'.");
        }
        finally
        {
            cancellation.Cancel();
            if (run is not null)
            {
                try { await run; } catch (OperationCanceledException) { }
            }
            TestOverrides.HeartbeatInterval = previousHeartbeat;
            TestOverrides.ProgressInterval = previousProgress;
            try { DeleteDirectoryWithRetry(root); } catch { }
        }
    }
}

internal static class GateHeartbeatProgressFailsafe
{
    internal static async Task<AcceptanceGateProgress> WaitForTargetAsync(
        Task<AcceptanceGateProgress> targetSignal,
        string target,
        CancellationTokenSource cancellation,
        Task run,
        Task failsafe)
    {
        var completed = await Task.WhenAny(targetSignal, failsafe);
        if (completed == targetSignal)
            return await targetSignal;

        cancellation.Cancel();
        try
        {
            await run;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // RunOwnedAsync completes cancellation only after its owned child has exited.
        }

        throw new Xunit.Sdk.XunitException(
            $"Missing progress target '{target}' before the gate heartbeat failsafe; " +
            "the run was cancelled and child exit was awaited.");
    }
}
