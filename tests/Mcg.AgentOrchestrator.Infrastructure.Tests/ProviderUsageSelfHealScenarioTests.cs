using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private worktrees/databases, one injected clock, and stdin-gated fixture processes.
// The launcher never invokes the requested provider command. Wait bounds detect hangs only.
public sealed class ProviderUsageSelfHealScenarioTests
{
    private const string Exhausted = "API error (status 402 Payment Required): Grok Build usage balance exhausted";

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task IncidentProbesOnceAtBackoffThenAtOperatorResetAndRestoresPrimary()
    {
        using var scenario = new Scenario();
        var incident = scenario.Incident();
        Assert.Equal(ProviderFailureKind.BudgetExhausted, incident.Tasks.Single().LastVerification!.ProviderFailureKind);
        scenario.At("2026-10-17T11:37:00Z");
        var early = scenario.NewGoal();
        Assert.Contains("next_probe=2026-10-17T11:38:00.0000000+00:00; reset_source=backoff; attempt=1", scenario.BlockedFinding(early));
        scenario.Tick(early);
        scenario.AssertAlternate(early);

        scenario.At("2026-10-17T11:38:00Z");
        var probe = scenario.NewGoal();
        var sibling = scenario.NewGoal();
        scenario.Tick(probe);
        scenario.AssertPrimary(probe, probe: true);
        scenario.Tick(sibling);
        scenario.AssertAlternate(sibling);
        Assert.Single(scenario.ProbeNotes());

        scenario.At("2026-10-17T11:39:00Z");
        scenario.Finish(probe, 1, "", Exhausted);
        scenario.Tick(probe);
        scenario.AssertAlternate(probe);
        Assert.Equal([RetryCause.ProviderInterruption], scenario.Causes);
        Assert.Null(scenario.Current(probe).Tasks.Single().LatestProviderBudgetRecoveryAt);
        var beforeReset = scenario.NewGoal();
        Assert.Contains("next_probe=2026-10-17T12:39:00.0000000+00:00", scenario.BlockedFinding(beforeReset));
        Assert.Contains("attempt=2", scenario.BlockedFinding(beforeReset));

        // Submission uses the public CLI route; application walks exactly the source goal.
        scenario.At("2026-10-17T11:40:00Z");
        var why = Path.Combine(scenario.Root, "provider-reset-rationale.txt");
        File.WriteAllText(why, "Provider usage page confirms the reset.");
        try
        {
            IReadOnlyList<AgentDefinition> agents = scenario.Agents;
            var profiles = scenario.Profiles;
            Goal? current = scenario.Kernel.GetGoal(scenario.Hold.SourceGoalId);
            InfrastructureTestSupport.CaptureConsole(() => Assert.False(CliCommandDispatcher.ExecuteCommand(
                ["provider-reset", "xAI", "2026-10-20T21:03:00-05:00", "--text-file", why],
                scenario.Kernel, scenario.Workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
            var store = SqliteOperatorIntentStore.ForDirectories(scenario.Workspace.OrchestratorDirectory, scenario.Workspace.LogDirectory);
            var coordinator = new OperatorIntentCoordinator(store, utcNow: () => scenario.Clock.UtcNow);
            var source = scenario.Kernel.GetGoal(scenario.Hold.SourceGoalId);
            Assert.True(coordinator.ExecutePending(scenario.Kernel, source).MutatedGoalState);
            var repository = InfrastructureTestSupport.CreateMigratedStateRepository(scenario.Workspace.SqliteStatePath);
            await repository.SaveAsync(scenario.Kernel, Xunit.TestContext.Current.CancellationToken);
            coordinator.CompletePersisted([source.Id]);
        }
        finally { File.Delete(why); }
        Assert.Contains("next_probe=2026-10-21T02:03:00.0000000+00:00; reset_source=operator", scenario.BlockedFinding(beforeReset));
        scenario.At("2026-10-21T02:02:00Z");
        var almost = scenario.NewGoal();
        scenario.Tick(almost);
        scenario.AssertAlternate(almost);
        scenario.At("2026-10-21T02:03:00Z");
        var restored = scenario.NewGoal();
        scenario.Tick(restored);
        scenario.AssertPrimary(restored, probe: true);
        Assert.Equal(3, scenario.PrimaryDispatches()); // Incident and two probes, through restoration.
        scenario.At("2026-10-21T02:04:00Z");
        scenario.Finish(restored, 0, "Success", "");
        Assert.False(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(scenario.Kernel.Goals, "xAI", null, out _));
        var normal = scenario.NewGoal();
        scenario.Tick(normal);
        scenario.AssertPrimary(normal, probe: false);
        Assert.Equal(4, scenario.PrimaryDispatches());
        Assert.Equal(2, scenario.ProbeNotes().Count());
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ParsedResetSuppressesEarlierBackoffProbes()
    {
        using var scenario = new Scenario();
        scenario.Incident(stderr: Exhausted + "; resets Oct 20, 9:03pm (America/Chicago)");
        foreach (var instant in new[] { "2026-10-17T11:38:00Z", "2026-10-17T12:39:00Z", "2026-10-21T02:02:00Z" })
        {
            scenario.At(instant);
            var goal = scenario.NewGoal();
            Assert.Contains("next_probe=2026-10-21T02:03:00.0000000+00:00; reset_source=parsed", scenario.BlockedFinding(goal));
            scenario.Tick(goal);
            scenario.AssertAlternate(goal);
            Assert.Equal(1, scenario.PrimaryDispatches());
            Assert.Empty(scenario.ProbeNotes());
        }
        scenario.At("2026-10-21T02:03:00Z");
        var probe = scenario.NewGoal();
        scenario.Tick(probe);
        scenario.AssertPrimary(probe, probe: true);
        Assert.Equal(2, scenario.PrimaryDispatches());
        Assert.Single(scenario.ProbeNotes());
    }

    [Theory]
    [InlineData("weekly")]
    [InlineData("signin")]
    [InlineData("early")]
    [InlineData("pinned")]
    public void BeforeDueOrWithoutBudgetHoldMatchesOptOutBaseline(string row)
    {
        using var baseline = new Scenario(admit: false);
        using var enabled = new Scenario(admit: true);
        var expected = Exercise(baseline, row);
        var actual = Exercise(enabled, row);
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.DispatchAgents, actual.DispatchAgents);
        Assert.Equal(expected.Substitutions, actual.Substitutions);
        Assert.Equal(expected.BlockedReasons, actual.BlockedReasons);
        Assert.Equal(expected.RetryAfter, actual.RetryAfter);
        Assert.Equal(expected.DispatchAgents.Length, actual.DispatchAgents.Length);
        Assert.Empty(baseline.ProbeNotes());
        Assert.Empty(enabled.ProbeNotes());
        if (row != "pinned") return;
        enabled.At("2026-10-17T11:38:00Z");
        var pinned = enabled.Kernel.Goals.Last();
        enabled.Tick(pinned);
        enabled.AssertPrimary(pinned, probe: true);
        Assert.Single(enabled.ProbeNotes());
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void OneReservationAcrossBatchAndGoalsAndNoSecondProbeWhileInFlight()
    {
        using var scenario = new Scenario();
        scenario.Incident();
        scenario.At("2026-10-17T11:38:00Z");
        var first = scenario.NewGoal(2);
        var second = scenario.NewGoal();
        var batch = first.Tasks.Select(task => task.Id).ToArray();
        Assert.All(first.Tasks, task => Assert.Equal(scenario.Primary.Id, task.AssignedAgentId));
        // Readiness has a separate one-task/provider quota. Exercise a two-candidate admission batch
        // directly, then let normal conductor dispatch observe the reservation and substituted sibling.
        ProviderBudgetProbeAdmission.Apply(scenario.Kernel, first, batch, scenario.Preflight, scenario.Clock.UtcNow);
        Assert.Single(scenario.ProbeNotes());
        new ProviderHoldAgentSubstitution(scenario.Kernel, scenario.Agents, scenario.Preflight).ApplyBatch(first, batch);
        Assert.Equal(scenario.Primary.Id, first.Tasks[0].AssignedAgentId);
        Assert.Equal(scenario.Alternate.Id, first.Tasks[1].AssignedAgentId);
        scenario.Tick(first);
        Assert.Equal(2, scenario.Starts[first.Id].Dispatches.Count);
        scenario.Tick(second);
        scenario.AssertAlternate(second);
        Assert.Equal(2, scenario.PrimaryDispatches()); // Original incident plus one probe.
        Assert.Equal(2, scenario.Kernel.Goals.SelectMany(goal => goal.Timeline).Count(evt => evt.Kind == ProgressKind.ProviderHoldSubstituted));
        Assert.True(ProviderBudgetHoldSchedules.Evaluate(scenario.Kernel.Goals, scenario.Hold, scenario.Clock.UtcNow).ProbeInFlight);
        var later = scenario.NewGoal();
        scenario.Tick(later);
        scenario.AssertAlternate(later);
        Assert.Equal(2, scenario.PrimaryDispatches());
        Assert.Single(scenario.ProbeNotes());
        Assert.All(new[] { first, second, later }, goal => Assert.NotEmpty(scenario.Starts[goal.Id].Dispatches));
    }

    private sealed record Baseline(string Outcome, string[] DispatchAgents, string[] Substitutions, string[] BlockedReasons, DateTimeOffset?[] RetryAfter);
    private static Baseline Exercise(Scenario scenario, string row)
    {
        var incident = row switch
        {
            "weekly" => scenario.Incident("You've hit your weekly limit · resets Oct 7, 12pm (America/Chicago)", ""),
            "signin" => scenario.Incident("", "ERROR: Your access token could not be refreshed because your refresh token was already used. Please log out and sign in again."),
            _ => scenario.Incident()
        };
        if (row == "weekly")
        {
            var task = incident.Tasks.Single();
            // Bare CLI refusals are recognized by dispatch classification, not the generic provider parser.
            Assert.Equal(ProviderFailureKind.Unknown, task.LastVerification!.ProviderFailureKind);
            Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit,
                DispatchFailureClassifier.Classify(task, task.LastVerification).Kind);
        }
        if (row == "signin") Assert.Equal(DispatchOutcomeKind.ProviderAuthentication,
            DispatchFailureClassifier.Classify(incident.Tasks.Single(), incident.Tasks.Single().LastVerification!).Kind);
        scenario.At("2026-10-17T11:37:00Z");
        var goal = scenario.NewGoal();
        if (row == "pinned") scenario.Pin(goal);
        var outcome = scenario.Tick(goal);
        if (row == "early") scenario.AssertAlternate(goal);
        if (row == "pinned")
        {
            Assert.Empty(scenario.Starts[goal.Id].Dispatches);
            Assert.Equal(scenario.Primary.Id, goal.Tasks.Single().AssignedAgentId);
            Assert.Equal("provider-budget-exhausted", Assert.Single(scenario.Starts[goal.Id].BlockedDiagnostics).Reason);
        }
        return new(outcome,
            scenario.Kernel.Goals.SelectMany(item => item.Tasks).SelectMany(task => task.DispatchHistory).Select(dispatch => dispatch.AssignedAgentId!).ToArray(),
            scenario.Kernel.Goals.SelectMany(item => item.Timeline).Where(evt => evt.Kind == ProgressKind.ProviderHoldSubstituted).Select(evt => evt.Message).ToArray(),
            scenario.Starts.Values.SelectMany(start => start.BlockedDiagnostics).Select(diagnostic => diagnostic.Reason).ToArray(),
            scenario.Kernel.Goals.SelectMany(item => item.Tasks).Select(task => task.SubscriptionRetryAfter).ToArray());
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
        internal readonly IReadOnlyList<AgentDefinition> Agents;
        internal WorkerProfileCatalog Profiles = WorkerProfileCatalog.Default();
        internal readonly Dictionary<GoalId, SubscriptionStartResult> Starts = [];
        internal readonly List<RetryCause> Causes = [];
        private readonly WorkerSandboxOptions _sandbox = new(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
        private readonly ConductorDriver _driver;

        internal Scenario(bool admit = true)
        {
            Kernel = new(Clock);
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Agents = [Primary, Alternate];
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
                        admitProviderBudgetProbe: admit, utcNow: () => Clock.UtcNow);
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
                writeEscalation: (_, _, _) => { }, classifyChangeRisk: _ => null,
                retryTaskWithCause: (goalId, taskId, message, round, cause) =>
                {
                    Causes.Add(cause);
                    return Kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: round);
                }, utcNow: () => Clock.UtcNow);
            _driver.ProviderHoldRedispatch = new ProviderHoldFailedGoalRedispatch(Kernel,
                (kernel, _) => new ProviderHoldAgentSubstitution(kernel, Agents, Preflight));
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
        internal string Tick(Goal goal) => _driver.AdvanceOnce(Current(goal), ConductorAutonomyPolicy.Permissive).Outcome.GetType().Name;
        internal Goal NewGoal(int count = 1)
        {
            var goal = InfrastructureTestSupport.MarkGoalRefined(Kernel, Kernel.CreateGoal("Verify the change.",
                Enumerable.Range(0, count).Select(index => new TaskSpec(TaskId.New(), $"Verify fixture check {index} without editing repository files.", AgentRole.Tester)).ToArray()));
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
        internal void Finish(Goal goal, int exit, string stdout, string stderr)
        {
            goal = Current(goal);
            var task = goal.Tasks.Single();
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
            Assert.Equal(probe ? 1 : 0, goal.Timeline.Count(evt => ProviderBudgetHoldSchedules.TryParseProbe(evt, out _)));
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
