using System.Collections.Concurrent;
using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique artifact/stop paths, injected process/git/clock seams and AsyncLocal census/capture.
public sealed class ConductorContinuitySupervisorDeliberateHoldProbeTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public async Task GatherHold_SixMinutesOfRealTicks_AdoptsWithoutReverting()
    {
        await using var scenario = new Scenario();
        await scenario.StartAsync();
        await scenario.RunLoopAsync("gather");
        Assert.Equal(TimeSpan.FromMinutes(6), scenario.Clock.GetUtcNow() - DateTimeOffset.UnixEpoch);
        AssertProbes(scenario, 0);
        var gathers = scenario.Forwarded.Where(item => item.Line.Contains("outcome=gathering", StringComparison.Ordinal)).ToArray();
        Assert.Equal(7, gathers.Length);
        Assert.Contains("remaining_seconds=480", gathers[0].Line);
        Assert.Contains("remaining_seconds=120", gathers[^1].Line);
        Assert.DoesNotContain(scenario.Events.Snapshot(), evt => evt.Status == "reverted");
        Assert.DoesNotContain(scenario.Lines, line => line.StartsWith("ACTIVATION_REVERTED ", StringComparison.Ordinal));
        await scenario.FinishAsync();
        AssertAdopted(scenario);
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public async Task AdmissibleAttempt_LeaseBusyForSixMinutes_RevertsAndRestores()
    {
        await using var scenario = new Scenario();
        await scenario.StartAsync();
        await scenario.RunLoopAsync("busy");
        Assert.Equal(TimeSpan.FromMinutes(6), scenario.Clock.GetUtcNow() - DateTimeOffset.UnixEpoch);
        AssertProbes(scenario, 1);
        Assert.Equal(7, scenario.Forwarded.Count(item => item.Line.StartsWith("ADMISSION ", StringComparison.Ordinal) &&
            item.Line.Contains("reason=acceptance-artifact-writer-busy", StringComparison.Ordinal)));
        await scenario.WaitForExitAsync();
        var reverted = Assert.Single(scenario.Events.Snapshot(), evt => evt.Status == "reverted");
        Assert.Contains("reason=ProbeNotAdmitted", reverted.Detail);
        Assert.StartsWith("ACTIVATION_RESTORED ", Assert.Single(scenario.Events.Snapshot(), evt => evt.Status == "restored").Detail);
        Assert.DoesNotContain(scenario.Events.Snapshot(), evt => evt.Status == "adopted");
        Assert.Contains(scenario.Lines, line => line.StartsWith("ACTIVATION_REVERTED ", StringComparison.Ordinal) &&
            line.Contains("reason=ProbeNotAdmitted", StringComparison.Ordinal));
        Assert.Contains(scenario.Lines, line => line.StartsWith("ACTIVATION_RESTORED ", StringComparison.Ordinal));
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public async Task NoVerifiedGoal_RealLoop_EqualsLiteralZeroWaitingBaseline()
    {
        await using var actual = new Scenario();
        await actual.StartAsync();
        await actual.RunLoopAsync("empty");
        AssertProbes(actual, 0);
        await actual.FinishAsync();

        await using var baseline = new Scenario();
        await baseline.StartAsync();
        for (var tick = 1; tick <= 7; tick++)
        {
            baseline.Forward($"ACTIVATION_ADMISSION_PROBE tick={tick} phase=none attempt=none waiting=0");
            baseline.Forward(new ActivationAdmissionSample(tick, false, 0, 0, 0).Format());
            baseline.Forward($"TICK_END tick={tick} activation=true");
            await baseline.DrainTickAsync();
            if (tick < 7) baseline.Clock.Advance(TimeSpan.FromSeconds(60));
        }
        await baseline.FinishAsync();

        AssertAdopted(actual);
        AssertAdopted(baseline);
        Assert.Equal(ActivationOutcomes(baseline), ActivationOutcomes(actual));
        var expected = Replay(baseline);
        var observed = Replay(actual);
        Assert.Equal(ActivationAdmissionProbeState.Skipped, observed.State);
        Assert.Equal("no-verified-goal-waiting", observed.SkipReason);
        Assert.Equal(expected, observed);
    }

    private static void AssertProbes(Scenario scenario, int waiting)
    {
        var probes = scenario.Forwarded.Where(item => item.Line.StartsWith(ActivationAdmissionProbeLine.Prefix, StringComparison.Ordinal)).ToArray();
        Assert.Equal(7, probes.Length);
        Assert.Equal(Enumerable.Range(0, 7).Select(tick => DateTimeOffset.UnixEpoch.AddMinutes(tick)),
            probes.Select(item => item.At));
        Assert.All(probes, item =>
        {
            Assert.True(ActivationAdmissionProbeLine.TryParse(item.Line, out var sample));
            Assert.Equal(waiting, sample.Waiting);
        });
    }

    private static void AssertAdopted(Scenario scenario)
    {
        Assert.Single(scenario.Events.Snapshot(), evt => evt.Operation == "activation" && evt.Status == "adopted");
        var lines = scenario.Lines.ToArray();
        var skipped = Array.FindIndex(lines, line => line.StartsWith("ACTIVATION_PROBE_SKIPPED reason=no-verified-goal-waiting ", StringComparison.Ordinal));
        var adopted = Array.FindIndex(lines, line => line.StartsWith("ACTIVATION_ADOPTED ", StringComparison.Ordinal));
        Assert.True(skipped >= 0 && adopted > skipped);
        Assert.DoesNotContain(scenario.Events.Snapshot(), evt => evt.Status is "reverted" or "restored" or "failed-both");
    }

    private static (string? Status, string? Reason)[] ActivationOutcomes(Scenario scenario) => scenario.Events.Snapshot()
        .Where(evt => evt.Operation == "activation")
        .Select(evt => (evt.Status, evt.Detail?.Split(' ').SingleOrDefault(field => field.StartsWith("reason=", StringComparison.Ordinal))))
        .ToArray();

    private static ActivationAdmissionProbeVerdict Replay(Scenario scenario)
    {
        var tracker = new ActivationAdmissionProbeTracker(3);
        foreach (var item in scenario.Forwarded) tracker.Observe(item.Line, item.At);
        return tracker.Evaluate(scenario.Clock.GetUtcNow());
    }

    internal sealed class LoopFixture
    {
        internal AgentOrchestratorKernel Kernel { get; }
        internal ConductorDriver Driver { get; }
        internal ConductorAutonomyPolicy Policy { get; }
        internal int SoloCalls;
        internal int WriterLeaseCalls;

        internal LoopFixture(string root, Func<DateTimeOffset> now, string mode)
        {
            var kernel = new AgentOrchestratorKernel(new LoopClock(now));
            var paths = new Dictionary<GoalId, IReadOnlyList<string>>();
            if (mode != "empty")
            {
                var ready = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Update src/Mcg.AgentOrchestrator.Core/Application/GatherReady.cs");
                PassVerificationAt(kernel, ready, ready.Tasks.Single(), now());
                Assert.Equal(GoalStatus.Verified, ready.Status);
                paths[ready.Id] = ["src/Mcg.AgentOrchestrator.Core/Application/GatherReady.cs"];
            }
            if (mode != "busy")
            {
                var reviewing = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Update tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GatherReviewing.cs");
                paths[reviewing.Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GatherReviewing.cs"];
                var snapshot = kernel.ExportSnapshot();
                kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
                {
                    Goals = snapshot.Goals.Select(goal => goal.Id == reviewing.Id.Value ? goal with
                    {
                        Tasks = goal.Tasks.Select(task => task with
                        {
                            RequiredRole = AgentRole.Reviewer,
                            Status = WorkTaskStatus.Running,
                            LastDispatch = new TaskDispatchSnapshot("test-worker", "test.exe", root, now())
                        }).ToArray()
                    } : goal).ToArray()
                }, new LoopClock(now));
            }
            Kernel = kernel;
            Policy = ConductorAutonomyPolicy.Permissive with { AcceptanceCohortGatherWindowSeconds = mode == "busy" ? 0 : 480 };
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, TrunkBranchName.Default,
                utcNow: now, isProcessAlive: _ => true,
                tryAcquireAttemptWriterLease: (_, _) => { WriterLeaseCalls++; return null; });
            var projector = new GateReadyCandidateProjector(
                id => new GateReadyCandidateRevisionPair(id.Value.PadRight(40, 'b')[..40], new string('a', 40)),
                id => new GateReadyLandingScopeObservation(true, paths[id]),
                (_, _, _) => new GateReadyMergeTreeObservation(true));
            Driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => { SoloCalls++; throw new InvalidOperationException("Unexpected gate execution"); },
                classifyRisk: _ => ChangeRiskTier.Behavior, getLandingFileScopes: goal => paths[goal.Id],
                isVerificationGateSatisfied: _ => true, gateReadyCandidateProjector: projector,
                parallelAcceptanceAttemptCoordinator: coordinator,
                runAcceptanceCohort: (_, _, _) => throw new InvalidOperationException("Unexpected cohort admission"), utcNow: now);
            Assert.True(Driver.AcceptanceCohortsEnabled);
        }

        private sealed class LoopClock(Func<DateTimeOffset> now) : IClock { public DateTimeOffset UtcNow => now(); }
    }

    private sealed class Scenario : IAsyncDisposable
    {
        internal readonly RecordingEvents Events = new();
        internal readonly VirtualDeadlines Clock = new();
        internal readonly ConcurrentQueue<string> Lines = new();
        internal readonly ConcurrentQueue<(string Line, DateTimeOffset At)> Forwarded = new();
        private readonly TaskCompletionSource _adopted = Signal();
        private readonly TaskCompletionSource _reverted = Signal();
        private readonly TaskCompletionSource<ConductorSupervisorProcessResult> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-deliberate-probe-{Guid.NewGuid():N}");
        private readonly MemoryStream _lease = new();
        private readonly ScriptedHost _host;
        private ConductorSupervisorProcessRequest? _successor;
        private Task<int> _run = Task.FromResult(-1);
        private Task _loop = Task.CompletedTask;
        private Task _tickObserved = Task.CompletedTask;

        internal Scenario()
        {
            Directory.CreateDirectory(_root);
            Events.OnAppend = evt =>
            {
                if (evt.Status == "adopted") _adopted.TrySetResult();
                if (evt.Status == "reverted") _reverted.TrySetResult();
            };
            _host = new ScriptedHost((request, index, token) =>
            {
                if (index == 0)
                {
                    ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("max-duration", 1, 1, true));
                    return Task.FromResult(new ConductorSupervisorProcessResult(0, 100));
                }
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                if (index == 1)
                {
                    _successor = request;
                    token.Register(() => _exit.TrySetResult(new(-1, 101, TerminationConfirmed: true)));
                    return _exit.Task;
                }
                Assert.Equal(2, index);
                for (var tick = 1; tick <= 3; tick++)
                {
                    request.OnStandardOutputLine(new ActivationAdmissionSample(tick, false, 5, 0, 0).Format());
                    request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
                }
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("stop-file", 3, 0, false));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 102));
            });
        }

        internal async Task StartAsync()
        {
            var stages = 0;
            var successor = new ConductorPreparedSuccessor("C:\\next", "C:\\next\\app.dll", "head-next", "head-next", "LOOP_START selfCheck=true", _lease);
            var supervisor = new ConductorContinuitySupervisor(_host, Events, timeProvider: Clock,
                inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)],
                stageSuccessor: _ => stages++ == 0 ? successor : null!,
                appendConductEvent: (_, _, line) => Lines.Enqueue(line),
                activationHealthyTicks: 3, activationDelay: Clock.Delay, activationStallTimeout: TimeSpan.FromHours(2),
                tickStallDelay: Clock.Delay, delay: (_, _) => Task.CompletedTask,
                changedPathsBetween: (from, to) =>
                {
                    Assert.Equal("incumbent", from);
                    Assert.Equal("head-next", to);
                    return ["src/Mcg.AgentOrchestrator.App/Orchestration/AcceptanceHolderLease.cs"];
                },
                verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"));
            _run = supervisor.RunAsync(["conduct", "--loop", "--daemon", "--poll-seconds", "4000"],
                "C:\\fake-repo", _root, "default", "default", cancellationToken: _stop.Token);
            await Task.WhenAny(Clock.WaitForCountAsync(1, _stop.Token), _run).WaitAsync(_stop.Token);
            if (_run.IsCompleted) await _run;
            Assert.NotNull(_successor);
        }

        internal async Task RunLoopAsync(string mode)
        {
            var fixture = new LoopFixture(_root, Clock.GetUtcNow, mode);
            _loop = Task.Run(() =>
            {
                using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
                using var writer = new ForwardingWriter(Forward);
                var ticks = 0;
                AsyncLocalConsoleRouter.CaptureTo(() => new ConductorBatchLoop(utcNow: Clock.GetUtcNow).Run(
                    fixture.Kernel, fixture.Driver, fixture.Policy, Path.Combine(_root, "stop"),
                    keepAliveWhenIdle: true, maxIterations: 7, emitActivationHeartbeat: true,
                    watchInterval: TimeSpan.FromSeconds(60), sleepFunc: _ => false, reloadPolicy: () =>
                    {
                        // Runs before every tick, including ticks that make progress and never sleep.
                        // The previous tick's Console.WriteLine has returned before we wait for its observer.
                        if (ticks++ > 0)
                        {
                            DrainTickAsync().GetAwaiter().GetResult();
                            Clock.Advance(TimeSpan.FromSeconds(60));
                        }
                        return new ConductorPolicyResolution(fixture.Policy, "preset", []);
                    }), writer);
            }, _stop.Token);
            await _loop.WaitAsync(_stop.Token);
            await DrainTickAsync();
            Assert.Equal(0, fixture.SoloCalls);
            if (mode == "busy") Assert.Equal(7, fixture.WriterLeaseCalls);
        }

        internal void Forward(string line)
        {
            Forwarded.Enqueue((line, Clock.GetUtcNow()));
            // One full tick, rather than each probe line, is the synchronization boundary.
            if (line.StartsWith("TICK_END ", StringComparison.Ordinal))
                _tickObserved = Clock.WaitForCountAsync(Clock.Count + 1, _stop.Token);
            _successor!.OnStandardOutputLine!(line);
        }

        internal async Task DrainTickAsync()
        {
            await Task.WhenAny(_tickObserved, _adopted.Task, _reverted.Task, _run).WaitAsync(_stop.Token);
            if (_run.IsCompleted) await _run;
        }

        internal async Task FinishAsync()
        {
            await Task.WhenAny(_adopted.Task, _reverted.Task, _run).WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(_adopted.Task.IsCompletedSuccessfully, "Successor did not reach adoption");
            ConductorContinuityExitArtifact.Write(_successor!.ExitArtifactPath, new("stop-file", 7, 0, false));
            _exit.TrySetResult(new(0, 101));
            await WaitForExitAsync();
        }

        internal async Task WaitForExitAsync() => Assert.Equal(0, await _run.WaitAsync(TestContext.Current.CancellationToken));

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await Task.WhenAll(_loop, _run); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally
            {
                _stop.Dispose();
                _lease.Dispose();
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class ForwardingWriter(Action<string> forward) : TextWriter
    {
        private readonly StringBuilder _line = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value)
        {
            if (value != '\n') { if (value != '\r') _line.Append(value); return; }
            var line = _line.ToString();
            _line.Clear();
            forward(line);
        }
    }

    private sealed class VirtualDeadlines : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<(DateTimeOffset Due, TaskCompletionSource Completion)> _pending = [];
        private TaskCompletionSource _registered = Signal();
        private int _count;
        internal int Count { get { lock (_gate) return _count; } }
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        internal Task Delay(TimeSpan duration, CancellationToken token)
        {
            if (duration <= TimeSpan.Zero) return Task.CompletedTask;
            lock (_gate)
            {
                var completion = Signal();
                token.Register(() => completion.TrySetCanceled(token));
                _pending.RemoveAll(entry => entry.Completion.Task.IsCompleted);
                _pending.Add((_now + duration, completion));
                _count++;
                var prior = _registered;
                _registered = Signal();
                prior.TrySetResult();
                return completion.Task;
            }
        }
        internal void Advance(TimeSpan duration)
        {
            lock (_gate)
            {
                _now += duration;
                foreach (var entry in _pending.Where(entry => entry.Due <= _now)) entry.Completion.TrySetResult();
                _pending.RemoveAll(entry => entry.Completion.Task.IsCompleted);
            }
        }
        internal async Task WaitForCountAsync(int count, CancellationToken token)
        {
            while (true)
            {
                Task registered;
                lock (_gate) { if (_count >= count) return; registered = _registered.Task; }
                await registered.WaitAsync(token).ConfigureAwait(false);
            }
        }
    }

    private sealed class ScriptedHost(Func<ConductorSupervisorProcessRequest, int, CancellationToken, Task<ConductorSupervisorProcessResult>> step)
        : IConductorSupervisorProcessHost
    {
        private int _index;
        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request, CancellationToken token) =>
            step(request, _index++, token);
    }

    private sealed class RecordingEvents : IRunEventStore
    {
        private readonly object _gate = new();
        private readonly List<RunEventAppend> _events = [];
        internal Action<RunEventAppend>? OnAppend;
        internal RunEventAppend[] Snapshot() { lock (_gate) return [.. _events]; }
        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _events.Add(evt);
                OnAppend?.Invoke(evt);
                return Task.FromResult(new RunEventRecord(_events.Count, evt.EventId ?? Guid.NewGuid().ToString("N"),
                    evt.OccurredAt ?? DateTimeOffset.MinValue, evt.EventType, evt.GoalId, evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));
            }
        }
        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0, string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
