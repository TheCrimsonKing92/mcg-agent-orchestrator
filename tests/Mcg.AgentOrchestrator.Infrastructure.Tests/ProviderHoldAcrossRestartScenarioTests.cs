using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private repositories/stores, fixture clock, and stdin-gated children.
public sealed class ProviderHoldAcrossRestartScenarioTests : CliCommandTestBase
{
    private const string Exhausted = "API error (status 402 Payment Required): Grok Build usage balance exhausted";

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void EvictionAndRestart_HeldBinding_WaitsForProbeThenConverges()
    {
        using var scenario = new Scenario();
        var source = scenario.Incident();
        Assert.Equal(WorkTaskStatus.Failed, source.Tasks.Single().Status);
        Assert.Equal(GoalStatus.Active, scenario.Stored(source).Status);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T11:07:00Z"), source.Tasks.Single().LastDispatch!.DispatchedAt);
        Assert.Equal(ProviderFailureKind.BudgetExhausted, source.Tasks.Single().LastVerification!.ProviderFailureKind);
        Assert.Equal(1, source.Tasks.Single().LastVerification!.ExitCode);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T11:08:00Z"), source.Tasks.Single().LastVerification!.CompletedAt);

        scenario.Agents = [scenario.Primary, scenario.Alternate];
        scenario.At("2026-10-17T11:09:00Z");
        scenario.Tick(source);
        scenario.AssertAlternate(source);
        scenario.At("2026-10-17T11:12:00Z");
        scenario.Finish(source, 0, "Success", "");
        scenario.Complete(source);
        Assert.Equal(GoalStatus.Completed, scenario.Stored(source).Status);
        Assert.Equal("OpenAI", scenario.Current(source).Tasks.Single().LastDispatch!.ProviderName);
        Assert.True(scenario.Current(source).Tasks.Single().LastVerification!.Succeeded);

        scenario.Agents = [scenario.Primary];
        var target = scenario.NewGoal();
        Assert.Equal(scenario.Primary.Id, target.Tasks.Single().AssignedAgentId);
        Assert.Equal(1, scenario.Kernel.EvictTerminalGoalAggregates([source.Id]));
        Assert.DoesNotContain(scenario.Kernel.Goals, goal => goal.Id == source.Id);
        Assert.Equal(source.Id, Assert.Single(scenario.Kernel.ProviderHoldHistory.Retained).Id);
        scenario.At("2026-10-17T11:13:00Z");
        scenario.Tick(target);
        scenario.AssertBlocked(target);

        var restarted = AgentOrchestratorKernel.FromSnapshot(
            CliPersistentStateRunner.LoadConductLoopKernel(scenario.Repository, [], scenario.Root).ExportSnapshot(), scenario.Clock);
        Assert.DoesNotContain(restarted.Goals, goal => goal.Id == source.Id);
        Assert.IsType<DispatchReadinessReady>(DispatchReadinessAssessment.Evaluate(restarted.GetGoal(target.Id),
            restarted.Goals, scenario.Agents, scenario.Profiles, scenario.Clock.UtcNow).Verdict);
        var lines = new List<string>();
        ProviderHoldHistorySeed.Apply(restarted, scenario.Repository, lines.Add);
        Assert.Equal($"PROVIDER_HOLD_HISTORY candidates=1 retained=1 goals={source.Id.Value[..8]}", Assert.Single(lines));
        Assert.Equal(source.Id, Assert.Single(restarted.ProviderHoldHistory.Retained).Id);
        scenario.Rebuild(restarted);
        scenario.At("2026-10-17T11:14:00Z");
        scenario.Tick(target);
        scenario.AssertBlocked(target);

        var schedule = ProviderBudgetHoldSchedules.Evaluate(restarted.ProviderHoldScope, scenario.Hold, scenario.Clock.UtcNow);
        scenario.Clock.UtcNow = schedule.NextProbeAt.AddMinutes(-1);
        scenario.Tick(target);
        scenario.AssertBlocked(target);
        Assert.Empty(scenario.ProbeNotes());
        scenario.Clock.UtcNow = schedule.NextProbeAt;
        scenario.Tick(target);
        scenario.AssertPrimary(target, probe: true);
        Assert.Equal(target.Tasks.Single().Id, Assert.Single(scenario.ProbeNotes()).TaskId);
        scenario.Clock.UtcNow = schedule.NextProbeAt.AddMinutes(2);
        scenario.Finish(target, 0, "Success", "");
        Assert.False(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(restarted.ProviderHoldScope, "xAI", null, out _));

        scenario.Complete(target);
        Assert.Equal(GoalStatus.Completed, scenario.Stored(target).Status);
        Assert.Equal(1, restarted.EvictTerminalGoalAggregates([target.Id]));
        Assert.Empty(restarted.ProviderHoldHistory.Retained);
        Assert.False(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(restarted.ProviderHoldScope, "xAI", null, out _));
        Assert.Empty(scenario.Escalations);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ClearingGoalEvicted_ActiveExhaustion_StaysClearedUntilBothLeave()
    {
        using var scenario = new Scenario();
        var active = scenario.Incident();
        // Observe an already-running worker's later success on the held binding.
        var clearing = scenario.NewGoal();
        scenario.At("2026-10-17T11:29:00Z");
        scenario.Kernel.RecordTaskDispatch(clearing.Id, clearing.Tasks.Single().Id,
            new("grok-cli", "fixture", scenario.Root, scenario.Clock.UtcNow,
                ProviderName: "xAI", ModelName: "grok-4.7", AssignedAgentId: scenario.Primary.Id.Value));
        scenario.Save();
        scenario.At("2026-10-17T11:30:00Z");
        scenario.Finish(clearing, 0, "Success", "");
        scenario.Complete(clearing);
        Assert.Equal(GoalStatus.Completed, scenario.Stored(clearing).Status);
        Assert.Equal(1, scenario.Kernel.EvictTerminalGoalAggregates([clearing.Id]));
        Assert.Equal(clearing.Id, Assert.Single(scenario.Kernel.ProviderHoldHistory.Retained).Id);
        Assert.Equal(GoalStatus.Active, scenario.Current(active).Status);
        Assert.False(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(scenario.Kernel.ProviderHoldScope, "xAI", null, out _));
        Assert.True(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(scenario.Kernel.Goals, "xAI", null, out _));
        scenario.Complete(active);
        Assert.Equal(1, scenario.Kernel.EvictTerminalGoalAggregates([active.Id]));
        Assert.Empty(scenario.Kernel.ProviderHoldHistory.Retained);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public void Evict_NonProviderFailure_PreservesBaselineReadiness()
    {
        using var scenario = new Scenario();
        var source = scenario.Incident(stderr: "Fixture validation failed.");
        Assert.NotEqual(ProviderFailureKind.BudgetExhausted, source.Tasks.Single().LastVerification!.ProviderFailureKind);
        scenario.Complete(source);
        var target = scenario.NewGoal();
        Assert.Equal(1, scenario.Kernel.EvictTerminalGoalAggregates([source.Id]));
        AssertBaseline(scenario.Kernel, target, scenario.Agents, scenario.Profiles, scenario.Clock.UtcNow);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public void Refresh_CancelledSuccess_PreservesBaselineReadiness()
    {
        using var scenario = new Scenario();
        var source = scenario.NewGoal();
        scenario.Tick(source);
        scenario.At("2026-10-17T11:08:00Z");
        scenario.Finish(source, 0, "Success", "");
        var target = scenario.NewGoal();
        var snapshot = scenario.Kernel.ExportSnapshot();
        scenario.Kernel.RefreshTrackedGoals(snapshot with
        {
            Goals = snapshot.Goals.Where(goal => goal.Id == source.Id.Value)
                .Select(goal => goal with { Status = GoalStatus.Cancelled }).ToArray()
        });
        Assert.DoesNotContain(scenario.Kernel.Goals, goal => goal.Id == source.Id);
        AssertBaseline(scenario.Kernel, scenario.Current(target), scenario.Agents, scenario.Profiles, scenario.Clock.UtcNow);
    }

    [Fact]
    public void ActiveCrossGoalHold_PreservesBaselineReadiness()
    {
        var (kernel, _, target, agents) = ReadinessTestSeed.Create(crossGoalHold: true);
        AssertBaseline(kernel, target, agents, WorkerProfileCatalog.Default(), ReadinessTestSeed.Now);
    }

    [Fact]
    public void Seed_EmptyIndex_DoesNotLoadGoalsOrEmit()
    {
        var (kernel, _, target, agents) = ReadinessTestSeed.Create();
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var before = repository.LoadGoalsCount;
        var lines = new List<string>();
        ProviderHoldHistorySeed.Apply(kernel, repository, lines.Add);
        Assert.Equal(before, repository.LoadGoalsCount);
        Assert.Empty(lines);
        AssertBaseline(kernel, target, agents, WorkerProfileCatalog.Default(), ReadinessTestSeed.Now);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public void Seed_EmitFailure_ReportsFailureAndLeavesHistoryUnchanged()
    {
        using var scenario = new Scenario();
        var source = scenario.Incident();
        scenario.Complete(source);
        var empty = new AgentOrchestratorKernel(scenario.Clock);
        var lines = new List<string>();
        ProviderHoldHistorySeed.Apply(empty, scenario.Repository, line =>
        {
            if (!line.Contains("seed-failed:", StringComparison.Ordinal)) throw new InvalidOperationException("Fixture emit failure.");
            lines.Add(line);
        });
        Assert.Equal("PROVIDER_HOLD_HISTORY seed-failed: InvalidOperationException: Fixture emit failure.", Assert.Single(lines));
        Assert.Empty(empty.ProviderHoldHistory.Retained);
        Assert.Empty(empty.Goals);
    }

    private static void AssertBaseline(AgentOrchestratorKernel kernel, Goal target,
        IReadOnlyList<AgentDefinition> agents, WorkerProfileCatalog profiles, DateTimeOffset now)
    {
        Assert.Same(kernel.Goals, kernel.ProviderHoldScope);
        Assert.Empty(kernel.ProviderHoldHistory.Retained);
        var baseline = DispatchReadinessAssessment.Evaluate(target, kernel.Goals, agents, profiles, now).Verdict;
        var actual = ConductorDriver.EvaluateConductorReadiness(kernel, target, agents, profiles, now);
        Assert.Equal(baseline.GetType(), actual.GetType());
        Assert.Equal((baseline as DispatchReadinessBlocked)?.ProviderBudgetHold,
            (actual as DispatchReadinessBlocked)?.ProviderBudgetHold);
    }

    // Copied from the CLI readiness fixture, which is compiled in a separate assembly.
    private static class ReadinessTestSeed
    {
        internal static readonly DateTimeOffset Now = new(2026, 9, 6, 20, 0, 0, TimeSpan.Zero);
    
        internal static (AgentOrchestratorKernel Kernel, Goal Source, Goal Target,
            IReadOnlyList<AgentDefinition> Agents) Create(bool crossGoalHold = false, bool selfHold = false)
        {
            var kernel = new AgentOrchestratorKernel(new FakeClock { UtcNow = Now });
            IReadOnlyList<AgentDefinition> agents = [new AgentDefinition(
                new AgentId("xai-developer"), "xAI Developer", AgentRole.Developer,
                new ModelProfile("xAI", "grok-4.6", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
                    SubscriptionMode.ApiKey, "high"),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("grok-cli", "grok-4.6", "high"))];
            var source = kernel.CreateGoal("Source binding observation",
                [new TaskSpec(TaskId.New(), "Inspect src/Source.cs", AgentRole.Developer)]);
            var target = kernel.CreateGoal("Inspect src/Target.cs",
                selfHold
                    ? [new TaskSpec(TaskId.New(), "Inspect first src/Target.cs", AgentRole.Developer),
                       new TaskSpec(TaskId.New(), "Inspect second src/Target.cs", AgentRole.Developer)]
                    : [new TaskSpec(TaskId.New(), "Inspect src/Target.cs", AgentRole.Developer)]);
            kernel.ActivateGoal(source.Id, agents);
            kernel.ActivateGoal(target.Id, agents);
            if (crossGoalHold || selfHold)
            {
                var heldGoal = selfHold ? target : source;
                var task = heldGoal.Tasks[0];
                var started = Now.AddMinutes(-30);
                kernel.RecordTaskDispatch(heldGoal.Id, task.Id, new TaskDispatchRecord(
                    "grok-cli", "grok", "C:\\repo", started,
                    ProviderName: "xAI", WorkerProviderKind: ProviderKind.XaiGrokCli, GoalId: heldGoal.Id));
                kernel.RecordTaskVerification(heldGoal.Id, task.Id, new TaskVerificationRecord(
                    "grok", "C:\\repo", 1, string.Empty, "Provider budget exhausted.", started.AddMinutes(1),
                    ProviderFailureKind: ProviderFailureKind.BudgetExhausted, DispatchStartedAt: started));
            }
            return (kernel, kernel.GetGoal(source.Id), kernel.GetGoal(target.Id), agents);
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-17T11:07:00Z");
    }

    private sealed class Scenario : WorkerDispatchTestSupport, IDisposable
    {
        internal readonly string Root = CreateSeededDispatchRepository();
        internal readonly FakeClock Clock = new();
        internal AgentOrchestratorKernel Kernel;
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
        internal readonly ITransactionalOrchestratorStateRepository Repository;
        private ConductorDriver _driver = null!;

        internal Scenario()
        {
            Kernel = new(Clock);
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Agents = [Primary];
            Repository = InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);
            Rebuild(Kernel);
        }

        internal void Rebuild(AgentOrchestratorKernel kernel)
        {
            Kernel = kernel;
            _driver = new ConductorDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true), getRunningPaidWorkerCount: () => 0,
                createWorkspace: _ => throw new InvalidOperationException("Fixture worktree must already exist."),
                dispatchAndStart: goal =>
                {
                    using var worker = new PreparationWorker();
                    var result = new GoalDispatchOperations().StartSubscriptionReadyTasks(Kernel, Workspace, goal, Agents, Profiles,
                        checkpointBeforeWorkerStart: (kernel, _, _, _) => Repository.SaveAsync(kernel).GetAwaiter().GetResult(),
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
                (kernel, _) => new ProviderHoldAgentSubstitution(kernel, Agents, Preflight), retryOnHeldBinding: true);
        }

        internal ProviderBudgetExhaustionHold Hold
        {
            get
            {
                Assert.True(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(Kernel.ProviderHoldScope, "xAI", null, out var hold));
                return hold;
            }
        }
        internal void At(string text) => Clock.UtcNow = DateTimeOffset.Parse(text);
        internal Goal Current(Goal goal) => Kernel.GetGoal(goal.Id);
        internal ConductorAdvanceResult Tick(Goal goal)
        {
            Starts.Remove(goal.Id);
            var result = _driver.AdvanceOnce(Current(goal), ConductorAutonomyPolicy.Permissive);
            Save();
            return result;
        }
        internal void Save() => Repository.SaveAsync(Kernel).GetAwaiter().GetResult();
        internal Goal Stored(Goal goal) => Repository.LoadGoalsAsync([goal.Id]).GetAwaiter().GetResult().GetGoal(goal.Id);
        internal void AssertBlocked(Goal goal)
        {
            Assert.Empty(Starts[goal.Id].Dispatches);
            Assert.Equal("provider-budget-exhausted", Assert.Single(Starts[goal.Id].BlockedDiagnostics).Reason);
        }
        internal void Complete(Goal goal)
        {
            Kernel.ReplaceGoalWithSnapshot(Kernel.ExportGoalSnapshot(goal.Id) with { Status = GoalStatus.Completed });
            Save();
        }
        internal Goal NewGoal(int count = 1)
        {
            var goal = InfrastructureTestSupport.MarkGoalRefined(Kernel, Kernel.CreateGoal(new GoalId((++_goalSequence).ToString("x8") + new string('0', 24)), "Verify the change.",
                Enumerable.Range(0, count).Select(index => new TaskSpec(new TaskId((_goalSequence * 100 + index).ToString("x8") + new string('0', 24)), $"Verify fixture check {index} without editing repository files.", AgentRole.Tester)).ToArray()));
            Kernel.ActivateGoal(goal.Id, Agents);
            GoalWorktrees.Ensure(Root, goal.Id);
            Save();
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
            if (exit != 0)
                Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Fixture provider failure.");
            Save();
        }
        internal WorkerSubscriptionPreflightResult Preflight(Goal goal, TaskSpec task, IEnumerable<Goal> holds) =>
            WorkerProfileDispatcher.PreflightSubscriptionTask(goal, task, Agents, Profiles, Workspace.ResolveExecutionDirectory(goal.Id), Clock.UtcNow,
                allowGitReference: true, sandboxOptions: _sandbox, commandExists: _ => true, providerHoldScope: holds,
                cascadeTesterCheapFirst: false, claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli);
        internal string BlockedFinding(Goal goal) => Preflight(Current(goal), Current(goal).Tasks.Single(), Kernel.ProviderHoldScope).Findings
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
