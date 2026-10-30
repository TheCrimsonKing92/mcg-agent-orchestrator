using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each scenario owns its worktrees/database and injected clock.
// The preparation processes only wait for stdin release; no provider CLI executes.
public sealed class ProviderSignInFailoverScenarioTests
{
    internal const string SignInError = "Error: Not signed in. To sign in without a browser, run:\n  grok login --device-code";

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void SignInIncidentDispatchesAlternateWithoutEscalation()
    {
        using var scenario = new Scenario();
        var goal = scenario.Incident();
        var result = scenario.Tick(goal);

        Assert.IsNotType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        goal = scenario.Current(goal);
        var evt = Assert.Single(goal.Timeline, item => item.Kind == ProgressKind.TaskRedelegated);
        Assert.Contains("PROVIDER_SIGNIN_FAILOVER from=xai-tester from_provider=xAI to=openai-tester to_provider=OpenAI role=Tester", evt.Message);
        Assert.Equal(new[] { RetryCause.ProviderInterruption }, scenario.Causes);
        var task = Assert.Single(goal.Tasks);
        Assert.Equal("openai-tester", task.LastDispatch!.AssignedAgentId);
        Assert.Equal("codex-cli", task.LastDispatch.WorkerName);
        Assert.Equal(1, scenario.LastTickDispatchCount);
        Assert.Single(scenario.Starts[goal.Id].Dispatches);
        Assert.DoesNotContain(scenario.Escalations, item => item.GoalId == goal.Id);
        Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.ProviderHoldSubstituted);
        Assert.False(OfflineAssignedAgentRedelegation.IsOperatorPin(goal, task.Id));
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("no-alternate")]
    [InlineData("offline")]
    [InlineData("same-provider")]
    [InlineData("cli-pinned")]
    [InlineData("non-signin")]
    public void IneligibleIncidentMatchesBaselineFieldByField(string row)
    {
        var primary = Scenario.PrimaryAgent();
        var alternate = row switch
        {
            "offline" => Scenario.AlternateAgent() with { Status = AgentStatus.Offline },
            "same-provider" => ProviderHoldAgentSubstitutionTests.Scenario.Agent("xai-tester-2", "xAI", "grok-cli", "grok-4.7"),
            _ => Scenario.AlternateAgent()
        };
        AgentDefinition[] agents = [primary, alternate];
        using var baseline = new Scenario(enabled: false, agents: agents);
        using var enabled = new Scenario(agents: agents, failoverAgents: row == "no-alternate" ? [primary] : null);

        var expected = Exercise(baseline, row);
        var actual = Exercise(enabled, row);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(expected.Result.Outcome);
        Assert.Equal(expected.Result.Outcome.GetType(), actual.Result.Outcome.GetType());
        var expectedOutcome = (ConductorAdvanceOutcome.Escalated)expected.Result.Outcome;
        var actualOutcome = (ConductorAdvanceOutcome.Escalated)actual.Result.Outcome;
        Assert.Equal(expectedOutcome.Reason, actualOutcome.Reason);
        Assert.NotNull(expectedOutcome.Decision);
        Assert.NotNull(actualOutcome.Decision);
        Assert.Equal(expectedOutcome.Decision.Action, actualOutcome.Decision.Action);
        Assert.Equal(expectedOutcome.Decision.Rung, actualOutcome.Decision.Rung);
        Assert.Equal(expectedOutcome.Decision.DiscriminatingEvidence, actualOutcome.Decision.DiscriminatingEvidence);
        if (row != "non-signin")
        {
            Assert.Equal(9, expectedOutcome.Decision.Rung);
            Assert.Equal("failed-terminal-fallthrough", expectedOutcome.Decision.DiscriminatingEvidence);
        }
        Assert.Equal(expected.DispatchCount, actual.DispatchCount);
        Assert.Equal(0, baseline.LastTickDispatchCount);
        Assert.Equal(baseline.LastTickDispatchCount, enabled.LastTickDispatchCount);
        Assert.Equal(baseline.Causes, enabled.Causes);
        Assert.Equal(expected.EventKinds, actual.EventKinds);
        Assert.Empty(baseline.Causes);
        Assert.Equal(Assert.Single(baseline.Escalations).Reason, Assert.Single(enabled.Escalations).Reason);
        Assert.DoesNotContain(baseline.Kernel.Goals.SelectMany(goal => goal.Timeline), IsFailover);
        Assert.DoesNotContain(enabled.Kernel.Goals.SelectMany(goal => goal.Timeline), IsFailover);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("Not signed in", false)]
    [InlineData("Error: Not signed in", true)]
    public void ReplacementSignInFailureEscalatesWithoutReturningToSource(string stderr, bool recoverableAuthentication)
    {
        using var scenario = new Scenario();
        var goal = scenario.Incident();
        Assert.IsNotType<ConductorAdvanceOutcome.Escalated>(scenario.Tick(goal).Outcome);
        Assert.Equal("openai-tester", scenario.Current(goal).Tasks.Single().LastDispatch!.AssignedAgentId);
        scenario.Finish(goal, 1, "", stderr);
        Assert.Equal(recoverableAuthentication,
            ProviderFailureSignals.HasRecoverableProviderAuthenticationFailure(scenario.Current(goal).Tasks.Single()));
        if (recoverableAuthentication) scenario.AssertAuthenticationFailure(goal);

        var outcome = Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Tick(goal).Outcome);

        Assert.Equal(9, outcome.Decision!.Rung);
        Assert.Equal("failed-terminal-fallthrough", outcome.Decision.DiscriminatingEvidence);
        goal = scenario.Current(goal);
        Assert.Equal("openai-tester", goal.Tasks.Single().AssignedAgentId!.Value);
        Assert.Single(goal.Timeline, item => item.Kind == ProgressKind.TaskRedelegated && IsFailover(item));
        Assert.Equal(0, scenario.LastTickDispatchCount);
        Assert.Equal(new[] { RetryCause.ProviderInterruption }, scenario.Causes);
        Assert.Single(scenario.Escalations);
    }

    private static bool IsFailover(ProgressEvent evt) => evt.Message.Contains(ProviderSignInFailover.Marker, StringComparison.Ordinal);

    private sealed record Observation(ConductorAdvanceResult Result, int DispatchCount, ProgressKind[] EventKinds);

    private static Observation Exercise(Scenario scenario, string row)
    {
        var goal = scenario.NewGoal();
        if (row == "cli-pinned")
        {
            scenario.Pin(goal);
            Assert.True(OfflineAssignedAgentRedelegation.IsOperatorPin(scenario.Current(goal), goal.Tasks.Single().Id));
        }
        scenario.Tick(goal);
        Assert.Equal("xai-tester", scenario.Current(goal).Tasks.Single().LastDispatch!.AssignedAgentId);
        scenario.Finish(goal, 1, "", row == "non-signin" ? "Error: unexpected end of stream" : SignInError);
        if (row != "non-signin") scenario.AssertAuthenticationFailure(goal);
        var failureIndex = scenario.Current(goal).Timeline.Count;
        var result = scenario.Tick(goal);
        goal = scenario.Current(goal);
        return new(result, goal.Tasks.Sum(task => task.DispatchHistory.Count), goal.Timeline.Skip(failureIndex).Select(evt => evt.Kind).ToArray());
    }

    internal sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-18T19:14:00Z");
    }

    internal sealed class Scenario : WorkerDispatchTestSupport, IDisposable
    {
        internal readonly string Root = CreateSeededDispatchRepository();
        internal readonly FakeClock Clock = new();
        internal readonly AgentOrchestratorKernel Kernel;
        internal readonly OrchestratorWorkspace Workspace;
        internal readonly IReadOnlyList<AgentDefinition> Agents;
        internal readonly Dictionary<GoalId, SubscriptionStartResult> Starts = [];
        internal readonly List<RetryCause> Causes = [];
        // This callback is the driver's sink for lifecycle GoalEscalated events, outside ProgressKind.
        internal readonly List<(GoalId GoalId, string Reason)> Escalations = [];
        internal int LastTickDispatchCount { get; private set; }
        private readonly WorkerSandboxOptions _sandbox = new(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
        private readonly ConductorDriver _driver;
        private WorkerProfileCatalog _profiles = WorkerProfileCatalog.Default();

        internal static AgentDefinition PrimaryAgent() => ProviderHoldAgentSubstitutionTests.Scenario.Agent("xai-tester", "xAI", "grok-cli", "grok-4.7");
        internal static AgentDefinition AlternateAgent() => ProviderHoldAgentSubstitutionTests.Scenario.Agent("openai-tester", "OpenAI", "codex-cli", "gpt-6.1-sol");

        internal Scenario(bool enabled = true, IReadOnlyList<AgentDefinition>? agents = null, IReadOnlyList<AgentDefinition>? failoverAgents = null)
        {
            Kernel = new(Clock);
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Agents = agents ?? [PrimaryAgent(), AlternateAgent()];
            var repository = InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);
            _driver = new ConductorDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true), getRunningPaidWorkerCount: () => 0,
                createWorkspace: _ => throw new InvalidOperationException("Fixture worktree must already exist."),
                dispatchAndStart: goal =>
                {
                    using var worker = new PreparationWorker();
                    var result = new GoalDispatchOperations().StartSubscriptionReadyTasks(Kernel, Workspace, goal, Agents, _profiles,
                        checkpointBeforeWorkerStart: (kernel, _, _, _) => repository.SaveAsync(kernel).GetAwaiter().GetResult(),
                        runner: new BackgroundDispatchRunner(disableProcessStart: false, startProcess: worker.Start),
                        sandboxOptions: _sandbox, commandExists: _ => true, cascadeTesterCheapFirst: false,
                        utcNow: () => Clock.UtcNow);
                    Starts[goal.Id] = result;
                    LastTickDispatchCount += result.Dispatches.Count;
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
                writeEscalation: (goal, _, reason) => Escalations.Add((goal.Id, reason)), classifyChangeRisk: _ => null,
                retryTaskWithCause: Retry, utcNow: () => Clock.UtcNow);
            _driver.ProviderHoldRedispatch = new ProviderHoldFailedGoalRedispatch(Kernel,
                (kernel, _) => new ProviderHoldAgentSubstitution(kernel, Agents, Preflight));
            if (enabled) _driver.SignInFailover = new ProviderSignInFailover(Kernel, failoverAgents ?? Agents);
        }

        internal TaskSpec Retry(GoalId goalId, TaskId taskId, string message, RetryRoundKind? round, RetryCause cause)
        {
            Causes.Add(cause);
            return Kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: round);
        }

        internal Goal Current(Goal goal) => Kernel.GetGoal(goal.Id);
        internal ConductorAdvanceResult Tick(Goal goal)
        {
            LastTickDispatchCount = 0;
            return _driver.AdvanceOnce(Current(goal), ConductorAutonomyPolicy.Permissive);
        }

        internal Goal NewGoal(int count = 1)
        {
            var goal = InfrastructureTestSupport.MarkGoalRefined(Kernel, Kernel.CreateGoal("Verify the change.",
                Enumerable.Range(0, count).Select(index => new TaskSpec(TaskId.New(), $"Verify fixture check {index} without editing repository files.", AgentRole.Tester)).ToArray()));
            Kernel.ActivateGoal(goal.Id, Agents);
            GoalWorktrees.Ensure(Root, goal.Id);
            return goal;
        }

        internal Goal Incident()
        {
            var goal = NewGoal();
            Tick(goal);
            Assert.Equal("xai-tester", Current(goal).Tasks.Single().LastDispatch!.AssignedAgentId);
            Finish(goal, 1, "", SignInError);
            AssertAuthenticationFailure(goal);
            return Current(goal);
        }

        internal void AssertAuthenticationFailure(Goal goal)
        {
            var task = Current(goal).Tasks.Single();
            Assert.Equal(DispatchOutcomeKind.ProviderAuthentication, DispatchFailureClassifier.Classify(task, task.LastVerification!).Kind);
        }

        internal void Finish(Goal goal, int exit, string stdout, string stderr)
        {
            goal = Current(goal);
            foreach (var task in goal.Tasks)
            {
                var dispatch = task.LastDispatch!;
                var kind = WorkerProviderCatalog.Default().ResolveProfile(dispatch.WorkerName).ParseOutcome(new(exit, stdout, stderr));
                Kernel.RecordTaskVerification(goal.Id, task.Id, new(dispatch.Command, dispatch.WorkingDirectory, exit,
                    stdout, stderr, Clock.UtcNow, ProviderFailureKind: kind, DispatchStartedAt: dispatch.DispatchedAt));
                if (task.LastProcess is { } process)
                    Kernel.RecordTaskProcessRefreshed(goal.Id, task.Id, process with { CompletedAt = Clock.UtcNow, ExitCode = exit }, verification: null);
                if (exit == 0) continue;
                Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Fixture provider failure.");
                Kernel.EscalateTaskFailure(goal.Id, task.Id, "Fixture provider failure.");
            }
        }

        private WorkerSubscriptionPreflightResult Preflight(Goal goal, TaskSpec task, IEnumerable<Goal> holds) =>
            WorkerProfileDispatcher.PreflightSubscriptionTask(goal, task, Agents, _profiles, Workspace.ResolveExecutionDirectory(goal.Id), Clock.UtcNow,
                allowGitReference: true, sandboxOptions: _sandbox, commandExists: _ => true, providerHoldScope: holds,
                cascadeTesterCheapFirst: false, claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli);

        internal void Pin(Goal goal)
        {
            IReadOnlyList<AgentDefinition> agents = Agents;
            Goal? current = goal;
            InfrastructureTestSupport.CaptureConsole(() => Assert.True(CliCommandDispatcher.ExecuteCommand(
                ["reassign-agent", "1", Agents[0].Id.Value], Kernel, Workspace, ref agents, new InMemoryModelProviderRegistry([]), ref _profiles, ref current)));
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
