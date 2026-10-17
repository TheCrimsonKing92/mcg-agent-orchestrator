using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each scenario owns its temp workspace, SQLite stores, log and clock.
public sealed class IncrementalExperimentWatchScenarioTests
{
    [Fact(Timeout = 30000)]
    [Trait("Category", "CrossTick")]
    public void ObserveTick_UnlandedThenLandedPairBelowTarget_RaisesNoAttention()
    {
        WithWorkspace(workspace =>
        {
            var fixture = new Fixture(workspace, 12);
            fixture.SaveMembers(GoalStatus.Active);
            fixture.AssertPersistedIncident();

            for (var tick = 0; tick < 3; tick++) fixture.Tick();
            Assert.Empty(fixture.Events());
            Assert.Empty(fixture.Attention());

            fixture.SaveMembers(GoalStatus.Completed);
            fixture.Tick();
            Assert.Empty(fixture.Events());
            Assert.Empty(fixture.Attention());
            var output = fixture.Show();
            Assert.Contains($"stop rule: 2 of 12 goals (not met){Environment.NewLine}" +
                "counted: landed member goals, both arms", output);
        });
    }

    [Fact(Timeout = 30000)]
    [Trait("Category", "CrossTick")]
    public void ObserveTick_LandedPairAtTarget_ReportsBasisOnceAndKeepsLegacyText()
    {
        WithWorkspace(workspace =>
        {
            var fixture = new Fixture(workspace, 2);
            fixture.SaveMembers(GoalStatus.Completed);
            fixture.AssertPersistedIncident();
            var legacy = fixture.AddLegacy();

            fixture.Tick();
            var events = fixture.Events();
            Assert.Equal(2, events.Count);
            var detail = Assert.Single(events.Where(text => text.Contains($"experiment={fixture.Record.Id} ")));
            Assert.Contains("observed=2 stopRuleMet=true", detail);
            Assert.EndsWith("guardrailBreached=false observedBasis=landed-member-goals", detail);
            var legacyDetail = Assert.Single(events.Where(text => text.Contains($"experiment={legacy.Id} ")));
            Assert.Equal($"EXPERIMENT_READING_DUE experiment={legacy.Id} trigger=stop-rule observed=13 " +
                "stopRuleMet=true verdict=inconclusive guardrailBreached=false", legacyDetail);
            Assert.DoesNotContain("observedBasis", legacyDetail);
            Assert.Equal(2, fixture.Attention().Count);

            fixture.Tick();
            fixture.Tick();
            Assert.Equal(events, fixture.Events());
            Assert.Equal(2, fixture.Attention().Count);
            Assert.Contains($"stop rule: 2 of 2 goals (met){Environment.NewLine}" +
                "counted: landed member goals, both arms", fixture.Show());
            var legacyOutput = fixture.Show(legacy);
            Assert.Contains("stop rule: 13 of 2 goals (met)", legacyOutput);
            Assert.DoesNotContain("counted:", legacyOutput);
        });
    }

    private static void WithWorkspace(Action<OrchestratorWorkspace> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "incremental-experiment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(OrchestratorWorkspace.ForDirectory(root)); }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Fixture
    {
        private static string BaselineId => new('a', 32);
        private static string ComparisonId => new('b', 32);
        private readonly OrchestratorWorkspace _workspace;
        private readonly ExperimentStore _experiments;
        private readonly SqliteOrchestratorStateRepository _state;
        private readonly ConductEventLogWriter _writer;
        private readonly ConductorExperimentWatch _watch;
        internal ExperimentRecord Record { get; }
        internal DateTimeOffset Now { get; }

        internal Fixture(OrchestratorWorkspace workspace, int target)
        {
            _workspace = workspace;
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            _state = new(workspace.SqliteStatePath);
            _experiments = new(workspace.ExperimentStorePath);
            Record = _experiments.AddAsync(IncrementalStopCountTests.Spec() with
                { StopRule = new(target, ExperimentStopUnit.Goals) }).GetAwaiter().GetResult();
            _experiments.AddMemberPairAsync(Record.Id, BaselineId, "baseline-sha", ComparisonId,
                "comparison-sha", "matched briefs", Record.CreatedAt).GetAwaiter().GetResult();
            Now = Record.CreatedAt.AddHours(1);
            _state.SaveGoalSnapshotsAsync(Enumerable.Range(1, 13).Select(index =>
                Snapshot($"unrelated-{index}", index % 2 == 0 ? GoalStatus.Completed : GoalStatus.Failed,
                    Record.CreatedAt.AddMinutes(index))).ToArray()).GetAwaiter().GetResult();
            _writer = new(workspace.ConductEventsLogPath, utcNow: () => Now);
            var items = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            _watch = new(workspace, _writer, () => items);
        }

        internal void SaveMembers(GoalStatus status) => _state.SaveGoalSnapshotsAsync(
            [Snapshot(BaselineId, status), Snapshot(ComparisonId, status)]).GetAwaiter().GetResult();

        internal void AssertPersistedIncident()
        {
            Assert.True(File.Exists(_workspace.SqliteStatePath));
            var goals = ExperimentGoals.Read(_workspace.SqliteStatePath);
            Assert.Equal(15, goals.Count);
            Assert.Equal(13, goals.Count(goal => goal.Tasks.SelectMany(task => task.DispatchHistory)
                .Any(dispatch => dispatch.DispatchedAt >= Record.CreatedAt && dispatch.DispatchedAt < Now)));
            var members = _experiments.ListMemberEventsAsync(Record.Id).GetAwaiter().GetResult();
            Assert.Equal(2, members.Count);
            var effective = ExperimentMembership.Effective(Record, members);
            Assert.Equal(new[] { BaselineId }, effective.Spec.Baseline.BaselineGoalIds);
            Assert.Equal(new[] { ComparisonId }, effective.Spec.Baseline.ComparisonGoalIds);
        }

        internal ExperimentRecord AddLegacy() => _experiments.AddAsync(IncrementalStopCountTests.Spec() with
        {
            Baseline = new(ExperimentBaselineKind.GoalCohort,
                BaselineGoalIds: ["unrelated-1"], ComparisonGoalIds: ["unrelated-2"]),
            StopRule = new(2, ExperimentStopUnit.Goals)
        }).GetAwaiter().GetResult();

        internal void Tick() => Assert.Equal("", AsyncLocalConsoleRouter.CaptureError(() => _watch.ObserveTick(Now)));

        internal List<string> Events() => !File.Exists(_writer.CurrentPath) ? [] :
            File.ReadAllLines(_writer.CurrentPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(entry => entry.GetProperty("eventKind").GetString() == "experiment-reading-due")
                .Select(entry => entry.GetProperty("detail").GetString()!).ToList();

        internal IReadOnlyList<CollaborationItem> Attention() =>
            CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory)
                .GetAttentionQueueAsync().GetAwaiter().GetResult();

        internal string Show(ExperimentRecord? record = null)
        {
            record ??= Record;
            var effective = ExperimentMembership.Effective(record,
                _experiments.ListMemberEventsAsync(record.Id).GetAwaiter().GetResult());
            var reading = ExperimentReading.Evaluate(effective, ExperimentGoals.Read(_workspace.SqliteStatePath),
                new Dictionary<string, DateTimeOffset>(), [], Now, null, ExperimentHostLedgers.Empty);
            return AsyncLocalConsoleRouter.Capture(() => ExperimentReading.Render(reading));
        }

        private static GoalSnapshot Snapshot(string id, GoalStatus status, DateTimeOffset? dispatch = null) =>
            new(id, id, status,
                [new TaskSnapshot(id + "-task", "implement", AgentRole.Developer,
                    status == GoalStatus.Active ? WorkTaskStatus.Pending : WorkTaskStatus.Completed,
                    null, null, null, [], null, null,
                    DispatchHistory: dispatch is { } at ? [new("worker", "command", "root", at)] : [])], []);
    }
}
