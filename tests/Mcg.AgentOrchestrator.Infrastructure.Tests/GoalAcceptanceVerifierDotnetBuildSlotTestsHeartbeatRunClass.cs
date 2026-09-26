using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsHeartbeatRunClass : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public void LegacyStableSlotHeartbeatWithoutRunClassStillCounts()
    {
        const int slot = 0;
        const string goalId = "abcdef12abcdef12abcdef12abcdef12";
        var path = GateHeartbeatArtifacts.GetRunScopedStableSlotPath(slot, Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        var snapshot = new GateHeartbeatSnapshot(
            goalId, "verification-check", "legacy", slot, Environment.ProcessId,
            Environment.ProcessId, "running", now, now, now, 0, 0, 0);
        try
        {
            GateHeartbeatArtifacts.Write(path, snapshot);
            Assert.DoesNotContain("runClass", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

            var read = GateHeartbeatArtifacts.ReadStableSlots().Single(status => status.Path == path);
            Assert.True(read.IsAvailable);
            Assert.Null(read.Snapshot!.RunClass);
            Assert.Contains(GateLoadContextProbe.ReadLiveGateOccupants(), occupant =>
                occupant.GoalId == goalId && occupant.CountsAsAcceptanceOccupant);
        }
        finally
        {
            GateHeartbeatArtifacts.TryDelete(path);
        }
    }

    [Xunit.Fact]
    public void FocusedRunClassSurvivesStableSlotMirror()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-heartbeat-run-class-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var goalId = new GoalId("abcdef12abcdef12abcdef12abcdef12");
        const int slot = 0;
        var environment = new DotnetBuildEnvironment(
            "goal-run-class", root, Path.Combine(root, "artifacts"),
            Path.Combine(root, "build-slots", "build-0.lock"), [],
            "goal-run-class", BuildPermitIndex: slot);
        string? primaryPath = null;
        string? mirrorPath = null;
        try
        {
            (primaryPath, mirrorPath) = GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                "focused lane", goalId, environment, Environment.ProcessId,
                Path.Combine(root, "run.out"), Path.Combine(root, "run.err"),
                attemptResultsPrefix: Path.Combine(root, "focused-attempt"),
                runClass: GateHeartbeatRunClass.FocusedEvidence);

            Assert.NotNull(mirrorPath);
            Assert.Contains(GateHeartbeatArtifacts.ReadStableSlots(), status =>
                status.Path == mirrorPath &&
                status.Snapshot?.RunClass == GateHeartbeatRunClass.FocusedEvidence);
            Assert.Contains(GateLoadContextProbe.ReadLiveGateOccupants(), occupant =>
                occupant.GoalId == goalId.Value && !occupant.CountsAsAcceptanceOccupant);
        }
        finally
        {
            if (primaryPath is not null) GateHeartbeatArtifacts.TryDelete(primaryPath);
            if (mirrorPath is not null) GateHeartbeatArtifacts.TryDelete(mirrorPath);
            try { DeleteDirectoryWithRetry(root); } catch { }
        }
    }

    [Xunit.Fact]
    public async Task ClassifierUnwrapsBothKindsOfExecutionOwner()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-owner-run-class-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(GoalAcceptanceVerifier.ClassifyGateHeartbeatRunClassForTests(null));
            await using var focused = AcceptanceExecutionOwners.CreateFocusedVerification(
                root, options: new AcceptanceRunExecutionOptions(
                    ResultsPrefix: Path.Combine(root, "focused")));
            var focusedContext = (IAcceptanceRunExecutionContext)focused;
            Assert.Equal(GateHeartbeatRunClass.FocusedEvidence,
                GoalAcceptanceVerifier.ClassifyGateHeartbeatRunClassForTests(focusedContext));
            Assert.Equal(GateHeartbeatRunClass.FocusedEvidence,
                GoalAcceptanceVerifier.ClassifyGateHeartbeatRunClassForTests(
                    new AcceptanceRunExecutionContextView(focusedContext, Path.Combine(root, "focused-arm"))));

            await using var attempt = AcceptanceExecutionOwners.CreateAttempt(
                root, options: new AcceptanceRunExecutionOptions(
                    ResultsPrefix: Path.Combine(root, "acceptance")));
            Assert.Equal(GateHeartbeatRunClass.Acceptance,
                GoalAcceptanceVerifier.ClassifyGateHeartbeatRunClassForTests(
                    new AcceptanceRunExecutionContextView(
                        (IAcceptanceRunExecutionContext)attempt, Path.Combine(root, "acceptance-arm"))));
        }
        finally
        {
            try { DeleteDirectoryWithRetry(root); } catch { }
        }
    }
}
