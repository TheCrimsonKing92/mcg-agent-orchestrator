using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: each test owns its kernel, clock and temporary root; process records are synthetic.
public sealed class WorkerCapacityStallWatchTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(10);

    [Xunit.Fact]
    public void UnchangedDispatchSet_AtThreshold_EmitsOneDecisionWithEveryDispatch()
    {
        using var fixture = new CapacityFixture();
        fixture.Tick(0);
        fixture.Tick(5);
        Assert.Empty(fixture.Events());
        fixture.Tick(10);

        var item = Assert.Single(fixture.Events());
        Assert.Equal("worker-capacity-stalled", item.GetProperty("eventKind").GetString());
        Assert.Equal("decision", item.GetProperty("operator").GetString());
        Assert.Equal(fixture.ExpectedDetail(600), item.GetProperty("detail").GetString());
        Assert.Null(fixture.WaitingGoal.CurrentHold);
        Assert.Empty(fixture.Lines);

        // Changing queued-goal count must neither reset nor re-arm the unchanged running set.
        fixture.Tick(15, heldGoals: 2);
        fixture.Tick(20);
        Assert.Single(fixture.Events());
    }

    [Xunit.Fact]
    public void DispatchSetChanges_BeforeThreshold_RestartsStallWindow()
    {
        using var fixture = new CapacityFixture();
        fixture.Tick(0);
        fixture.ReplaceSecondDispatch(5, 333);
        fixture.Tick(5);
        fixture.Tick(10);
        fixture.Tick(12);
        Assert.Empty(fixture.Events());

        fixture.Tick(15);
        Assert.Equal(fixture.ExpectedDetail(600), Assert.Single(fixture.Events()).GetProperty("detail").GetString());
        fixture.Tick(20);
        Assert.Single(fixture.Events());
    }

    [Xunit.Fact]
    public void DispatchSetChanges_AfterEmission_RearmsForNewSet()
    {
        using var fixture = new CapacityFixture();
        fixture.Tick(0);
        fixture.Tick(10);
        Assert.Single(fixture.Events());
        fixture.ReplaceSecondDispatch(11, 333);
        fixture.Tick(11);
        fixture.Tick(20);
        Assert.Single(fixture.Events());

        fixture.Tick(21);
        Assert.Equal(2, fixture.Events().Count);
        Assert.Equal(fixture.ExpectedDetail(600), fixture.Events()[1].GetProperty("detail").GetString());
    }

    [Xunit.Fact]
    public void QuietTick_PastThreshold_PreservesBaselineWithoutEmission()
    {
        using var fixture = new CapacityFixture();
        fixture.Tick(0);
        fixture.Tick(11, heldGoals: 0);
        Assert.Empty(fixture.Events());

        fixture.Tick(12);
        Assert.Equal(fixture.ExpectedDetail(720), Assert.Single(fixture.Events()).GetProperty("detail").GetString());
    }

    [Xunit.Fact]
    public void ReorderedGoals_UnchangedIdentities_DoesNotRestartWindow()
    {
        using var fixture = new CapacityFixture();
        fixture.Tick(0);
        var snapshot = fixture.Kernel.ExportSnapshot();
        fixture.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Reverse().ToArray() });

        fixture.Tick(10);

        Assert.Equal(fixture.ExpectedDetail(600), Assert.Single(fixture.Events()).GetProperty("detail").GetString());
    }

    [Xunit.Fact]
    public void EmptyRunningSet_PastThreshold_ReportsNoDispatchesOnce()
    {
        using var fixture = new CapacityFixture(runningCount: 0);
        fixture.Tick(0);
        fixture.Tick(10);
        fixture.Tick(11);

        Assert.Equal("WORKER_CAPACITY_STALLED heldAtCapacity=1 unchangedForSeconds=600 running=0 dispatches=none",
            Assert.Single(fixture.Events()).GetProperty("detail").GetString());
    }

    [Xunit.Fact]
    public void RequiredAppendFails_UnchangedSet_AttemptsOnlyOnce()
    {
        using var fixture = new CapacityFixture(failAppend: true);
        fixture.Tick(0);
        fixture.Tick(10);
        fixture.Tick(11);

        Assert.Equal(1, fixture.AppendAttempts);
        Assert.Empty(fixture.Events());
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void Run_CapacityHeldTicks_EmitsOneStallAndResetsForNextRun()
    {
        using var fixture = new CapacityFixture();
        fixture.Run(3);

        var item = Assert.Single(fixture.Events());
        Assert.Equal("decision", item.GetProperty("operator").GetString());
        Assert.Equal(fixture.ExpectedDetail(660), item.GetProperty("detail").GetString());
        Assert.DoesNotContain(fixture.Lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
        Assert.Empty(fixture.Events("goal-escalation"));
        Assert.Empty(fixture.Events("goal-stalled"));
        Assert.Null(fixture.WaitingGoal.CurrentHold);

        fixture.Now = fixture.Now.AddMinutes(11);
        fixture.Run(1);
        Assert.Single(fixture.Events());
    }

    private sealed class CapacityFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "capacity-watch-" + Guid.NewGuid().ToString("N"));
        private readonly ConductorDriver _driver;
        private readonly ConductorBatchLoop _loop;
        private readonly ConductEventLogWriter _conduct;
        private readonly HashSet<GoalId> _changed = [];
        private readonly GoalId _waitingId;
        private readonly GoalId _otherWaitingId;
        private GoalId _secondRunningId;
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal WaitingGoal => Kernel.GetGoal(_waitingId);
        internal List<string> Lines { get; } = [];
        internal DateTimeOffset Now = Start;
        internal int AppendAttempts;

        internal CapacityFixture(int runningCount = 2, bool failAppend = false)
        {
            var (kernel, waiting) = SimpleGoal();
            Kernel = kernel;
            _waitingId = waiting.Id;
            _otherWaitingId = AddGoal("Another waiting goal").Id;
            Directory.CreateDirectory(_root);
            if (runningCount > 0) StartDispatch(AddGoal("First running goal"), 111, Start);
            if (runningCount > 1)
            {
                var second = AddGoal("Second running goal");
                _secondRunningId = second.Id;
                StartDispatch(second, 222, Start);
            }
            _driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningCount: () => Kernel.Goals.Sum(goal => goal.Tasks.Count(task => task.LastProcess is { IsRunning: true })),
                dispatchAndStart: _ => throw new InvalidOperationException("A full worker cap must prevent dispatch."),
                hasGateReadyGoal: () => false,
                utcNow: () => Now);
            _conduct = new ConductEventLogWriter(Path.Combine(_root, "conduct.log"), utcNow: () => Now,
                beforeRequiredEventDrain: () =>
                {
                    AppendAttempts++;
                    if (failAppend) throw new IOException("forced capacity event append failure");
                });
            _loop = new ConductorBatchLoop(conductEventLogWriter: _conduct, utcNow: () => Now);
        }

        internal void Tick(int minutes, int heldGoals = 1)
        {
            Now = Start.AddMinutes(minutes);
            var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.WorkspaceReady, "At worker cap (2/2); will advance when a slot opens")
            { Owner = ConductorHoldOwner.WorkerCapacity };
            foreach (var goal in new[] { WaitingGoal, Kernel.GetGoal(_otherWaitingId) }.Take(heldGoals))
                _loop.TrackGoalOutcomeAndCapacity(Kernel, _driver, goal, held, Now, Threshold, _changed, Lines, _conduct);
            _loop.ObserveWorkerCapacityTick(Kernel, Threshold);
        }

        internal void ReplaceSecondDispatch(int minutes, int pid)
        {
            var goal = Kernel.GetGoal(_secondRunningId);
            var task = goal.Tasks.Single();
            Kernel.RecordTaskProcessCancelled(goal.Id, task.Id, task.LastProcess! with
            { CompletedAt = Start.AddMinutes(minutes), ExitCode = -1, WasCancelled = true });
            Assert.False(task.LastProcess!.IsRunning);
            var successor = AddGoal("Successor running goal");
            _secondRunningId = successor.Id;
            StartDispatch(successor, pid, Start.AddMinutes(minutes));
            Assert.Equal(2, Kernel.Goals.Sum(item => item.Tasks.Count(work => work.LastProcess is { IsRunning: true })));
        }

        internal void Run(int ticks)
        {
            var summary = _loop.Run(Kernel, _driver,
                ConductorAutonomyPolicy.Conservative with { MaxConcurrentPaidWorkers = 2 },
                Path.Combine(_root, "stop"), onlyGoalId: _waitingId.Value,
                maxIterations: ticks, watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => { Now = Now.AddMinutes(11); return false; },
                onTick: tick => Lines.AddRange(tick.ProgressLines ?? []), goalStallThreshold: Threshold);
            Assert.Equal(ticks, summary.Ticks);
            Assert.Equal(ticks, summary.Held);
            Assert.Contains(WaitingGoal.Timeline,
                item => item.Message.Contains("At worker cap (2/2)", StringComparison.Ordinal));
        }

        internal string ExpectedDetail(int seconds)
        {
            var running = Kernel.Goals.SelectMany(goal => goal.Tasks
                .Where(task => task.LastProcess is { IsRunning: true })
                .Select(task => (Goal: goal.Id.Value, Task: task.Id.Value, Pid: task.LastProcess!.ProcessId)))
                .OrderBy(item => item.Goal[..8], StringComparer.Ordinal).ThenBy(item => item.Task[..8], StringComparer.Ordinal)
                .ThenBy(item => item.Pid).ThenBy(item => item.Goal, StringComparer.Ordinal)
                .ThenBy(item => item.Task, StringComparer.Ordinal).ToArray();
            Assert.Equal(2, running.Length);
            var dispatches = string.Join(",", running.Select(item => $"goal:{item.Goal[..8]}/task:{item.Task[..8]}/pid:{item.Pid}"));
            return $"WORKER_CAPACITY_STALLED heldAtCapacity=1 unchangedForSeconds={seconds} running=2 dispatches={dispatches}";
        }

        internal List<JsonElement> Events(string kind = "worker-capacity-stalled")
        {
            if (!File.Exists(_conduct.CurrentPath)) return [];
            return File.ReadAllLines(_conduct.CurrentPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(item => item.GetProperty("eventKind").GetString() == kind).ToList();
        }

        private Goal AddGoal(string objective) => GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, DefaultAgents(), objective);

        private void StartDispatch(Goal goal, int pid, DateTimeOffset at)
        {
            var task = goal.Tasks.Single();
            Kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("test-worker", "worker", _root, at));
            Kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(pid, "worker", _root,
                Path.Combine(_root, $"{pid}.out"), Path.Combine(_root, $"{pid}.err"), Path.Combine(_root, $"{pid}.exit"),
                at, null, null));
            Assert.True(task.LastProcess!.IsRunning);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
