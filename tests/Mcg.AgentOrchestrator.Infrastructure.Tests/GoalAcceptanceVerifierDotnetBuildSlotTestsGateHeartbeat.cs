using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
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
        var previousHeartbeat = GoalAcceptanceVerifier.HeartbeatInterval;
        var previousProgress = GoalAcceptanceVerifier.ProgressInterval;
        var progress = new List<AcceptanceGateProgress>();
        try
        {
            TryDeleteStableSlotHeartbeat(0);
            GoalAcceptanceVerifier.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
            GoalAcceptanceVerifier.ProgressInterval = TimeSpan.FromMilliseconds(200);
            var verifier = new GoalAcceptanceVerifier();
            var goalId = new GoalId("feedfacefeedfacefeedfacefeedface");
            using var sink = GoalAcceptanceVerifier.PushGateProgressSink(progress.Add);
            using var cts = new CancellationTokenSource();
            var run = verifier.RunAsync(root, goalId, stableSlotIndex: 0, cancellationToken: cts.Token);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!progress.Any(item => item.CurrentTarget == "hung gate receipt") &&
                   DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

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
            GoalAcceptanceVerifier.HeartbeatInterval = previousHeartbeat;
            GoalAcceptanceVerifier.ProgressInterval = previousProgress;
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
            using (GoalAcceptanceVerifier.PushAcceptanceAttemptResultsPrefix(
                Path.Combine(root, "attempt-owner-a")))
            {
                (firstPrimaryPath, firstMirrorPath) = GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                    "infrastructure lane",
                    firstGoalId,
                    environment,
                    childPid,
                    stdoutPath,
                    stderrPath);
            }

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
            using (GoalAcceptanceVerifier.PushAcceptanceAttemptResultsPrefix(
                Path.Combine(root, "attempt-owner-b")))
            {
                (secondPrimaryPath, secondMirrorPath) = GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                    "infrastructure lane",
                    secondGoalId,
                    environment,
                    childPid + 1,
                    stdoutPath,
                    stderrPath);
            }
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

            using (GoalAcceptanceVerifier.PushAcceptanceAttemptResultsPrefix(
                Path.Combine(root, "attempt-owner-a")))
            {
                GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                    "infrastructure lane",
                    firstGoalId,
                    environment,
                    childPid,
                    stdoutPath,
                    stderrPath,
                    finalState: "completed");
            }

            Assert.False(File.Exists(firstMirrorPath));
            var remainingSlot = GateHeartbeatArtifacts.ReadStableSlots()
                .Single(status => status.SlotIndex == buildSlot);
            Assert.Equal(secondMirrorPath, remainingSlot.Path);
            Assert.Equal("running", remainingSlot.Snapshot?.State);
            Assert.Equal(secondGoalId.Value, remainingSlot.Snapshot?.GoalId);

            using (GoalAcceptanceVerifier.PushAcceptanceAttemptResultsPrefix(
                Path.Combine(root, "attempt-owner-b")))
            {
                GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                    "infrastructure lane",
                    secondGoalId,
                    environment,
                    childPid,
                    stdoutPath,
                    stderrPath,
                    finalState: "completed");
            }

            string? orphanMirrorPath;
            using (GoalAcceptanceVerifier.PushAcceptanceAttemptResultsPrefix(
                Path.Combine(root, "attempt-owner-orphan")))
            {
                (_, orphanMirrorPath) = GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                    "infrastructure lane",
                    secondGoalId,
                    environment,
                    int.MaxValue,
                    stdoutPath,
                    stderrPath);
            }

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
