using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its log, attempt files, heartbeat root, database and clock.
public sealed class ConductorInvariantObservationReaderTests
{
    [Fact]
    public void Read_RefusalsAndCompletedAttempts_SeparatesFaultsFromProgress()
    {
        using var fixture = new Fixture();
        var firstAt = fixture.Clock.Now;
        fixture.Refuse("main");
        fixture.Clock.Now = firstAt.AddMinutes(1);
        fixture.Refuse("candidate");
        var observation = fixture.Reader.Read(fixture.Goals, fixture.Clock.Now);
        Assert.Null(observation.LastGateProgressAt);
        Assert.Equal(new[] { firstAt, fixture.Clock.Now }, observation.Refusals.Select(item => item.At));
        Assert.All(observation.Refusals, item => Assert.Equal(fixture.MembersKey, item.RecordId));
        Assert.Equal(new[] { "identity-changed field=main", "identity-changed field=candidate" },
            observation.Refusals.Select(item => item.Reason));
        var refused = fixture.Coordinator.ReadAll().ToArray();
        Assert.Equal(2, refused.Length);
        Assert.All(refused, item => Assert.StartsWith("refused:", item.Detail));

        var completedAt = fixture.Clock.Now.AddMinutes(1);
        fixture.Clock.Now = completedAt;
        ConductorGroupedGateAttemptCoordinator.Save(fixture.NewAttempt() with
            { ReconciledAt = completedAt, Detail = "passed", Outcome = "Reconciled" });
        Assert.Equal(completedAt, fixture.Reader.Read(fixture.Goals, fixture.Clock.Now).LastGateProgressAt);
        fixture.Clock.Now = completedAt.AddMinutes(1);
        fixture.Heartbeat(fixture.Clock.Now);
        Assert.Equal(fixture.Clock.Now, fixture.Reader.Read(fixture.Goals, fixture.Clock.Now).LastGateProgressAt);
    }

    [Fact]
    public void Read_LatestHoldAndParkedStatus_SelectsOnlyCapacityWaiters()
    {
        using var fixture = new Fixture();
        var since = fixture.Clock.Now;
        fixture.Hold(0, "acceptance width 1 reached");
        fixture.Hold(1, "CohortCapacity");
        fixture.Hold(2, "Acceptance cohort gate owns this member");
        fixture.Hold(3, "acceptance width 1 reached");
        fixture.Hold(4, "acceptance width 1 reached");
        fixture.Goals[4] = (fixture.Goals[4].Id, GoalStatus.Parked);
        fixture.Clock.Now = since.AddMinutes(2);
        fixture.Hold(0, "acceptance width 1 reached");
        fixture.Hold(3, "waiting on dependency x");
        var ready = fixture.Reader.Read(fixture.Goals, fixture.Clock.Now).GateReadyGoals;
        Assert.Equal(fixture.Goals.Take(3).Select(goal => goal.Id.Value), ready.Select(goal => goal.GoalId));
        Assert.All(ready, goal => Assert.Equal(since, goal.ReadySince));
        fixture.Writer.Append("goal", null, $"GOAL goal={fixture.Goals[0].Id.Value[..8]} result=executed");
        Assert.DoesNotContain(fixture.Reader.Read(fixture.Goals, fixture.Clock.Now).GateReadyGoals,
            goal => goal.GoalId == fixture.Goals[0].Id.Value);
    }

    [Fact]
    public void Read_RotatedLogAndMalformedLines_PreservesReadyWindowAndRefusals()
    {
        using var fixture = new Fixture();
        fixture.Hold(0, "acceptance width 1 reached");
        fixture.Refuse("main");
        var since = fixture.Clock.Now;
        File.Move(fixture.Log, Path.Combine(fixture.Root, "conduct-events-20261017.log"));
        File.WriteAllText(fixture.Log, "{malformed GOAL goal=x\n");
        fixture.Clock.Now = since.AddMinutes(1);
        fixture.Hold(0, "acceptance width 1 reached");
        fixture.Refuse("main");
        var observation = fixture.Reader.Read(fixture.Goals, fixture.Clock.Now);
        Assert.Equal(since, Assert.Single(observation.GateReadyGoals).ReadySince);
        Assert.Equal(2, observation.Refusals.Count);
        Assert.Null(observation.LastGateProgressAt);
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "conductor-invariants", Guid.NewGuid().ToString("N"));
        internal OwnerConsoleTestClock Clock { get; } = new() { Now = DateTimeOffset.Parse("2026-10-17T00:06:00Z") };
        internal string Log => Path.Combine(Root, "conduct-events.log");
        internal string State => Path.Combine(Root, ConductorProgramHealthMonitor.StateFileName);
        internal string AttemptRoot => Path.Combine(Root, "grouped-gate-attempts");
        internal (GoalId Id, GoalStatus Status)[] Goals { get; } = Enumerable.Range(0, 12)
            .Select(index => (new GoalId(index.ToString("x8") + new string('a', 24)), GoalStatus.Verified)).ToArray();
        internal ConductEventLogWriter Writer { get; }
        internal CollaborationItemStore Attention { get; }
        internal ConductorInvariantObservationReader Reader { get; }
        internal ConductorGroupedGateAttemptCoordinator Coordinator { get; }
        private DotnetBuildStorageRoot Storage { get; }
        internal int StopCalls { get; private set; }
        internal string MembersKey => string.Join('+', Goals.Take(2).Select(goal => goal.Id.Value));

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Storage = new DotnetBuildStorageRoot(Path.Combine(Root, "build-storage"));
            Writer = new ConductEventLogWriter(Log, utcNow: () => Clock.Now);
            Attention = CollaborationItemStore.ForDirectory(Root);
            Reader = new(Log, AttemptRoot, now => GateHeartbeatArtifacts.ReadStableSlots(Storage, now));
            Coordinator = new(AttemptRoot, generationId: 123,
                launch: _ => throw new InvalidOperationException("This fixture never launches processes."),
                isProcessAlive: _ => true, stop: _ => { StopCalls++; return true; },
                utcNow: () => Clock.Now, eventSink: line => Writer.Append("acceptance-cohort", null, line),
                buildStorageRoot: Storage);
        }

        internal ConductorProgramHealthMonitor Monitor(ConductEventLogWriter? writer = null,
            Func<GoalId, IReadOnlyList<ProgramHealthReceiptObservation>>? receipts = null) =>
            new(State, () => null, writer ?? Writer, Clock, receipts ?? (_ => []), Attention);
        internal ConductorGroupedGateAttempt NewAttempt() => Coordinator.Create("merge-train", [],
            "main", "tree", "manifest", "identity", Root, ConductorAutonomyPolicy.Permissive) with
        {
            Members = Goals.Take(2).Select(goal => new ConductorGroupedGateMember(goal.Id.Value,
                "branch", "candidate", [], [], ChangeRiskTier.Behavior)).ToArray()
        };
        internal void Refuse(string field)
        {
            var attempt = NewAttempt();
            ConductorGroupedGateAttemptCoordinator.Save(attempt);
            Assert.True(File.Exists(attempt.MetadataPath));
            Assert.True(Coordinator.Refuse(attempt, field));
            Assert.StartsWith("refused:", ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath).Detail);
        }
        internal void Hold(int index, string reason) => Writer.Append("goal", Goals[index].Id.Value,
            ConductorBatchLoop.FormatGoalProgressLine(Goals[index].Id.Value[..8],
                new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, reason)));
        internal void ReadyAll()
        {
            var now = Clock.Now;
            Clock.Now = DateTimeOffset.Parse("2026-10-17T00:04:00Z");
            for (var index = 0; index < Goals.Length; index++) Hold(index, "acceptance width 1 reached");
            Clock.Now = now;
        }
        internal void Heartbeat(DateTimeOffset progress) => GateHeartbeatArtifacts.Write(
            GateHeartbeatArtifacts.GetStableSlotPath(0, Storage),
            new(null, "test", "target", 0, null, null, "running", progress, Clock.Now, progress, 0, 0, 0));
        internal JsonElement[] Events() => File.Exists(Log) ? File.ReadAllLines(Log).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray() : [];
        internal JsonElement[] InvariantEvents(string prefix) => Events().Where(row =>
            row.GetProperty("eventKind").GetString() == "invariant-breach" &&
            row.GetProperty("detail").GetString()!.StartsWith(prefix + " ", StringComparison.Ordinal)).ToArray();
        internal IReadOnlyList<CollaborationItem> OpenItems() => Attention.GetAttentionQueueAsync().GetAwaiter().GetResult();
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
