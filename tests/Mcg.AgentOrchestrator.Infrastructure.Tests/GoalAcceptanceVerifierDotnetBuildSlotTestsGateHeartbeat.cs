using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsGateHeartbeat : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_gate_heartbeat_surfaces_hung_child_without_process_inspection")]
    public async Task GoalAcceptanceVerifierGateHeartbeatSurfacesHungChildWithoutProcessInspection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "hung gate receipt", "type": "command", "command": "powershell", "arguments": ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"], "timeoutMinutes": 1 }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var previousHeartbeat = TestOverrides.HeartbeatInterval;
        var previousProgress = TestOverrides.ProgressInterval;
        var progress = new ConcurrentQueue<AcceptanceGateProgress>();
        var targetObserved = new TaskCompletionSource<AcceptanceGateProgress>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            TryDeleteStableSlotHeartbeat(0);
            TestOverrides.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
            TestOverrides.ProgressInterval = TimeSpan.FromMilliseconds(200);
            var verifier = new GoalAcceptanceVerifier(TestOverrides);
            var goalId = new GoalId("feedfacefeedfacefeedfacefeedface");
            using var cts = new CancellationTokenSource();
            Action<AcceptanceGateProgress> progressSink = item =>
            {
                progress.Enqueue(item);
                if (item.CurrentTarget == "hung gate receipt")
                    targetObserved.TrySetResult(item);
            };
            var run = verifier.RunOwnedAsync(
                root, goalId, null, 0, null, cts.Token,
                new AcceptanceRunExecutionOptions(ProgressSink: progressSink));

            _ = await GateHeartbeatProgressFailsafe.WaitForTargetAsync(
                targetObserved.Task,
                "hung gate receipt",
                cts,
                run,
                Task.Delay(TimeSpan.FromMinutes(2)));

            var heartbeatPath = Assert.Single(
                progress
                    .Where(item => item.CurrentTarget == "hung gate receipt")
                    .Select(item => item.HeartbeatPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

            Assert.True(File.Exists(heartbeatPath), $"Missing attempt heartbeat '{heartbeatPath}'.");
            var snapshot = JsonSerializer.Deserialize<GateHeartbeatSnapshot>(
                File.ReadAllText(heartbeatPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(snapshot);
            Assert.Equal("feedfacefeedfacefeedfacefeedface", snapshot!.GoalId);
            Assert.Equal("verification-check", snapshot.Phase);
            Assert.Equal("hung gate receipt", snapshot.CurrentTarget);
            Assert.True(snapshot.ChildPid.HasValue || snapshot.State is "completed" or "timed-out");
            Assert.True(DateTimeOffset.UtcNow - snapshot.LastProgressAt >= TimeSpan.Zero);
            Assert.Contains(progress, item => item.GoalId == goalId.Value && item.CurrentTarget == "hung gate receipt");
        }
        finally
        {
            TestOverrides.HeartbeatInterval = previousHeartbeat;
            TestOverrides.ProgressInterval = previousProgress;
            try { DeleteDirectoryWithRetry(root); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_gate_heartbeat_progress_comes_from_live_visible_output")]
    public async Task GoalAcceptanceVerifierGateHeartbeatProgressComesFromLiveVisibleOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "live output gate receipt", "type": "command", "command": "powershell", "arguments": ["-NoProfile", "-Command", "Write-Output 'heartbeat-visible-output'; Start-Sleep -Seconds 30"], "timeoutMinutes": 1 }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var previousHeartbeat = TestOverrides.HeartbeatInterval;
        var previousProgress = TestOverrides.ProgressInterval;
        var previousCapturePublication = TestOverrides.CapturePublicationInterval;
        var observed = new ConcurrentQueue<AcceptanceGateProgress>();
        var commandStarted = new TaskCompletionSource<AcceptanceGateProgress>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var outputObserved = new TaskCompletionSource<AcceptanceGateProgress>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unchangedOutputObserved = new TaskCompletionSource<AcceptanceGateProgress>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task<AcceptanceVerificationResult>? run = null;
        AcceptanceGateProgress? firstOutput = null;
        try
        {
            TryDeleteStableSlotHeartbeat(0);
            TestOverrides.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
            TestOverrides.ProgressInterval = TimeSpan.FromMilliseconds(200);
            TestOverrides.CapturePublicationInterval = TimeSpan.FromMilliseconds(100);
            Action<AcceptanceGateProgress> progressSink = item =>
            {
                observed.Enqueue(item);
                if (item.CurrentTarget != "live output gate receipt" ||
                    item.ChildProcessId is null)
                {
                    return;
                }

                commandStarted.TrySetResult(item);
                if (item.OutputBytes <= 0)
                {
                    return;
                }

                var prior = Interlocked.CompareExchange(ref firstOutput, item, null);
                if (prior is null)
                {
                    outputObserved.TrySetResult(item);
                }
                else if (item.OutputBytes == prior.OutputBytes &&
                         item.LastObservedAt > prior.LastObservedAt)
                {
                    unchangedOutputObserved.TrySetResult(item);
                }
            };
            var verifier = new GoalAcceptanceVerifier(TestOverrides);
            run = verifier.RunOwnedAsync(
                root,
                new GoalId("decafbaddecafbaddecafbaddecafbad"),
                changedFiles: null,
                stableSlotIndex: 0,
                stableSlotLease: null,
                cancellationToken: cancellation.Token,
                executionOptions: new AcceptanceRunExecutionOptions(ProgressSink: progressSink));

            await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var first = await outputObserved.Task.WaitAsync(TimeSpan.FromSeconds(6));
            var unchanged = await unchangedOutputObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Xunit.Assert.NotNull(first.ChildProcessId);
            Xunit.Assert.True(first.OutputBytes > 0);
            Xunit.Assert.Equal(first.OutputBytes, unchanged.OutputBytes);
            Xunit.Assert.Equal(first.LastProgressAt, unchanged.LastProgressAt);
            Xunit.Assert.True(unchanged.LastObservedAt > first.LastObservedAt);

            cancellation.Cancel();
            await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Xunit.Assert.Contains(observed, item =>
                item.CurrentTarget == "live output gate receipt" &&
                item.ChildProcessId.HasValue &&
                item.OutputBytes > 0);
        }
        finally
        {
            cancellation.Cancel();
            if (run is not null)
            {
                try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            TestOverrides.HeartbeatInterval = previousHeartbeat;
            TestOverrides.ProgressInterval = previousProgress;
            TestOverrides.CapturePublicationInterval = previousCapturePublication;
            try { DeleteDirectoryWithRetry(root); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_gate_heartbeat_mirrors_are_run_scoped_and_terminal_cleanup_preserves_sibling")]
    public void GoalAcceptanceVerifierGateHeartbeatMirrorsAreRunScopedAndTerminalCleanupPreservesSibling()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-gate-status-mirror-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var firstGoalId = new GoalId("abcdef01abcdef01abcdef01abcdef01");
        var secondGoalId = new GoalId("12345678123456781234567812345678");
        const int buildSlot = 0;
        var environment = new DotnetBuildEnvironment(
            "goal-mirror",
            root,
            Path.Combine(root, "artifacts"),
            Path.Combine(root, "build-slots", $"build-{buildSlot}.lock"),
            [],
            "goal-mirror",
            BuildPermitIndex: buildSlot);
        var stdoutPath = Path.Combine(root, "run.out");
        var stderrPath = Path.Combine(root, "run.err");
        var childPid = Environment.ProcessId;

        try
        {
            TryDeleteStableSlotHeartbeat(buildSlot);

            // The attempt-results prefix is exactly what made gate-status structurally blind: it forces the
            // PRIMARY heartbeat onto an attempt-scoped path that GateHeartbeatArtifacts.ReadStableSlots never
            // reads.
            string firstPrimaryPath;
            string? firstMirrorPath;
            var firstAttemptPrefix = Path.Combine(root, "attempt-owner-a");
            (firstPrimaryPath, firstMirrorPath) = GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                "infrastructure lane",
                firstGoalId,
                environment,
                childPid,
                stdoutPath,
                stderrPath,
                attemptResultsPrefix: firstAttemptPrefix);

            var expectedStableSlotPath = GateHeartbeatArtifacts.GetStableSlotPath(buildSlot);

            // Blindness precondition: with the attempt prefix active the primary heartbeat is attempt-scoped
            // and is NOT the stable slot path gate-status reads.
            Assert.StartsWith(
                Path.Combine(root, "attempt-owner"),
                firstPrimaryPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(expectedStableSlotPath, firstPrimaryPath);

            Assert.Equal(
                GateHeartbeatArtifacts.GetRunScopedStableSlotPath(buildSlot, firstPrimaryPath),
                firstMirrorPath);
            Assert.True(
                File.Exists(firstMirrorPath),
                $"Run-scoped heartbeat mirror missing: {firstMirrorPath}");

            string secondPrimaryPath;
            string? secondMirrorPath;
            var secondAttemptPrefix = Path.Combine(root, "attempt-owner-b");
            (secondPrimaryPath, secondMirrorPath) = GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                "infrastructure lane",
                secondGoalId,
                environment,
                childPid + 1,
                stdoutPath,
                stderrPath,
                attemptResultsPrefix: secondAttemptPrefix);
            Assert.NotEqual(firstMirrorPath, secondMirrorPath);

            var runningSlots = GateHeartbeatArtifacts.ReadStableSlots()
                .Where(status => status.SlotIndex == buildSlot)
                .ToArray();
            Assert.Equal(2, runningSlots.Length);
            Assert.Contains(runningSlots, status =>
                status.Snapshot?.GoalId == firstGoalId.Value &&
                status.Snapshot.State == "running");
            Assert.Contains(runningSlots, status =>
                status.Snapshot?.GoalId == secondGoalId.Value &&
                status.Snapshot.State == "running");

            GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                "infrastructure lane",
                firstGoalId,
                environment,
                childPid,
                stdoutPath,
                stderrPath,
                finalState: "completed",
                attemptResultsPrefix: firstAttemptPrefix);

            Assert.False(File.Exists(firstMirrorPath));
            var remainingSlot = GateHeartbeatArtifacts.ReadStableSlots()
                .Single(status => status.SlotIndex == buildSlot);
            Assert.Equal(secondMirrorPath, remainingSlot.Path);
            Assert.Equal("running", remainingSlot.Snapshot?.State);
            Assert.Equal(secondGoalId.Value, remainingSlot.Snapshot?.GoalId);

            GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                "infrastructure lane",
                secondGoalId,
                environment,
                childPid,
                stdoutPath,
                stderrPath,
                finalState: "completed",
                attemptResultsPrefix: secondAttemptPrefix);

            string? orphanMirrorPath;
            (_, orphanMirrorPath) = GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                "infrastructure lane",
                secondGoalId,
                environment,
                int.MaxValue,
                stdoutPath,
                stderrPath,
                attemptResultsPrefix: Path.Combine(root, "attempt-owner-orphan"));

            Assert.NotNull(orphanMirrorPath);
            Assert.True(File.Exists(orphanMirrorPath));
            var afterOrphanPrune = GateHeartbeatArtifacts.ReadStableSlots()
                .Single(status => status.SlotIndex == buildSlot);
            Assert.Equal("missing", afterOrphanPrune.UnavailableReason);
            Assert.False(File.Exists(orphanMirrorPath));
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(buildSlot);
            try { DeleteDirectoryWithRetry(root); } catch { }
        }
    }
}
