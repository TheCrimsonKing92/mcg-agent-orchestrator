using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private repositories, stdin-gated fixture workers and injected time.
public sealed class ProviderUsageHoldEventsScenarioTests
{
    private const string Exhausted = "API error (status 402 Payment Required): Grok Build usage balance exhausted";
    private const string Key = "xai::<provider-default>";

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ExhaustionThenSuccessfulProbe_AnnouncesThreeTimedTransitions()
    {
        using var scenario = new Scenario();
        var goal = scenario.Incident();
        var source = $"{goal.Id.Value[..8]}/{goal.Tasks.Single().Id.Value[..8]}";
        scenario.At("2026-10-17T11:09:00Z");
        scenario.Tick(goal);
        var placed = Assert.Single(Events(scenario));
        Assert.Equal("provider-hold-placed", placed.EventKind);
        Assert.Equal(goal.Id.Value, placed.GoalId);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T11:08:00Z"), placed.Timestamp);
        Assert.Equal($"PROVIDER_HOLD_PLACED source={source} placed_at=2026-10-17T11:08:00.0000000+00:00 next_probe=2026-10-17T11:38:00.0000000+00:00 reset_source=backoff attempt=1 binding={Key}", placed.Detail);
        foreach (var at in new[] { "2026-10-17T11:20:00Z", "2026-10-17T11:37:00Z" })
        {
            scenario.At(at);
            scenario.Tick(goal);
            Assert.Single(Events(scenario));
        }
        scenario.At("2026-10-17T11:38:00Z");
        scenario.Tick(goal);
        scenario.AssertPrimary(goal, probe: true);
        var probe = Assert.Single(Events(scenario).Where(evt => evt.EventKind == "provider-hold-probe"));
        Assert.Equal(goal.Id.Value, probe.GoalId);
        Assert.Equal(scenario.Clock.UtcNow, probe.Timestamp);
        Assert.Equal($"PROVIDER_HOLD_PROBE probe={source} due=2026-10-17T11:38:00.0000000+00:00 attempt=1 binding={Key}", probe.Detail);
        scenario.At("2026-10-17T11:40:00Z");
        scenario.Finish(goal, 0, "Success", "");
        var events = Events(scenario);
        Assert.Equal(3, events.Count);
        var restored = events[2];
        Assert.Equal("provider-hold-restored", restored.EventKind);
        Assert.Equal(goal.Id.Value, restored.GoalId);
        Assert.Equal(scenario.Clock.UtcNow, restored.Timestamp);
        Assert.Equal($"PROVIDER_HOLD_RESTORED source={source} placed_at=2026-10-17T11:08:00.0000000+00:00 held_seconds=1920 binding={Key}", restored.Detail);
        Assert.Equal(new[] { "Provider back: xai is accepting work again", "Provider check: trying one task on xai", "Provider paused: xai has no usage balance left" },
            OwnerActivityNarrator.Narrate(events, _ => "").Select(row => row.Phrase));
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void Restart_SkipsMalformedLinesAndDeduplicatesHoldAndProbe()
    {
        using var scenario = new Scenario();
        var goal = scenario.Incident();
        var original = File.ReadAllText(scenario.Writer.CurrentPath);
        File.WriteAllText(scenario.Writer.CurrentPath, "not json\n" + original);
        scenario.Writer.Append("provider-hold-placed", goal.Id.Value, "PROVIDER_HOLD_PLACED source=bad placed_at=invalid");
        var before = File.ReadAllBytes(scenario.Writer.CurrentPath);
        scenario.At("2026-10-17T11:20:00Z");
        new ConductorProviderHoldWatch(scenario.Writer).ObserveTick(scenario.Kernel.ProviderHoldScope, scenario.Clock.UtcNow);
        Assert.Equal(before, File.ReadAllBytes(scenario.Writer.CurrentPath));
        scenario.Tick(goal);
        scenario.At("2026-10-17T11:38:00Z");
        scenario.Tick(goal);
        Assert.Single(Events(scenario).Where(evt => evt.EventKind == "provider-hold-probe"));
        before = File.ReadAllBytes(scenario.Writer.CurrentPath);
        new ConductorProviderHoldWatch(scenario.Writer).ObserveTick(scenario.Kernel.ProviderHoldScope, scenario.Clock.UtcNow);
        Assert.Equal(before, File.ReadAllBytes(scenario.Writer.CurrentPath));
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ExhaustedProbe_AnnouncesNewPlacementWithProjectedSchedule()
    {
        using var scenario = new Scenario();
        var goal = scenario.Incident();
        scenario.At("2026-10-17T11:09:00Z");
        scenario.Tick(goal);
        scenario.At("2026-10-17T11:38:00Z");
        scenario.Tick(goal);
        scenario.AssertPrimary(goal, probe: true);
        scenario.At("2026-10-17T11:39:00Z");
        scenario.Finish(goal, 1, "", Exhausted);
        var schedule = ProviderBudgetHoldSchedules.Evaluate(scenario.Kernel.ProviderHoldScope, scenario.Hold, scenario.Clock.UtcNow);
        scenario.At("2026-10-17T11:40:00Z");
        scenario.Tick(goal);
        var placed = Events(scenario).Where(evt => evt.EventKind == "provider-hold-placed").ToArray();
        Assert.Equal(2, placed.Length);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T11:39:00Z"), placed[1].Timestamp);
        Assert.Equal("2026-10-17T11:39:00.0000000+00:00", OwnerActivityNarrator.Field(placed[1], "placed_at"));
        Assert.Equal(ProviderBudgetHoldSchedules.FormatInstant(schedule.NextProbeAt), OwnerActivityNarrator.Field(placed[1], "next_probe"));
        Assert.Equal(schedule.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture), OwnerActivityNarrator.Field(placed[1], "attempt"));
        Assert.DoesNotContain(Events(scenario), evt => evt.EventKind == "provider-hold-restored");
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public void Restart_AfterHoldClears_AppendsOneRestorationFromLoggedSource()
    {
        using var scenario = new Scenario();
        var goal = scenario.Incident();
        scenario.At("2026-10-17T11:40:00Z");
        var restarted = new ConductorProviderHoldWatch(scenario.Writer);
        restarted.ObserveTick([], scenario.Clock.UtcNow);
        restarted.ObserveTick([], scenario.Clock.UtcNow);
        var restored = Assert.Single(Events(scenario).Where(evt => evt.EventKind == "provider-hold-restored"));
        Assert.Equal(goal.Id.Value, restored.GoalId);
        Assert.Equal($"PROVIDER_HOLD_RESTORED source={goal.Id.Value[..8]}/{goal.Tasks.Single().Id.Value[..8]} placed_at=2026-10-17T11:08:00.0000000+00:00 held_seconds=1920 binding={Key}", restored.Detail);
    }

    private static IReadOnlyList<OwnerConductEvent> Events(Scenario scenario) => File.ReadAllLines(scenario.Writer.CurrentPath)
        .Select(line => ConductEventFileSource.TryParse(line, out var evt) ? evt : null)
        .OfType<OwnerConductEvent>().Where(evt => evt.EventKind.StartsWith("provider-hold-", StringComparison.Ordinal)).ToArray();

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-17T11:07:00Z");
    }

    private sealed class Scenario : WorkerDispatchTestSupport, IDisposable
    {
        internal readonly string Root = CreateSeededDispatchRepository();
        internal readonly FakeClock Clock = new();
        internal readonly AgentOrchestratorKernel Kernel;
        internal readonly OrchestratorWorkspace Workspace;
        internal readonly AgentDefinition Primary = ProviderHoldAgentSubstitutionTests.Scenario.Agent("xai-tester", "xAI", "grok-cli", "grok-4.7");
        internal readonly AgentDefinition Alternate = ProviderHoldAgentSubstitutionTests.Scenario.Agent("openai-tester", "OpenAI", "codex-cli", "gpt-6.1-sol");
        internal IReadOnlyList<AgentDefinition> Agents;
        internal WorkerProfileCatalog Profiles = WorkerProfileCatalog.Default();
        internal readonly Dictionary<GoalId, SubscriptionStartResult> Starts = [];
        internal readonly List<RetryCause> Causes = [];
        internal readonly List<string> Escalations = [];
        private int _goalSequence;
        private readonly WorkerSandboxOptions _sandbox = new(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
        private readonly ConductorDriver _driver;
        internal readonly ConductEventLogWriter Writer;
        internal readonly ConductorProviderHoldWatch Watch;

        internal Scenario(bool retryOnHeldBinding = true, bool alternate = false)
        {
            Kernel = new(Clock);
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Writer = new ConductEventLogWriter(Workspace.ConductEventsLogPath, utcNow: () => Clock.UtcNow);
            Watch = new ConductorProviderHoldWatch(Writer);
            Agents = alternate ? [Primary, Alternate] : [Primary];
            var repository = InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);
            _driver = new ConductorDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true), getRunningPaidWorkerCount: () => 0,
                createWorkspace: _ => throw new InvalidOperationException("Fixture worktree must already exist."),
                dispatchAndStart: goal =>
                {
                    using var worker = new PreparationWorker();
                    var result = new GoalDispatchOperations().StartSubscriptionReadyTasks(Kernel, Workspace, goal, Agents, Profiles,
                        checkpointBeforeWorkerStart: (kernel, _, _, _) => repository.SaveAsync(kernel).GetAwaiter().GetResult(),
                        runner: new BackgroundDispatchRunner(disableProcessStart: false, startProcess: worker.Start),
                        sandboxOptions: _sandbox, commandExists: _ => true, cascadeTesterCheapFirst: false,
                        admitProviderBudgetProbe: true, utcNow: () => Clock.UtcNow);
                    Starts[goal.Id] = result;
                    Assert.Equal(result.Dispatches.Count, result.Processes.Tasks.Count);
                    return result.Dispatches.Count > 0
                        ? DispatchStartOutcome.Started(result.Dispatches.Select(item => item.Task).ToArray())
                        : DispatchStartOutcome.EmptyBatch(string.Join("\n", result.BlockedDiagnostics.Select(item => item.ToLine())));
                },
                startRecordedDispatches: null, buildServerShutdown: null,
                runAcceptanceVerification: _ => throw new InvalidOperationException("Fixture must not run acceptance."),
                runAdvisorySemanticAcceptance: null, retryTask: null, recordTaskNote: (goalId, taskId, text) => Kernel.RecordTaskNote(goalId, taskId, text),
                recordCriterionRetryFeedback: null, clearCriterionRetryFeedback: null,
                rebaseOntoMain: _ => throw new InvalidOperationException("Fixture must not rebase."),
                land: (_, _) => throw new InvalidOperationException("Fixture must not land."),
                afterSuccessfulLanding: null, record: _ => { },
                cleanup: _ => throw new InvalidOperationException("Fixture must not clean a goal."),
                writeEscalation: (_, _, reason) => Escalations.Add(reason), classifyChangeRisk: _ => null,
                retryTaskWithCause: (goalId, taskId, message, round, cause) =>
                {
                    Causes.Add(cause);
                    return Kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: round);
                }, utcNow: () => Clock.UtcNow);
            _driver.ProviderHoldRedispatch = new ProviderHoldFailedGoalRedispatch(Kernel,
                (kernel, _) => new ProviderHoldAgentSubstitution(kernel, Agents, Preflight), retryOnHeldBinding);
        }

        internal ProviderBudgetExhaustionHold Hold
        {
            get
            {
                Assert.True(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(Kernel.Goals, "xAI", null, out var hold));
                return hold;
            }
        }
        internal void At(string text) => Clock.UtcNow = DateTimeOffset.Parse(text);
        internal Goal Current(Goal goal) => Kernel.GetGoal(goal.Id);
        internal ConductorAdvanceResult Tick(Goal goal)
        {
            var result = _driver.AdvanceOnce(Current(goal), ConductorAutonomyPolicy.Permissive);
            Observe();
            return result;
        }
        private void Observe() => Watch.ObserveTick(Kernel.ProviderHoldScope, Clock.UtcNow);
        internal Goal NewGoal(int count = 1)
        {
            var goal = InfrastructureTestSupport.MarkGoalRefined(Kernel, Kernel.CreateGoal(new GoalId((++_goalSequence).ToString("x8") + new string('0', 24)), "Verify the change.",
                Enumerable.Range(0, count).Select(index => new TaskSpec(new TaskId((_goalSequence * 100 + index).ToString("x8") + new string('0', 24)), $"Verify fixture check {index} without editing repository files.", AgentRole.Tester)).ToArray()));
            Kernel.ActivateGoal(goal.Id, Agents);
            GoalWorktrees.Ensure(Root, goal.Id);
            return goal;
        }
        internal Goal Incident(string stdout = "", string stderr = Exhausted)
        {
            var goal = NewGoal();
            Tick(goal);
            AssertPrimary(goal, probe: false);
            At("2026-10-17T11:08:00Z");
            Finish(goal, 1, stdout, stderr);
            return Current(goal);
        }
        internal void Finish(Goal goal, int exit, string stdout, string stderr, TaskId? taskId = null)
        {
            goal = Current(goal);
            var task = taskId is { } id ? goal.Tasks.Single(item => item.Id == id) : goal.Tasks.Single();
            var dispatch = task.LastDispatch!;
            var kind = WorkerProviderCatalog.Default().ResolveProfile("grok-cli").ParseOutcome(new(exit, stdout, stderr));
            Kernel.RecordTaskVerification(goal.Id, task.Id, new(dispatch.Command, dispatch.WorkingDirectory, exit,
                stdout, stderr, Clock.UtcNow, ProviderFailureKind: kind, DispatchStartedAt: dispatch.DispatchedAt));
            if (task.LastProcess is { } process)
                Kernel.RecordTaskProcessRefreshed(goal.Id, task.Id, process with { CompletedAt = Clock.UtcNow, ExitCode = exit }, verification: null);
            if (exit == 0) { Observe(); return; }
            Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Fixture provider failure.");
            Kernel.EscalateTaskFailure(goal.Id, task.Id, "Fixture provider failure.");
            Observe();
        }
        internal WorkerSubscriptionPreflightResult Preflight(Goal goal, TaskSpec task, IEnumerable<Goal> holds) =>
            WorkerProfileDispatcher.PreflightSubscriptionTask(goal, task, Agents, Profiles, Workspace.ResolveExecutionDirectory(goal.Id), Clock.UtcNow,
                allowGitReference: true, sandboxOptions: _sandbox, commandExists: _ => true, providerHoldScope: holds,
                cascadeTesterCheapFirst: false, claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli);
        internal string BlockedFinding(Goal goal) => Preflight(Current(goal), Current(goal).Tasks.Single(), Kernel.Goals).Findings
            .Single(finding => finding.StartsWith("blocked: code=provider-budget-exhausted;", StringComparison.Ordinal));
        internal int PrimaryDispatches() => Kernel.Goals.SelectMany(goal => goal.Tasks).SelectMany(task => task.DispatchHistory)
            .Count(dispatch => dispatch.AssignedAgentId == Primary.Id.Value);
        internal IEnumerable<ProgressEvent> ProbeNotes() => Kernel.Goals.SelectMany(goal => goal.Timeline)
            .Where(evt => ProviderBudgetHoldSchedules.TryParseProbe(evt, out _));
        internal void AssertPrimary(Goal goal, bool probe)
        {
            goal = Current(goal);
            var dispatch = Assert.Single(Starts[goal.Id].Dispatches);
            Assert.Equal(Primary.Id, dispatch.Task.AssignedAgentId);
            Assert.Equal(Primary.Id.Value, goal.Tasks.Single().LastDispatch!.AssignedAgentId);
            Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
            Assert.Equal(probe, goal.Timeline.Any(evt => ProviderBudgetHoldSchedules.TryParseProbe(evt, out _)));
            Assert.Equal(Clock.UtcNow, goal.Tasks.Single().LastDispatch!.DispatchedAt);
        }
        internal void AssertAlternate(Goal goal)
        {
            goal = Current(goal);
            var dispatch = Assert.Single(Starts[goal.Id].Dispatches);
            Assert.Equal(Alternate.Id, dispatch.Task.AssignedAgentId);
            Assert.Equal(Alternate.Id.Value, goal.Tasks.Single().LastDispatch!.AssignedAgentId);
            Assert.Single(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
        }
        internal void Pin(Goal goal)
        {
            IReadOnlyList<AgentDefinition> agents = Agents;
            var profiles = Profiles;
            Goal? current = goal;
            InfrastructureTestSupport.CaptureConsole(() => Assert.True(CliCommandDispatcher.ExecuteCommand(
                ["reassign-agent", "1", Primary.Id.Value], Kernel, Workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
        }
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }

    private sealed class PreparationWorker : IDisposable
    {
        private readonly List<(Process Process, TestChildProcessCapture Capture)> _children = [];
        internal Process Start(ProcessStartInfo requested)
        {
            var start = new ProcessStartInfo(WorkerShell.Executable)
            {
                WorkingDirectory = requested.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            foreach (var argument in WorkerShell.BaseArguments()) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("$null = [Console]::ReadLine(); Write-Output 'fixture-worker-exit'");
            Process? process = null;
            var capture = TestChildProcessCapture.Start(start, start: info => process = Process.Start(info));
            Assert.NotNull(process);
            Assert.False(process.HasExited);
            _children.Add((process, capture));
            return process;
        }
        public void Dispose()
        {
            foreach (var (process, capture) in _children)
                try
                {
                    process.StandardInput.WriteLine("release");
                    var exit = capture.Complete();
                    Assert.Equal(0, exit.ExitCode);
                    Assert.Equal("fixture-worker-exit", exit.Stdout.Trim());
                    Assert.Equal(string.Empty, exit.Stderr);
                }
                finally
                {
                    WorkerProcessJobs.Release(process.Id);
                    capture.Dispose();
                }
        }
    }
}
