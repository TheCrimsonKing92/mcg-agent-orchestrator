using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private worktrees/databases, injected clock, and stdin-gated processes.
// Fixture copied from ProviderUsageSelfHealScenarioTests; no provider command is executed.
public sealed class ProviderUsageHoldSelfRetryScenarioTests
{
    private const string Exhausted = "API error (status 402 Payment Required): Grok Build usage balance exhausted";

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void NoAlternate_RetriesHeldBindingWaitsForProbeAndClearsOnSuccess()
    {
        using var scenario = new Scenario();
        var goal = scenario.Incident();
        Assert.Equal(GoalStatus.Failed, goal.Status);
        Assert.Equal(ProviderFailureKind.BudgetExhausted, goal.Tasks.Single().LastVerification!.ProviderFailureKind);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T11:07:00Z"), goal.Tasks.Single().LastDispatch!.DispatchedAt);
        scenario.At("2026-10-17T11:09:00Z");
        Assert.IsNotType<ConductorAdvanceOutcome.Escalated>(scenario.Tick(goal).Outcome);
        goal = scenario.Current(goal);
        var retry = Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRetried));
        Assert.Equal(goal.Tasks.Single().Id, retry.TaskId);
        Assert.StartsWith("Retry on the held binding xai::<provider-default>", retry.Message);
        Assert.Contains("next_probe=2026-10-17T11:38:00.0000000+00:00", retry.Message);
        Assert.Equal([RetryCause.ProviderInterruption], scenario.Causes);
        Assert.Equal(WorkTaskStatus.Assigned, goal.Tasks.Single().Status);
        Assert.Equal(scenario.Primary.Id, goal.Tasks.Single().AssignedAgentId);
        Assert.Empty(scenario.Starts[goal.Id].Dispatches);
        Assert.Equal("provider-budget-exhausted", Assert.Single(scenario.Starts[goal.Id].BlockedDiagnostics).Reason);
        Assert.Empty(scenario.Escalations);
        foreach (var at in new[] { "2026-10-17T11:20:00Z", "2026-10-17T11:37:00Z" })
        {
            scenario.At(at);
            scenario.Tick(goal);
            Assert.Empty(scenario.Starts[goal.Id].Dispatches);
            Assert.Equal(1, scenario.PrimaryDispatches());
        }
        scenario.At("2026-10-17T11:38:00Z");
        scenario.Tick(goal);
        scenario.AssertPrimary(goal, probe: true);
        Assert.Equal(goal.Tasks.Single().Id, Assert.Single(scenario.ProbeNotes()).TaskId);
        scenario.At("2026-10-17T11:40:00Z");
        scenario.Finish(goal, 0, "Success", "");
        Assert.False(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(scenario.Kernel.Goals, "xAI", null, out _));
        Assert.Equal(2, scenario.PrimaryDispatches());
        Assert.DoesNotContain(scenario.Current(goal).Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
        Assert.Empty(scenario.Escalations);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ExhaustedProbe_RetriesAgainAndDispatchesAtTheNextSchedule()
    {
        using var scenario = new Scenario();
        var goal = scenario.Incident();
        var first = ProviderBudgetHoldSchedules.Evaluate(scenario.Kernel.Goals, scenario.Hold, scenario.Clock.UtcNow);
        scenario.At("2026-10-17T11:09:00Z");
        scenario.Tick(goal);
        scenario.Clock.UtcNow = first.NextProbeAt;
        scenario.Tick(goal);
        scenario.AssertPrimary(goal, probe: true);
        scenario.At("2026-10-17T11:39:00Z");
        scenario.Finish(goal, 1, "", Exhausted);
        var second = ProviderBudgetHoldSchedules.Evaluate(scenario.Kernel.Goals, scenario.Hold, scenario.Clock.UtcNow);
        Assert.True(second.NextProbeAt > scenario.Clock.UtcNow);
        Assert.Equal(first.Attempt + 1, second.Attempt);
        scenario.At("2026-10-17T11:40:00Z");
        Assert.IsNotType<ConductorAdvanceOutcome.Escalated>(scenario.Tick(goal).Outcome);
        var retried = scenario.Current(goal);
        Assert.Equal(2, retried.Timeline.Count(evt => evt.Kind == ProgressKind.TaskRetried));
        Assert.Equal([RetryCause.ProviderInterruption, RetryCause.ProviderInterruption], scenario.Causes);
        Assert.Equal(scenario.Primary.Id, retried.Tasks.Single().AssignedAgentId);
        Assert.Equal(WorkTaskStatus.Assigned, retried.Tasks.Single().Status);
        var finding = scenario.BlockedFinding(goal);
        Assert.Contains($"next_probe={ProviderBudgetHoldSchedules.FormatInstant(second.NextProbeAt)}; reset_source={second.ResetSource}; attempt={second.Attempt}", finding);
        Assert.Contains($"next_probe={ProviderBudgetHoldSchedules.FormatInstant(second.NextProbeAt)}", retried.Timeline.Last(evt => evt.Kind == ProgressKind.TaskRetried).Message);
        scenario.Clock.UtcNow = second.NextProbeAt.AddMinutes(-1);
        scenario.Tick(goal);
        Assert.Empty(scenario.Starts[goal.Id].Dispatches);
        Assert.Equal(2, scenario.PrimaryDispatches());
        scenario.Clock.UtcNow = second.NextProbeAt;
        scenario.Tick(goal);
        scenario.AssertPrimary(goal, probe: true);
        Assert.Equal(3, scenario.PrimaryDispatches());
        Assert.Equal(2, scenario.ProbeNotes().Count());
        Assert.Empty(scenario.Escalations);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void OptOut_NoAlternateKeepsExistingEscalation()
    {
        using var scenario = new Scenario(retryOnHeldBinding: false);
        var goal = scenario.Incident();
        scenario.At("2026-10-17T11:09:00Z");
        var outcome = Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Tick(goal).Outcome);
        Assert.Equal("failed-terminal-fallthrough", outcome.Decision!.DiscriminatingEvidence);
        Assert.Equal(outcome.Reason, Assert.Single(scenario.Escalations));
        Assert.Empty(scenario.Causes);
        Assert.DoesNotContain(scenario.Current(goal).Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
        scenario.At("2026-10-17T11:38:00Z");
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Tick(goal).Outcome);
        Assert.Equal(1, scenario.PrimaryDispatches());
        Assert.Empty(scenario.ProbeNotes());
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("alternate")]
    [InlineData("mixed")]
    [InlineData("cleared")]
    [InlineData("already-substituted")]
    public void UnchangedRecoveryCases_MatchOptOutBaseline(string row)
    {
        using var baseline = new Scenario(retryOnHeldBinding: false, alternate: row is "alternate" or "already-substituted");
        using var enabled = new Scenario(retryOnHeldBinding: true, alternate: row is "alternate" or "already-substituted");
        var expected = ExerciseControl(baseline, row);
        var actual = ExerciseControl(enabled, row);
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.Reason, actual.Reason);
        Assert.Equal(expected.Decision?.Action, actual.Decision?.Action);
        Assert.Equal(expected.Decision?.Rung, actual.Decision?.Rung);
        Assert.Equal(expected.Decision?.DiscriminatingEvidence, actual.Decision?.DiscriminatingEvidence);
        Assert.Equal(expected.Causes, actual.Causes);
        Assert.Equal(expected.Agents, actual.Agents);
        Assert.Equal(expected.Substitutions, actual.Substitutions);
        Assert.Equal(expected.Escalations, actual.Escalations);
        if (row == "alternate")
        {
            Assert.NotEqual(nameof(ConductorAdvanceOutcome.Escalated), actual.Outcome);
            Assert.Equal([enabled.Alternate.Id.Value], actual.Agents);
            Assert.Single(actual.Substitutions);
        }
        else Assert.Equal(nameof(ConductorAdvanceOutcome.Escalated), actual.Outcome);
    }

    [Fact]
    public void Construction_DefaultOptsOutAndWorkspaceFactoryOptsIn()
    {
        using var scenario = new Scenario();
        var direct = new ProviderHoldFailedGoalRedispatch(scenario.Kernel,
            (kernel, _) => new ProviderHoldAgentSubstitution(kernel, scenario.Agents, scenario.Preflight));
        Assert.False(direct.RetriesOnHeldBinding);
        AgentCatalogStore.Save(scenario.Workspace.AgentCatalogPath, new AgentCatalog(scenario.Agents));
        var shipped = ProviderHoldFailedGoalRedispatch.ForWorkspace(scenario.Kernel, scenario.Workspace, ConductorAutonomyPolicy.Permissive);
        Assert.NotNull(shipped);
        Assert.True(shipped.RetriesOnHeldBinding);
    }

    private sealed record Control(string Outcome, string? Reason, PolicyDecisionRecord? Decision,
        RetryCause[] Causes, string?[] Agents, string[] Substitutions, string[] Escalations);

    private static Control ExerciseControl(Scenario scenario, string row)
    {
        if (row == "already-substituted")
            scenario.Agents = [scenario.Primary, scenario.Alternate,
                ProviderHoldAgentSubstitutionTests.Scenario.Agent("ollama-tester", "Ollama", "codex-oss-cli", "qwen3-coder")];
        Goal goal;
        if (row == "mixed")
        {
            goal = scenario.NewGoal(2);
            foreach (var task in goal.Tasks)
                scenario.Kernel.RecordTaskDispatch(goal.Id, task.Id, new("grok-cli", "fixture", scenario.Workspace.ResolveExecutionDirectory(goal.Id),
                    scenario.Clock.UtcNow, ProviderName: "xAI", AssignedAgentId: scenario.Primary.Id.Value));
            scenario.At("2026-10-17T11:08:00Z");
            scenario.Finish(goal, 1, "", Exhausted, goal.Tasks[0].Id);
            scenario.Finish(goal, 1, "", "A non-provider fixture failure.", goal.Tasks[1].Id);
            Assert.Equal(ProviderFailureKind.Unknown, scenario.Current(goal).Tasks[1].LastVerification!.ProviderFailureKind);
        }
        else goal = scenario.Incident();
        if (row == "cleared")
        {
            var success = scenario.NewGoal();
            scenario.At("2026-10-17T11:08:10Z");
            scenario.Kernel.RecordTaskDispatch(success.Id, success.Tasks.Single().Id,
                new("grok-cli", "fixture", scenario.Workspace.ResolveExecutionDirectory(success.Id), scenario.Clock.UtcNow, ProviderName: "xAI"));
            scenario.At("2026-10-17T11:08:20Z");
            scenario.Finish(success, 0, "Success", "");
            Assert.False(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(scenario.Kernel.Goals, "xAI", null, out _));
        }
        scenario.At("2026-10-17T11:09:00Z");
        if (row == "already-substituted")
        {
            scenario.Tick(goal);
            scenario.AssertAlternate(goal);
            scenario.At("2026-10-17T11:10:00Z");
            scenario.Finish(goal, 1, "", Exhausted);
            Assert.Equal(GoalStatus.Failed, scenario.Current(goal).Status);
            var other = scenario.NewGoal();
            var eligible = Assert.IsType<ProviderHoldAgentSubstitution.Result.Substitute>(
                new ProviderHoldAgentSubstitution(scenario.Kernel, scenario.Agents, scenario.Preflight).Resolve(other, other.Tasks.Single()));
            Assert.Equal("ollama-tester", eligible.Agent.Id.Value);
            scenario.At("2026-10-17T11:11:00Z");
        }
        var outcome = scenario.Tick(goal).Outcome;
        var decision = outcome switch
        {
            ConductorAdvanceOutcome.Escalated escalated => escalated.Decision,
            ConductorAdvanceOutcome.Executed executed => executed.Decision,
            ConductorAdvanceOutcome.Held held => held.Decision,
            _ => null
        };
        return new(outcome.GetType().Name, (outcome as ConductorAdvanceOutcome.Escalated)?.Reason, decision,
            scenario.Causes.ToArray(), scenario.Kernel.Goals.OrderBy(item => item.Id.Value).SelectMany(item => item.Tasks)
                .Select(task => task.AssignedAgentId?.Value).ToArray(),
            scenario.Kernel.Goals.SelectMany(item => item.Timeline).Where(evt => evt.Kind == ProgressKind.ProviderHoldSubstituted)
                .Select(evt => evt.Message).ToArray(), scenario.Escalations.ToArray());
    }

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

        internal Scenario(bool retryOnHeldBinding = true, bool alternate = false)
        {
            Kernel = new(Clock);
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
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
                Assert.True(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(Kernel.Goals, "xAI", null, out var hold));
                return hold;
            }
        }
        internal void At(string text) => Clock.UtcNow = DateTimeOffset.Parse(text);
        internal Goal Current(Goal goal) => Kernel.GetGoal(goal.Id);
        internal ConductorAdvanceResult Tick(Goal goal) => _driver.AdvanceOnce(Current(goal), ConductorAutonomyPolicy.Permissive);
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
            if (exit == 0) return;
            Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Fixture provider failure.");
            Kernel.EscalateTaskFailure(goal.Id, task.Id, "Fixture provider failure.");
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
