using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each case owns its temporary workspace, stores, journals and clock.
public sealed class ExperimentWatchGoalScopeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    private static string A => new('a', 32);
    private static string B => new('b', 32);
    private static string C => new('c', 32);

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ObserveTick_MembershipChanges_RetainsAndHydratesOnlyNeededGoals()
    {
        WithWorkspace(workspace =>
        {
            var snapshots = Seed(workspace);
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            var first = store.AddAsync(Cohort(A[..8])).GetAwaiter().GetResult();
            var watch = new ConductorExperimentWatch(workspace, null);
            watch.ObserveTick(At);
            AssertRetained(watch.Inputs, 1, A);

            Save(workspace, [snapshots[0] with { Objective = "Changed member" }]);
            SetUpdatedAt(workspace, A, At.AddMinutes(1));
            watch.ObserveTick(At.AddMinutes(1));
            AssertRetained(watch.Inputs, 1, A);

            Save(workspace, [snapshots[1] with { Objective = "Changed non-member" }]);
            SetUpdatedAt(workspace, B, At.AddMinutes(2));
            watch.ObserveTick(At.AddMinutes(2));
            AssertRetained(watch.Inputs, 0, A);

            var second = store.AddAsync(Cohort(B)).GetAwaiter().GetResult();
            watch.ObserveTick(At.AddMinutes(3));
            AssertRetained(watch.Inputs, 1, A, B);

            // Release B's other membership before assigning it to the first experiment.
            Close(store, second);
            var added = store.AddMemberPairAsync(first.Id, C, "baseline-sha", B, "comparison-sha",
                "matched briefs", At.AddMinutes(4)).GetAwaiter().GetResult();
            Assert.Equal(2, added.Count);
            watch.ObserveTick(At.AddMinutes(4));
            AssertRetained(watch.Inputs, 1, A, B, C);
        });
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ObserveTick_ExperimentCloses_ReleasesCacheAndFreshWatchLoadsNothing()
    {
        WithWorkspace(workspace =>
        {
            Seed(workspace);
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            var record = store.AddAsync(Cohort(A[..8])).GetAwaiter().GetResult();
            var watch = new ConductorExperimentWatch(workspace, null);
            watch.ObserveTick(At);
            AssertRetained(watch.Inputs, 1, A);

            Close(store, record);
            watch.ObserveTick(At.AddMinutes(1));
            AssertRetained(watch.Inputs, 0);
            var fresh = new ConductorExperimentWatch(workspace, null);
            fresh.ObserveTick(At.AddMinutes(2));
            AssertRetained(fresh.Inputs, 0);
        });
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ObserveTick_ExperimentDatabaseDisappears_ReleasesCache()
    {
        WithWorkspace(workspace =>
        {
            Seed(workspace);
            new ExperimentStore(workspace.ExperimentStorePath).AddAsync(Cohort(A)).GetAwaiter().GetResult();
            var watch = new ConductorExperimentWatch(workspace, null);
            watch.ObserveTick(At);
            AssertRetained(watch.Inputs, 1, A);

            File.Delete(workspace.ExperimentStorePath);
            watch.ObserveTick(At.AddMinutes(1));
            AssertRetained(watch.Inputs, 0);
        });
    }

    [Fact]
    public void ObserveTick_OnlyGateExperiment_ReleasesPreviouslyHydratedGoals()
    {
        WithWorkspace(workspace =>
        {
            Seed(workspace);
            var spec = Spec(new(ExperimentBaselineKind.AlternatingGates), ExperimentStopUnit.Gates);
            new ExperimentStore(workspace.ExperimentStorePath).AddAsync(spec).GetAwaiter().GetResult();
            var watch = new ConductorExperimentWatch(workspace, null);
            Assert.Equal(3, watch.Inputs.ReadGoals(workspace.SqliteStatePath, At).Count);

            watch.ObserveTick(At.AddMinutes(1));
            AssertRetained(watch.Inputs, 0);
        });
    }

    [Fact]
    public void ReadGoals_MemberChangesAndFullRefresh_HydratesOnlyScopedId()
    {
        WithWorkspace(workspace =>
        {
            var snapshots = Seed(workspace);
            var cache = new ExperimentWatchInputCache();
            var scope = Scope(A);
            var expected = ExperimentGoals.Read(workspace.SqliteStatePath).Single(goal => goal.Id.Value == A);
            var first = Assert.Single(cache.ReadGoals(workspace.SqliteStatePath, At, scope));
            Assert.Equal(A, first.Id.Value);
            Assert.Equal(expected.Status, first.Status);
            Assert.Equal(expected.Tasks.Count, first.Tasks.Count);
            AssertRetained(cache, 1, A);

            Save(workspace, [snapshots[0] with { Status = GoalStatus.Cancelled }]);
            SetUpdatedAt(workspace, A, At.AddMinutes(1));
            var changed = Assert.Single(cache.ReadGoals(workspace.SqliteStatePath, At.AddMinutes(1), scope));
            Assert.Equal(A, changed.Id.Value);
            Assert.Equal(GoalStatus.Cancelled, changed.Status);
            Assert.NotSame(first, changed);
            AssertRetained(cache, 1, A);

            var refreshed = Assert.Single(cache.ReadGoals(workspace.SqliteStatePath, At.AddMinutes(60), scope));
            Assert.NotSame(changed, refreshed);
            Assert.Equal(GoalStatus.Cancelled, refreshed.Status);
            AssertRetained(cache, 1, A);

            Assert.Empty(cache.ReadGoals(workspace.SqliteStatePath, At.AddMinutes(61), ExperimentGoalScope.Empty));
            AssertRetained(cache, 0);
        });
    }

    [Fact]
    public void ReadGoals_ScopeShrinksAndExpands_ReleasesAndReloadsLeavingRows()
    {
        WithWorkspace(workspace =>
        {
            Seed(workspace);
            var cache = new ExperimentWatchInputCache();
            var both = ExperimentGoalScope.For([Record(Cohort(A)), Record(Cohort(B))]);
            var first = cache.ReadGoals(workspace.SqliteStatePath, At, both);
            var previousB = first.Single(goal => goal.Id.Value == B);
            AssertRetained(cache, 2, A, B);

            Assert.Single(cache.ReadGoals(workspace.SqliteStatePath, At.AddMinutes(1), Scope(A)));
            AssertRetained(cache, 0, A);
            var expanded = cache.ReadGoals(workspace.SqliteStatePath, At.AddMinutes(2), both);
            Assert.NotSame(previousB, expanded.Single(goal => goal.Id.Value == B));
            AssertRetained(cache, 1, A, B);
        });
    }

    [Fact]
    public void ReadGoals_ArchivedGoals_HydratesFullTasksOnlyForScopedId()
    {
        WithWorkspace(workspace =>
        {
            var snapshots = Seed(workspace);
            var expected = ExperimentGoals.Read(workspace.SqliteStatePath).Single(goal => goal.Id.Value == A);
            var archived = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
                .ArchiveTerminalGoalSnapshotsAsync(At.AddDays(-1)).GetAwaiter().GetResult();
            Assert.Equal(3, archived);

            var cache = new ExperimentWatchInputCache();
            var actual = Assert.Single(cache.ReadGoals(workspace.SqliteStatePath, At, Scope(A)));
            Assert.Equal(A, actual.Id.Value);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(snapshots[0].Tasks.Count, actual.Tasks.Count);
            Assert.Equal(expected.Tasks.Count, actual.Tasks.Count);
            AssertRetained(cache, 1, A);
        });
    }

    [Fact]
    public void ReadGoals_EmptyScope_DoesNotOpenAnInvalidDatabase()
    {
        WithWorkspace(workspace =>
        {
            Seed(workspace);
            var cache = new ExperimentWatchInputCache();
            cache.ReadGoals(workspace.SqliteStatePath, At, Scope(A));
            var invalidPath = Path.Combine(workspace.RootDirectory, "invalid.db");
            File.WriteAllText(invalidPath, "not a sqlite database");

            Assert.Empty(cache.ReadGoals(invalidPath, At.AddMinutes(1), ExperimentGoalScope.Empty));
            AssertRetained(cache, 0);
            var reloaded = Assert.Single(cache.ReadGoals(workspace.SqliteStatePath, At.AddMinutes(2), Scope(A)));
            Assert.Equal(A, reloaded.Id.Value);
            AssertRetained(cache, 1, A);
        });
    }

    [Theory]
    [InlineData(ExperimentBaselineKind.GoalCohort, true, ExperimentStopUnit.Goals)]
    [InlineData(ExperimentBaselineKind.GoalCohort, false, ExperimentStopUnit.Goals)]
    [InlineData(ExperimentBaselineKind.TwinGoal, false, ExperimentStopUnit.Gates)]
    [InlineData(ExperimentBaselineKind.BeforeAfterWindow, false, ExperimentStopUnit.Goals)]
    [InlineData(ExperimentBaselineKind.AlternatingGates, false, ExperimentStopUnit.Gates)]
    public void Evaluate_ScopedInputs_PreservesEveryReadingField(
        ExperimentBaselineKind kind, bool incremental, ExperimentStopUnit unit)
    {
        WithWorkspace(workspace =>
        {
            var since = At.AddHours(-4);
            var until = At.AddHours(-2);
            var ids = new[] { "aaaa1111000000000000000000000000", "aaaa1111111111111111111111111111", B, C,
                new string('d', 32), new string('e', 32) };
            var dispatches = new[] { since.AddMinutes(10), since.AddMinutes(20), until.AddMinutes(10),
                until.AddMinutes(20), since.AddMinutes(30), since.AddDays(-3) };
            Save(workspace, ids.Select((id, index) => Snapshot(id, index % 2 + 1, dispatches[index])).ToArray());
            Directory.CreateDirectory(workspace.GoalLifecycleEventsDirectory);
            File.WriteAllLines(Path.Combine(workspace.GoalLifecycleEventsDirectory, "landings.jsonl"),
                ids.Select((id, index) => JsonSerializer.Serialize(new
                    { eventType = "GoalLanded", goalId = id, timestamp = dispatches[index].AddMinutes(2) })));
            for (var index = 0; index < ids.Length; index++)
                SetUpdatedAt(workspace, ids[index], dispatches[index].AddMinutes(3));

            var baseline = kind switch
            {
                ExperimentBaselineKind.GoalCohort => new ExperimentBaseline(kind,
                    BaselineGoalIds: ["aaaa1111", "ffffffff"], ComparisonGoalIds: [B], IncrementalMembers: incremental),
                ExperimentBaselineKind.TwinGoal => new(kind, TwinGoalId: ids[0], ComparisonGoalId: B),
                ExperimentBaselineKind.BeforeAfterWindow => new(kind, Since: since, Until: until),
                _ => new ExperimentBaseline(kind)
            };
            var spec = Spec(baseline, unit);
            if (kind == ExperimentBaselineKind.BeforeAfterWindow)
                spec = spec with { Metrics = ["rounds-per-landing", "productive-rounds", "landings-per-hour"] };
            var record = Record(spec) with { CreatedAt = since };
            var cache = new ExperimentWatchInputCache();
            var scoped = cache.ReadGoals(workspace.SqliteStatePath, At, ExperimentGoalScope.For([record]));
            var all = ExperimentGoals.Read(workspace.SqliteStatePath);
            Assert.Equal(6, all.Count);
            var landings = ExperimentLandingTimes.Read(workspace.GoalLifecycleEventsDirectory);
            Assert.Equal(6, landings.Count);
            Assert.All(all, goal =>
            {
                Assert.NotEmpty(goal.Timeline);
                Assert.NotEmpty(goal.Tasks.SelectMany(task => task.DispatchHistory));
            });
            var expected = ExperimentReading.Evaluate(record, all, landings, [], At, 3, ExperimentHostLedgers.Empty);
            var actual = ExperimentReading.Evaluate(record, scoped, landings, [], At, 3, ExperimentHostLedgers.Empty);

            Assert.Equal(expected.ObservedCount, actual.ObservedCount);
            Assert.Equal(expected.StopRuleMet, actual.StopRuleMet);
            Assert.Equal(expected.Verdict, actual.Verdict);
            Assert.Equal(expected.Reason, actual.Reason);
            Assert.Equal(expected.GuardrailBreached, actual.GuardrailBreached);
            Assert.Equal(expected.Goals, actual.Goals);
            Assert.Equal(expected.GoalsLabel, actual.GoalsLabel);
            Assert.Equal(expected.ObservedBasis, actual.ObservedBasis);
            Assert.Equal(expected.StopTarget, actual.StopTarget);
            Assert.Equal(expected.Metrics.ToArray(), actual.Metrics.ToArray());
            if (kind == ExperimentBaselineKind.GoalCohort)
            {
                Assert.Contains("is ambiguous", actual.Reason);
                Assert.Contains("not found", actual.Reason);
            }
            Assert.Equal(kind switch
            {
                ExperimentBaselineKind.GoalCohort => incremental ? 3 : 5,
                ExperimentBaselineKind.TwinGoal => 2,
                ExperimentBaselineKind.BeforeAfterWindow => 5,
                _ => 0
            }, scoped.Count);
            Assert.DoesNotContain(scoped, goal => goal.Id.Value == ids[5]);
        });
    }

    private static void AssertRetained(ExperimentWatchInputCache cache, int hydrated, params string[] ids)
    {
        Assert.Equal(ids.Length, cache.RetainedGoalCount);
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            cache.RetainedGoalIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(hydrated, cache.LastHydratedGoalCount);
    }

    private static ExperimentSpec Cohort(string reference) => Spec(new(ExperimentBaselineKind.GoalCohort,
        BaselineGoalIds: [reference], ComparisonGoalIds: [], IncrementalMembers: true), ExperimentStopUnit.Goals);

    private static ExperimentSpec Spec(ExperimentBaseline baseline, ExperimentStopUnit unit)
    {
        var metric = baseline.Kind == ExperimentBaselineKind.AlternatingGates ? "gate-seconds" : "productive-rounds";
        return new("Scoped watch inputs", new(ExperimentInterventionKind.BriefOrPromptChange, "Scoped inputs"),
            baseline, [metric], new(metric, new(metric, ">", 1000)), new(12, unit),
            new([new(metric, "<=", 0)], [new(metric, ">", 0)]));
    }

    private static ExperimentRecord Record(ExperimentSpec spec) =>
        new(new string('f', 32), spec, At, ExperimentOutcomeState.Open, null);

    private static ExperimentGoalScope Scope(string reference) => ExperimentGoalScope.For([Record(Cohort(reference))]);

    private static void Close(ExperimentStore store, ExperimentRecord record) =>
        store.DecideAsync(record.Id, ExperimentOutcomeState.Inconclusive, "fixture", "close").GetAwaiter().GetResult();

    private static GoalSnapshot[] Seed(OrchestratorWorkspace workspace)
    {
        var snapshots = new[] { Snapshot(A, 2, At.AddDays(-10)), Snapshot(B, 1, At.AddDays(-10)),
            Snapshot(C, 3, At.AddDays(-10)) };
        Save(workspace, snapshots);
        foreach (var snapshot in snapshots) SetUpdatedAt(workspace, snapshot.Id, At.AddDays(-9));
        return snapshots;
    }

    private static GoalSnapshot Snapshot(string id, int taskCount, DateTimeOffset dispatch)
    {
        var tasks = Enumerable.Range(0, taskCount).Select(index => new TaskSnapshot($"{id}-{index}",
            "Implement", AgentRole.Developer, WorkTaskStatus.Completed, null, null, null, [], null, null,
            DispatchHistory: [new("worker", "command", "root", dispatch, ResultCommit: "fixture-commit")],
            WorkerBuildInvocations: [])).ToArray();
        return new(id, id, GoalStatus.Completed, tasks,
            [new ProgressEventSnapshot(id, null, ProgressKind.GoalPolicyDecision, "Completed", dispatch.AddMinutes(1))],
            CreatedAt: dispatch.AddHours(-1));
    }

    private static void Save(OrchestratorWorkspace workspace, GoalSnapshot[] snapshots)
    {
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
            .SaveGoalSnapshotsAsync(snapshots).GetAwaiter().GetResult();
    }

    private static void SetUpdatedAt(OrchestratorWorkspace workspace, string id, DateTimeOffset at)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = workspace.SqliteStatePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE goals SET updated_at = $at WHERE id = $id";
        command.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", id);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void WithWorkspace(Action<OrchestratorWorkspace> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "experiment-scope-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try { action(OrchestratorWorkspace.ForDirectory(root)); }
        finally { Directory.Delete(root, true); }
    }
}
