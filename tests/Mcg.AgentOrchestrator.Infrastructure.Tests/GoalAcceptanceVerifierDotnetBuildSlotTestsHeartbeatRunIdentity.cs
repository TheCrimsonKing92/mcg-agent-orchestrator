using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsHeartbeatRunIdentity : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public void LegacyJsonWithoutRunIdLoadsAsNullAndRoundTrips()
    {
        using var isolatedRoot = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        var path = GateHeartbeatArtifacts.GetStableSlotPath(0);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string legacyJson = """
            {"goalId":null,"phase":"verification-check","currentTarget":"legacy","slotIndex":0,
             "processId":4300,"childPid":null,"state":"running",
             "startedAt":"2026-10-03T22:00:00Z","lastObservedAt":"2026-10-03T22:00:00Z",
             "lastProgressAt":"2026-10-03T22:00:00Z","stdoutBytes":0,"stderrBytes":0,"outputBytes":0,
             "runClass":"acceptance"}
            """;
        var observedAt = new DateTimeOffset(2026, 10, 3, 22, 0, 0, TimeSpan.Zero);
        try
        {
            File.WriteAllText(path, legacyJson);
            var read = GateHeartbeatArtifacts.ReadStableSlot(0, observedAt);
            Assert.True(read.IsAvailable, read.UnavailableReason);
            Assert.Null(read.Snapshot!.RunId);
            GateHeartbeatArtifacts.Write(path, read.Snapshot);
            Assert.DoesNotContain("runId", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            Assert.Null(GateHeartbeatArtifacts.ReadStableSlot(0, observedAt).Snapshot!.RunId);
        }
        finally
        {
            GateHeartbeatArtifacts.TryDelete(path);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task GroupedGateRunIdentitySurvivesHeartbeatAndOccupantReaders(bool train)
    {
        using var isolatedRoot = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        var root = Path.Combine(Path.GetTempPath(), $"mcg-heartbeat-run-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var memberIds = new[] { new string('b', 32), new string('a', 32) };
        var runId = train ? ConductorDriver.TrainGateRunIdentity(memberIds)
            : ConductorDriver.CohortGateRunIdentity(memberIds);
        var environment = new DotnetBuildEnvironment(
            "grouped-run", root, Path.Combine(root, "artifacts"),
            Path.Combine(root, "build-slots", "build-0.lock"), [], "grouped-run", BuildPermitIndex: 0);
        string? primaryPath = null;
        string? mirrorPath = null;
        try
        {
            await using var owner = AcceptanceExecutionOwners.CreateAttempt(root,
                options: new AcceptanceRunExecutionOptions(RunId: "owner-attempt",
                    ResultsPrefix: Path.Combine(root, "acceptance"), GateRunIdentity: runId));
            var context = new AcceptanceRunExecutionContextView(
                (IAcceptanceRunExecutionContext)owner, Path.Combine(root, "arm"));
            var resolved = GoalAcceptanceVerifier.ResolveGateHeartbeatRunIdForTests(context);
            Assert.Equal(runId, resolved);
            (primaryPath, mirrorPath) = GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                "grouped lane", null, environment, Environment.ProcessId,
                Path.Combine(root, "run.out"), Path.Combine(root, "run.err"),
                attemptResultsPrefix: Path.Combine(root, "acceptance"),
                runClass: GateHeartbeatRunClass.Acceptance, runId: resolved);

            Assert.NotNull(mirrorPath);
            var snapshot = Assert.Single(GateHeartbeatArtifacts.ReadStableSlots(),
                status => status.Path == mirrorPath).Snapshot;
            Assert.Equal(runId, snapshot!.RunId);
            Assert.Null(snapshot.GoalId);
            Assert.Contains(GateLoadContextProbe.ReadLiveGateOccupants(), occupant =>
                occupant.SourcePath == mirrorPath && occupant.RunId == runId && occupant.CountsAsAcceptanceOccupant);
            Assert.Contains("\"runId\"", File.ReadAllText(mirrorPath!), StringComparison.Ordinal);
        }
        finally
        {
            if (primaryPath is not null) GateHeartbeatArtifacts.TryDelete(primaryPath);
            if (mirrorPath is not null) GateHeartbeatArtifacts.TryDelete(mirrorPath);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task SoloUsesAttemptIdAndFocusedOwnerNeverSuppliesRunIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-resolve-run-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(GoalAcceptanceVerifier.ResolveGateHeartbeatRunIdForTests(null));
            await using var solo = AcceptanceExecutionOwners.CreateAttempt(root,
                options: new AcceptanceRunExecutionOptions(RunId: "solo-attempt",
                    ResultsPrefix: Path.Combine(root, "solo")));
            Assert.Equal("solo-attempt", GoalAcceptanceVerifier.ResolveGateHeartbeatRunIdForTests(
                new AcceptanceRunExecutionContextView((IAcceptanceRunExecutionContext)solo, Path.Combine(root, "solo-arm"))));
            await using var focused = AcceptanceExecutionOwners.CreateFocusedVerification(root,
                options: new AcceptanceRunExecutionOptions(ResultsPrefix: Path.Combine(root, "focused"),
                    GateRunIdentity: "ignored-focused-id"));
            Assert.Null(GoalAcceptanceVerifier.ResolveGateHeartbeatRunIdForTests(
                new AcceptanceRunExecutionContextView((IAcceptanceRunExecutionContext)focused, Path.Combine(root, "focused-arm"))));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }
}
