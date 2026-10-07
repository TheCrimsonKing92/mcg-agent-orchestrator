using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = StateLogDivergenceCheckRunnerTests.Fixture;

// Parallel-safe: each fixture owns its state path, path-keyed mutex, queries and injected clock.
public sealed class StateLogDivergenceDurableStateTests
{
    [Fact]
    public async Task UnchangedFixtureIsSuppressedAcrossInstances()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.Equal(2, (await RunAsync(await RunnerAsync(fixture))).EmittedEvents);
        fixture.Now += StateLogDivergenceCheckRunner.Interval;
        var second = await RunAsync(await RunnerAsync(fixture));
        Assert.Equal(3, second.CheckedGoals);
        Assert.Equal(0, second.EmittedEvents);
        Assert.Equal(2, fixture.Events("state-log-divergence").Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedCountsOrKindsReportOnlyChangedGoal(bool kindsOnly)
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.Equal(2, (await RunAsync(await RunnerAsync(fixture))).EmittedEvents);
        if (kindsOnly)
            File.WriteAllText(fixture.LogPath(fixture.LostGoal),
                File.ReadAllText(fixture.LogPath(fixture.LostGoal)).Replace("TaskRetried", "TaskFailed", StringComparison.Ordinal));
        else
            fixture.Writer.AppendTimelineEvent(new(fixture.LostGoal.Id, null, ProgressKind.TaskRetried,
                "second unmatched retry", fixture.Now));

        fixture.Now += StateLogDivergenceCheckRunner.Interval;
        var second = await RunAsync(await RunnerAsync(fixture));
        Assert.Equal(1, second.EmittedEvents);
        var events = fixture.Events("state-log-divergence");
        Assert.Equal(3, events.Length);
        Assert.Equal(fixture.LostGoal.Id.Value, events[^1].GetProperty("goalId").GetString());
        Assert.Contains(kindsOnly ? "lost=1 repeated=0 stored_only=0 kinds=TaskFailed:1" :
            "lost=2 repeated=0 stored_only=0 kinds=TaskRetried:2", events[^1].GetProperty("detail").GetString());
        Assert.Single(events, entry => entry.GetProperty("goalId").GetString() == fixture.StoredGoal.Id.Value);
        fixture.Now += StateLogDivergenceCheckRunner.Interval;
        Assert.Equal(0, (await RunAsync(await RunnerAsync(fixture))).EmittedEvents);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("Superseded")]
    public async Task TerminalReportIsRememberedAndSkippedBeforeLoadingEvenWhenCountsChange(string status)
    {
        using var fixture = await Fixture.CreateAsync();
        var queries = new TrackingQueries(fixture, status);
        Assert.Equal(2, (await RunAsync(await RunnerAsync(fixture, queries))).EmittedEvents);
        Assert.Contains(fixture.LostGoal.Id.Value, queries.LoadedIds);
        for (var round = 0; round < 2; round++)
        {
            fixture.Writer.AppendTimelineEvent(new(fixture.LostGoal.Id, null, ProgressKind.TaskRetried,
                $"terminal retry {round}", fixture.Now));
            queries.LoadedIds.Clear();
            fixture.Now += StateLogDivergenceCheckRunner.Interval;
            var result = await RunAsync(await RunnerAsync(fixture, queries));
            Assert.Equal(0, result.EmittedEvents);
            Assert.Equal(2, result.CheckedGoals);
            Assert.Equal(1, result.SkippedReasons["terminal-reported"]);
            Assert.DoesNotContain(fixture.LostGoal.Id.Value, queries.LoadedIds);
        }
        Assert.Equal(2, fixture.Events("state-log-divergence").Length);
        Assert.Contains("reasons=terminal-reported:1",
            Assert.Single(fixture.Events("state-log-divergence-skipped")).GetProperty("detail").GetString());
        Assert.Contains("terminal-reported:1", File.ReadAllText(StatePath(fixture)));
    }

    [Fact]
    public async Task PersistedHourlyGatePreventsSchedulingAndQueriesUntilInjectedClockReachesInterval()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.Equal(2, (await RunAsync(await RunnerAsync(fixture))).EmittedEvents);
        fixture.Now += TimeSpan.FromMinutes(30);
        var queries = new TrackingQueries(fixture);
        var runner = await RunnerAsync(fixture, queries);
        Assert.False(runner.OnTick());
        Assert.Null(runner.CurrentRun);
        Assert.Equal(0, queries.MetadataCalls);
        fixture.Now += TimeSpan.FromMinutes(30);
        var result = await RunAsync(runner);
        Assert.Equal(1, queries.MetadataCalls);
        Assert.Equal(0, result.EmittedEvents);
    }

    [Fact]
    public async Task DurableRunUsesOnlyQueriesAndWritesOnlyStateBeyondExistingAdvisoryJournal()
    {
        using var fixture = await Fixture.CreateAsync();
        var repository = DispatchProxy.Create<IOrchestratorStateOutboxRepository, StateLogDivergenceReadOnlyTests.QueryProbe>();
        var probe = (StateLogDivergenceReadOnlyTests.QueryProbe)repository;
        probe.Target = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath);
        var before = Snapshot(fixture);
        var result = await RunAsync(await RunnerAsync(fixture, repository));
        Assert.Equal(3, result.CheckedGoals);
        Assert.Equal(2, result.EmittedEvents);
        Assert.Equal(1, probe.Calls.Count(call => call == "ListGoalMetadataAsync"));
        Assert.Equal(3, probe.Calls.Count(call => call == "LoadGoalsAsync"));
        Assert.Equal(0, probe.Calls.Count(call => call.StartsWith("Save", StringComparison.Ordinal)));
        Assert.Equal(0, probe.Calls.Count(call => call.StartsWith("Transact", StringComparison.Ordinal)));
        Assert.Equal(0, probe.Calls.Count(call => call.Contains("Outbox", StringComparison.Ordinal)));
        var after = Snapshot(fixture);
        Assert.Equal(before.Keys.Order(), after.Keys.Where(path => path != StatePath(fixture)).Order());
        foreach (var path in before.Keys) Assert.Equal(before[path], after[path]);
        Assert.Equal([StatePath(fixture)], after.Keys.Where(path => !before.ContainsKey(path)).ToArray());
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        using var state = JsonDocument.Parse(after[StatePath(fixture)]);
        Assert.Equal(fixture.Now, state.RootElement.GetProperty("lastStartedUtc").GetDateTimeOffset());
        Assert.Equal("TaskRetried:1", state.RootElement.GetProperty("goals")
            .GetProperty(fixture.LostGoal.Id.Value).GetProperty("kinds").GetString());
        Assert.False(state.RootElement.GetProperty("goals").GetProperty(fixture.LostGoal.Id.Value)
            .GetProperty("terminal").GetBoolean());
    }

    [Fact]
    public async Task ResolvedDivergenceClearsStoredEntryAndLaterRecurrenceReportsAgain()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.Equal(2, (await RunAsync(await RunnerAsync(fixture))).EmittedEvents);
        var path = fixture.LogPath(fixture.LostGoal);
        var original = File.ReadAllText(path);
        File.WriteAllLines(path, File.ReadAllLines(path).Where(line =>
            StateLogDivergenceComparer.ParseLine(line)?.Entry.Kind != nameof(ProgressKind.TaskRetried)));
        fixture.Now += StateLogDivergenceCheckRunner.Interval;
        Assert.Equal(0, (await RunAsync(await RunnerAsync(fixture))).EmittedEvents);
        using (var state = JsonDocument.Parse(File.ReadAllText(StatePath(fixture))))
            Assert.False(state.RootElement.GetProperty("goals").TryGetProperty(fixture.LostGoal.Id.Value, out _));
        File.WriteAllText(path, original);
        fixture.Now += StateLogDivergenceCheckRunner.Interval;
        Assert.Equal(1, (await RunAsync(await RunnerAsync(fixture))).EmittedEvents);
        Assert.Equal(fixture.LostGoal.Id.Value, fixture.Events("state-log-divergence")[^1].GetProperty("goalId").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{torn")]
    [InlineData("null")]
    [InlineData("{\"goals\":null}")]
    public async Task EmptyOrMalformedStateIsRecoveredWithoutFaultingTickOrJoin(string contents)
    {
        using var fixture = await Fixture.CreateAsync();
        File.WriteAllText(StatePath(fixture), contents);
        var runner = await RunnerAsync(fixture);
        Assert.Equal(2, (await RunAsync(runner)).EmittedEvents);
        await TestHangGuard.WaitAsync(runner.WaitForCurrentRunAsync(), "recovered state exit join");
        Assert.True(runner.CurrentRun!.IsCompletedSuccessfully);
        Assert.Empty(fixture.Events("state-log-divergence-failed"));
        using var state = JsonDocument.Parse(File.ReadAllText(StatePath(fixture)));
        Assert.Equal(2, state.RootElement.GetProperty("goals").EnumerateObject().Count());
    }

    [Fact]
    public async Task UnreadableStateAndSaveFailureAreAdvisoryAndDoNotFaultExitJoin()
    {
        using var fixture = await Fixture.CreateAsync();
        File.WriteAllText(StatePath(fixture), "unreadable");
        using (var locked = new FileStream(StatePath(fixture), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var runner = await RunnerAsync(fixture);
            Assert.Equal(2, (await RunAsync(runner)).EmittedEvents);
            await TestHangGuard.WaitAsync(runner.WaitForCurrentRunAsync(), "unreadable state exit join");
            Assert.True(runner.CurrentRun!.IsCompletedSuccessfully);
            Assert.Contains("STATE_LOG_DIVERGENCE_FAILED exception=", Assert.Single(
                fixture.Events("state-log-divergence-failed")).GetProperty("detail").GetString());
            Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task EachBackgroundRunReloadsStateAfterPreload()
    {
        using var fixture = await Fixture.CreateAsync();
        var runner = await RunnerAsync(fixture);
        // Another instance's successful run occurs after this runner's preload.
        Assert.Equal(2, (await RunAsync(await RunnerAsync(fixture))).EmittedEvents);
        fixture.Now += StateLogDivergenceCheckRunner.Interval;
        Assert.Equal(0, (await RunAsync(runner)).EmittedEvents);
        Assert.Equal(2, fixture.Events("state-log-divergence").Length);
    }

    private static string StatePath(Fixture fixture) =>
        Path.Combine(fixture.Workspace.OrchestratorDirectory, StateLogDivergenceCheckRunner.StateFileName);

    private static async Task<StateLogDivergenceCheckRunner> RunnerAsync(Fixture fixture, IOrchestratorStateQueries? queries = null)
    {
        var runner = new StateLogDivergenceCheckRunner(
            () => queries ?? SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath),
            fixture.Workspace.GoalLifecycleEventsDirectory, fixture.Workspace.ConductEventsLogPath,
            () => fixture.Now, StatePath(fixture));
        await TestHangGuard.WaitAsync(runner.StateLoaded, "durable state preload");
        return runner;
    }

    private static async Task<StateLogDivergenceRunResult> RunAsync(StateLogDivergenceCheckRunner runner)
    {
        Assert.True(runner.OnTick());
        return await TestHangGuard.WaitAsync(runner.CurrentRun!, "durable divergence run completion");
    }

    private static Dictionary<string, byte[]> Snapshot(Fixture fixture) =>
        Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories)
            // The existing advisory journal is expected output; SQLite owns its WAL/SHM sidecars.
            .Where(path => path != fixture.Workspace.ConductEventsLogPath &&
                !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal))
            .ToDictionary(path => path, File.ReadAllBytes);

    private sealed class TrackingQueries(Fixture fixture, string? lostGoalStatus = null) : IOrchestratorStateQueries
    {
        private readonly IOrchestratorStateQueries _target = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath);
        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
            CancellationToken cancellationToken = default) => _target.ListOpenHumanInputRequestsAsync(cancellationToken);

        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            CancellationToken cancellationToken = default) => _target.ListTerminalOwnerQuestionHoldsAsync(cancellationToken);

        internal int MetadataCalls;
        internal readonly List<string> LoadedIds = [];

        public async Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
        {
            MetadataCalls++;
            return (await _target.ListGoalMetadataAsync(cancellationToken)).Select(summary =>
                summary.Id == fixture.LostGoal.Id.Value && lostGoalStatus is not null
                    ? summary with { Status = lostGoalStatus } : summary).ToArray();
        }

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default)
        {
            LoadedIds.AddRange(goalIds.Select(id => id.Value));
            return _target.LoadGoalsAsync(goalIds, cancellationToken);
        }
    }
}
