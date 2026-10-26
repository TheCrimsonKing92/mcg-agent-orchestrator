using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: event gates, clocks, kernel state and filesystem roots are owned by each test.
public sealed class OwnerConsoleIncrementalRefreshTests
{
    private const string GateGoal = "57cf8f07be1944ce91efaa1b16dd85e0";

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancelled")]
    public async Task Request_BurstDuringRefresh_DrainsOneOrderedBatch(string outcome)
    {
        var firstRelease = Signal();
        var secondRelease = Signal();
        var secondStarted = Signal();
        var calls = new List<(OwnerConductEvent[] Items, OwnerConsoleRefreshScope Scope)>();
        var coalescer = new OwnerConsoleRefreshCoalescer(async (items, scope) =>
        {
            calls.Add((items.ToArray(), scope));
            if (calls.Count == 1)
            {
                await firstRelease.Task;
                if (outcome == "failure") throw new InvalidOperationException("refresh failed");
                if (outcome == "cancelled") throw new OperationCanceledException();
            }
            else if (calls.Count == 2)
            {
                secondStarted.SetResult();
                await secondRelease.Task;
            }
        });
        var first = Item(-1);
        coalescer.Request(first, OwnerConsoleRefreshScope.None);
        Assert.Single(calls); // Invocation starts before Request returns.
        Assert.Equal([first], calls[0].Items);
        var pending = Enumerable.Range(0, 50).Select(Item).ToArray();
        foreach (var item in pending) coalescer.Request(item, OwnerConsoleRefreshScope.None);
        coalescer.Request(null, OwnerConsoleRefreshScope.For(["57cf8f07"]));
        coalescer.Request(null, OwnerConsoleRefreshScope.For(["22222222"]));
        Assert.Single(calls);
        Assert.False(coalescer.Completion.IsCompleted);

        firstRelease.SetResult();
        await AwaitEvent(secondStarted.Task, "second invocation started");
        Assert.Equal(2, calls.Count);
        Assert.Equal(pending, calls[1].Items);
        Assert.Equal([GateGoal, "22222222abcdef"], calls[1].Scope.Resolve([GateGoal, "22222222abcdef"]));
        secondRelease.SetResult();
        await AwaitEvent(coalescer.Completion, "pending refresh drained");
        Assert.Equal(2, calls.Count);

        coalescer.Request(Item(51), OwnerConsoleRefreshScope.None);
        Assert.Equal(3, calls.Count);
        await AwaitEvent(coalescer.Completion, "third invocation completed");
        Assert.Equal([Item(51)], calls[2].Items);
    }

    [Fact]
    public async Task Request_IdleSynchronousRefresh_StartsOnceAndCompletes()
    {
        var calls = 0;
        IReadOnlyList<OwnerConductEvent>? received = null;
        OwnerConsoleRefreshScope? receivedScope = null;
        var coalescer = new OwnerConsoleRefreshCoalescer((items, scope) =>
        {
            calls++;
            received = items;
            receivedScope = scope;
            return Task.CompletedTask;
        });
        coalescer.Request(null, OwnerConsoleRefreshScope.All);
        Assert.Equal(1, calls);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<OwnerConductEvent>>(received));
        Assert.Same(OwnerConsoleRefreshScope.All, receivedScope);
        await AwaitEvent(coalescer.Completion, "idle refresh completed");
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Request_DuringSynchronousFailure_PreservesReentrantRequest()
    {
        var calls = 0;
        IReadOnlyList<OwnerConductEvent>? pending = null;
        OwnerConsoleRefreshCoalescer? coalescer = null;
        coalescer = new((items, scope) =>
        {
            calls++;
            if (calls == 1)
            {
                coalescer!.Request(Item(2), OwnerConsoleRefreshScope.None);
                throw new InvalidOperationException("synchronous failure");
            }
            pending = items;
            return Task.CompletedTask;
        });
        coalescer.Request(Item(1), OwnerConsoleRefreshScope.None);
        await AwaitEvent(coalescer.Completion, "reentrant pending refresh completed");
        Assert.Equal(2, calls);
        Assert.Equal([Item(2)], pending);
    }

    [Fact]
    public void Union_AllNoneAndGoalScopes_PreservesRequestedGoals()
    {
        var first = OwnerConsoleRefreshScope.For(["57cf8f07"]);
        var second = OwnerConsoleRefreshScope.For(["22222222"]);
        Assert.Same(OwnerConsoleRefreshScope.All, OwnerConsoleRefreshScope.All.Union(first));
        Assert.Same(OwnerConsoleRefreshScope.All, first.Union(OwnerConsoleRefreshScope.All));
        Assert.Same(first, OwnerConsoleRefreshScope.None.Union(first));
        Assert.Same(first, first.Union(OwnerConsoleRefreshScope.None));
        Assert.Equal([GateGoal, "22222222abcdef"], first.Union(second).Resolve([GateGoal, "22222222abcdef", "other"]));
    }

    [Fact]
    public async Task BuildBoard_UnchangedFiles_AddsNoReadsOrParses()
    {
        using var fixture = new BoardFixture();
        var builder = fixture.Builder(fixture.ConductTail, fixture.DurationTail);
        var initial = await builder.BuildBoardAsync();
        Assert.True(Assert.IsType<OwnerConsoleGateProgress>(initial.Gate).Live);
        Assert.Contains("output moving", Assert.Single(initial.Board).Stage);
        Assert.True(fixture.ConductTail.ParseCalls > 0);
        Assert.True(fixture.DurationTail.ParseCalls > 0);
        Assert.Equal(new FileInfo(fixture.Workspace.ConductEventsLogPath).Length, fixture.ConductTail.ContentBytesRead);
        Assert.Equal(new FileInfo(fixture.DurationPath).Length, fixture.DurationTail.ContentBytesRead);
        var conductCounts = (fixture.ConductTail.ContentBytesRead, fixture.ConductTail.ParseCalls);
        var durationCounts = (fixture.DurationTail.ContentBytesRead, fixture.DurationTail.ParseCalls);

        await builder.BuildBoardAsync();

        Assert.Equal(conductCounts, (fixture.ConductTail.ContentBytesRead, fixture.ConductTail.ParseCalls));
        Assert.Equal(durationCounts, (fixture.DurationTail.ContentBytesRead, fixture.DurationTail.ParseCalls));
    }

    [Fact]
    public async Task BuildBoard_ThreeCycles_MatchesFreshModelsAndCliTicks()
    {
        using var fixture = new BoardFixture();
        var builder = fixture.Builder(fixture.ConductTail, fixture.DurationTail);
        var tickReader = new OwnerConsoleLatestTickReader(fixture.ConductTail, fixture.Workspace.RunEventStorePath);
        byte[]? previous = null;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            if (cycle == 2) fixture.AppendProgress();
            var incremental = await builder.BuildBoardAsync();
            var fresh = await fixture.Builder(new(fixture.Workspace.ConductEventsLogPath), new(fixture.DurationPath)).BuildBoardAsync();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(incremental);
            Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(fresh), bytes);
            Assert.Equal(ConductorStatusSnapshotReader.LatestTick(fixture.Workspace), tickReader.Read());
            Assert.Equal(fixture.Clock.Now.AddSeconds(cycle == 2 ? -5 : -20), tickReader.Read());
            Assert.Contains("output moving", Assert.Single(incremental.Board).Stage);
            if (cycle == 1) Assert.Equal(previous, bytes);
            if (cycle == 2) Assert.NotEqual(Convert.ToBase64String(previous!), Convert.ToBase64String(bytes));
            previous = bytes;
        }
    }

    private static OwnerConductEvent Item(int number) => new(
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(number), "tick", GateGoal, number.ToString());

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AwaitEvent(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException ex) { throw new TimeoutException("Hang guard: " + name + " was not observed.", ex); }
    }

    private sealed class BoardFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "incremental-board-" + Guid.NewGuid().ToString("N"));
        private readonly OwnerConsoleHarness _harness;
        internal readonly OwnerConsoleTestClock Clock = new();
        internal readonly OrchestratorWorkspace Workspace;
        internal readonly OwnerConsoleJsonlTail ConductTail;
        internal readonly OwnerConsoleJsonlTail DurationTail;
        internal string DurationPath => Path.Combine(_root, "durations.jsonl");
        private string ManifestPath => Path.Combine(_root, "manifest.json");

        internal BoardFixture()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Directory.CreateDirectory(Path.GetDirectoryName(Workspace.ConductEventsLogPath)!);
            ConductTail = new(Workspace.ConductEventsLogPath);
            DurationTail = new(DurationPath);
            _harness = new(Clock);
            _harness.AddGoal(GateGoal, "Gate", AgentRole.Developer);
            var snapshot = _harness.Kernel.ExportSnapshot();
            _harness.Kernel.ReplaceWithSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal with
                {
                    Status = GoalStatus.Verifying,
                    Tasks = goal.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray(),
                    Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "state changed", Clock.Now.AddMinutes(-6))]
                }).ToArray()
            });
            File.WriteAllText(ManifestPath,
                """{"engine":{"infrastructureTestLanes":[{"name":"A","estimatedSerialSeconds":100}]}}""");
            File.WriteAllText(DurationPath, Duration(100));
            File.WriteAllText(Workspace.ConductEventsLogPath,
                Event(-120, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=57cf8f07 train=owner") +
                Progress(-60, 100) + Progress(-30, 200) + Event(-20, "tick", "TICK tick=1"));
        }

        internal OwnerConsoleViewModelBuilder Builder(OwnerConsoleJsonlTail conduct, OwnerConsoleJsonlTail duration)
        {
            var gate = new OwnerConsoleGateProgressLoader(Workspace.ConductEventsLogPath, ManifestPath, DurationPath,
                Clock, conductTail: conduct, durationTail: duration);
            var motion = new OwnerGateMotionReader(Workspace.ConductEventsLogPath, Clock, conduct);
            var tick = new OwnerConsoleLatestTickReader(conduct, Workspace.RunEventStorePath);
            return new(_harness.State, _harness.Questions, _harness.Liveness, new Epics(), Clock,
                gateMotion: motion.Describe, latestTick: tick.Read, gateProgress: gate.Load);
        }

        internal void AppendProgress()
        {
            File.AppendAllText(Workspace.ConductEventsLogPath, Progress(-10, 300) + Event(-5, "tick", "TICK tick=2"));
            File.AppendAllText(DurationPath, Duration(200));
        }

        private string Duration(double seconds) => JsonSerializer.Serialize(new
        { laneName = "infrastructure tests: A", durationSeconds = seconds, recordedAt = Clock.Now.AddDays(-1) }) + "\n";

        private string Progress(int seconds, int bytes) => Event(seconds, "gate-progress",
            $"PHASE_PROGRESS goal=57cf8f07 members=57cf8f07 phase=verification-check target=\"infrastructure tests: A\" elapsed_ms=10000 child_pid=123 output_bytes={bytes}");

        private string Event(int seconds, string kind, string detail) => JsonSerializer.Serialize(new
        { timestamp = Clock.Now.AddSeconds(seconds), eventKind = kind, goalId = "57cf8f07", detail }) + "\n";

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }
}
