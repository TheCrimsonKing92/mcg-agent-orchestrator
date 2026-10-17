using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: files, storage, SQLite and clocks belong to a unique fixture root.
public sealed class ConductorInvariantSliceTwoWiringTests
{
    [Fact]
    public void ForWorkspace_BothAttemptStores_ExcludesOtherStores()
    {
        using var fixture = new Fixture();
        var preReview = fixture.Attempt(0, "pre-review-evidence-attempts");
        var gate = fixture.Attempt(1, "acceptance-gate-attempts");
        fixture.Save(preReview);
        fixture.Save(gate);
        fixture.Save(fixture.Attempt(2, "other-attempts"));
        var observation = fixture.Reader.Read(fixture.Goals, fixture.Clock.Now);
        Assert.Equal(new[] { preReview.AttemptId, gate.AttemptId }.Order(),
            Assert.IsAssignableFrom<IReadOnlyList<ConductorFinishedAttempt>>(observation.FinishedAttempts)
                .Select(attempt => attempt.AttemptId).Order());
    }

    [Fact]
    public void Read_OldConstructor_ProvidesEmptyNewObservations()
    {
        using var fixture = new Fixture();
        fixture.Save(fixture.Attempt());
        var reader = new ConductorInvariantObservationReader(fixture.Log,
            Path.Combine(fixture.Workspace.OrchestratorDirectory, "grouped-gate-attempts"), _ => []);
        var observation = reader.Read(fixture.Goals, fixture.Clock.Now);
        Assert.NotNull(observation.FinishedAttempts);
        Assert.Empty(observation.FinishedAttempts);
        Assert.Equal(ConductorRelaunchObservation.Empty, observation.Relaunch);
    }

    [Fact]
    public void Read_RotatedRelaunchAndFutureLines_PreservesChronologicalWindow()
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        fixture.Build(Fixture.BuildA);
        fixture.Schedule();
        File.Move(fixture.Log, Path.Combine(Path.GetDirectoryName(fixture.Log)!, "conduct-events-20261016.log"));
        fixture.Clock.Now = start.AddMinutes(1);
        fixture.Schedule(); // Coalesced schedules retain the first goal and timestamp.
        fixture.Append("ACTIVATION_ADOPTED adoptedCommit=future", start.AddHours(1));
        File.AppendAllText(fixture.Log, "{malformed LOOP_RELAUNCH_SCHEDULED \n");
        var relaunch = fixture.Reader.Read(fixture.Goals, fixture.Clock.Now).Relaunch;
        Assert.Equal(new(start, fixture.Goals[0].Id.Value, null, null, Fixture.BuildA), relaunch);
        fixture.Append("ACTIVATION_REVERTED reason=probe");
        fixture.Clock.Now = start.AddMinutes(2);
        fixture.Schedule(1);
        relaunch = fixture.Reader.Read(fixture.Goals, fixture.Clock.Now).Relaunch;
        Assert.Equal(new(fixture.Clock.Now, fixture.Goals[1].Id.Value, null, null, Fixture.BuildA), relaunch);
        fixture.Append("ACTIVATION_ADOPTED adoptedCommit=main");
        Assert.Equal(ConductorRelaunchObservation.Empty with { RunningBuildCommit = Fixture.BuildA },
            fixture.Reader.Read(fixture.Goals, fixture.Clock.Now).Relaunch);
    }

    internal sealed class Fixture : IDisposable
    {
        internal const string BuildA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        internal const string BuildB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "invariant-slice-two", Guid.NewGuid().ToString("N"));
        internal OwnerConsoleTestClock Clock { get; } = new() { Now = DateTimeOffset.Parse("2026-10-16T21:17:00Z") };
        internal OrchestratorWorkspace Workspace { get; }
        internal string Log => Workspace.ConductEventsLogPath;
        internal string State => Path.Combine(Workspace.OrchestratorDirectory, ConductorProgramHealthMonitor.StateFileName);
        internal (GoalId Id, GoalStatus Status)[] Goals { get; } = Enumerable.Range(0, 4)
            .Select(index => (new GoalId(index.ToString("x8") + new string('a', 24)), GoalStatus.Verified)).ToArray();
        internal ConductEventLogWriter Writer { get; private set; }
        internal CollaborationItemStore Attention { get; private set; }
        internal ConductorInvariantObservationReader Reader { get; }
        internal ConductorProgramHealthMonitor Monitor { get; private set; }

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Writer = new(Log, utcNow: () => Clock.Now);
            Attention = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
            Reader = ConductorInvariantObservationReader.ForWorkspace(Workspace,
                new DotnetBuildStorageRoot(Path.Combine(Root, "build-storage")));
            Monitor = NewMonitor();
        }

        private ConductorProgramHealthMonitor NewMonitor() => new(State, () => null, Writer, Clock, _ => [], Attention);
        internal void Restart()
        {
            Writer = new(Log, utcNow: () => Clock.Now);
            Attention = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
            Monitor = NewMonitor();
        }
        internal void Tick(DateTimeOffset now, string? head = null)
        {
            Clock.Now = now;
            Monitor.Evaluate(Goals, head, Reader.Read);
        }
        internal void Append(string detail, DateTimeOffset? at = null) => Writer.Append("replay", null, detail, at ?? Clock.Now);
        internal void Schedule(int goal = 0) => Append($"LOOP_RELAUNCH_SCHEDULED tick=1 goal={Goals[goal].Id.Value} changedFiles=1 coalesced=true");
        internal void Build(string sha) => Append("SUPERVISOR_BUILD " + JsonSerializer.Serialize(new { commitSha = sha }));
        internal void Drain(int liveGates, int tick) => Append(new ConductorSelfRelaunchDrainHold(false,
            liveGates, TimeSpan.FromSeconds(tick * 30)).DescribeProgress(tick, Goals[0].Id.Value, 0, ""));

        internal ConductorParallelAcceptanceAttempt Attempt(int goal = 0, string store = "pre-review-evidence-attempts",
            string suffix = "first", DateTimeOffset? finishedAt = null)
        {
            var id = Goals[goal].Id.Value + "-" + suffix;
            var directory = Path.Combine(Workspace.OrchestratorDirectory, store, Goals[goal].Id.Value);
            var finish = finishedAt ?? Clock.Now;
            return new(id, Goals[goal].Id.Value, Goals[goal].Id.Value[..8], 0, BuildB, BuildA,
                finish.AddMinutes(-1), finish, 0, ConductorParallelAcceptanceAttemptOutcome.Passed,
                Path.Combine(directory, id + ".out.log"), Path.Combine(directory, id + ".err.log"),
                Path.Combine(directory, id + ".exit.txt"), Path.Combine(directory, id + ".heartbeat.json"),
                Path.Combine(directory, id + ".result.json"), Path.Combine(directory, id + ".attempt.json"),
                CompletedAt: finish, Kind: store == "pre-review-evidence-attempts" ? "pre-review-evidence" : "gate");
        }

        internal void Save(ConductorParallelAcceptanceAttempt attempt, bool exit = true,
            DateTimeOffset? modifiedAt = null, DateTimeOffset? exitAt = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(attempt.MetadataPath)!);
            // Same options as the coordinator's private JsonOptions; Outcome remains numeric.
            File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            File.SetLastWriteTimeUtc(attempt.MetadataPath, (modifiedAt ?? Clock.Now).UtcDateTime);
            if (!exit) return;
            File.WriteAllText(attempt.ExitCodePath, "0");
            File.SetLastWriteTimeUtc(attempt.ExitCodePath, (exitAt ?? attempt.CompletedAt ?? Clock.Now).UtcDateTime);
        }
        internal JsonElement[] Events(string prefix) => File.Exists(Log) ? File.ReadAllLines(Log).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).Where(row => row.GetProperty("detail").GetString()!.StartsWith(prefix + " ", StringComparison.Ordinal)).ToArray() : [];
        internal IReadOnlyList<CollaborationItem> OpenItems() => Attention.GetAttentionQueueAsync().GetAwaiter().GetResult();
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
