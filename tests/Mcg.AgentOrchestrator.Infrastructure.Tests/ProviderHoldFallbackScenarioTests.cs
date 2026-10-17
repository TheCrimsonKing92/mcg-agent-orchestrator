using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Scenario = ProviderHoldAgentSubstitutionTests.Scenario;

// Parallel-safe: each test owns its worktrees, databases and injected stdin-gated shell processes.
// The injected launcher never executes the prepared provider command.
public sealed class ProviderHoldFallbackScenarioTests
{
    [Xunit.Fact]
    public async Task IncidentRedispatchesAllThreeGoalsWithoutOperatorIntents()
    {
        using var scenario = new Scenario();
        var second = scenario.NewGoal();
        var third = scenario.NewGoal();
        var starts = new Dictionary<GoalId, SubscriptionStartResult>();
        var causes = new List<RetryCause>();
        var driver = Driver(scenario, starts, causes);

        foreach (var goal in new[] { second, third, scenario.Source })
        {
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            AssertSubstituted(scenario, goal, starts[goal.Id]);
            Assert.NotEqual(GoalStatus.Failed, goal.Status);
        }
        Assert.Equal([RetryCause.ProviderInterruption], causes);
        Assert.Null(scenario.Source.Tasks.Single().LatestProviderBudgetRecoveryAt);
        Assert.Equal("xai::<provider-default>", scenario.Hold.BindingKey);
        var store = SqliteOperatorIntentStore.ForDirectories(scenario.Workspace.OrchestratorDirectory,
            scenario.Workspace.LogDirectory);
        foreach (var goal in new[] { scenario.Source, second, third })
            Assert.Empty(await store.ListForGoalAsync(goal.Id.Value, cancellationToken: Xunit.TestContext.Current.CancellationToken));
    }

    [Xunit.Fact]
    public void NoAlternateKeepsAssignedGoalsBlockedAndFailedGoalEscalated()
    {
        using var scenario = new Scenario();
        scenario.Agents = [scenario.Primary];
        var second = scenario.NewGoal();
        var third = scenario.NewGoal();
        var starts = new Dictionary<GoalId, SubscriptionStartResult>();
        var driver = Driver(scenario, starts, []);
        foreach (var goal in new[] { second, third })
        {
            var expected = scenario.Preflight(goal, goal.Tasks.Single(), scenario.Kernel.Goals).Findings
                .Single(finding => finding.Contains("code=provider-budget-exhausted", StringComparison.Ordinal));
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            AssertBlocked(scenario, goal, starts[goal.Id]);
            Assert.Contains(expected, starts[goal.Id].BlockedDiagnostics.Single().Details!);
        }
        var baseline = ConductorDriverTests.MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true))
            .AdvanceOnce(scenario.Source, ConductorAutonomyPolicy.Permissive);
        var actual = driver.AdvanceOnce(scenario.Source, ConductorAutonomyPolicy.Permissive);
        var expectedEscalation = Assert.IsType<ConductorAdvanceOutcome.Escalated>(baseline.Outcome);
        var actualEscalation = Assert.IsType<ConductorAdvanceOutcome.Escalated>(actual.Outcome);
        Assert.Equal(expectedEscalation.Reason, actualEscalation.Reason);
        Assert.Equal(expectedEscalation.Decision!.Action, actualEscalation.Decision!.Action);
        Assert.Equal(expectedEscalation.Decision.Rung, actualEscalation.Decision.Rung);
        Assert.Equal(expectedEscalation.Decision.DiscriminatingEvidence, actualEscalation.Decision.DiscriminatingEvidence);
        Assert.Equal(GoalStatus.Failed, scenario.Source.Status);
        Assert.DoesNotContain(scenario.Source.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
    }

    [Xunit.Fact]
    public void SkillWriteWithOnlyUntypedAlternateKeepsTheHold()
    {
        using var scenario = new Scenario(skillTarget: true);
        scenario.Agents = [scenario.Primary,
            Scenario.Agent("qwen-tester", "LlamaCpp", WorkerProfile.QwenCodeCliName, "qwen3-coder")];
        var goal = scenario.NewGoal();
        var starts = new Dictionary<GoalId, SubscriptionStartResult>();
        var driver = Driver(scenario, starts, []);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        AssertBlocked(scenario, goal, starts[goal.Id]);
        Assert.Contains(starts[goal.Id].BlockedDiagnostics.Single().Details!, finding =>
            finding.Contains(scenario.Hold.BindingKey, StringComparison.OrdinalIgnoreCase));
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(
            driver.AdvanceOnce(scenario.Source, ConductorAutonomyPolicy.Permissive).Outcome);
    }

    [Xunit.Fact]
    public void LaterSuccessfulBindingDispatchesNewTaskOnThePrimary()
    {
        using var scenario = new Scenario();
        var recoveryGoal = scenario.NewGoal();
        var task = recoveryGoal.Tasks.Single();
        var dispatchAt = Scenario.ObservedAt.AddHours(2);
        scenario.Kernel.RecordTaskDispatch(recoveryGoal.Id, task.Id, new TaskDispatchRecord(
            "grok-cli", "grok", scenario.Workspace.ResolveExecutionDirectory(recoveryGoal.Id), dispatchAt,
            ProviderName: "xAI", WorkerProviderKind: ProviderKind.XaiGrokCli, GoalId: recoveryGoal.Id));
        scenario.Kernel.RecordTaskVerification(recoveryGoal.Id, task.Id, new TaskVerificationRecord(
            "grok", scenario.Root, 0, "Success", string.Empty, dispatchAt.AddMinutes(1), DispatchStartedAt: dispatchAt));
        Assert.False(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(scenario.Kernel.Goals, "xAI", null, out _));
        var goal = scenario.NewGoal();
        var starts = new Dictionary<GoalId, SubscriptionStartResult>();
        Driver(scenario, starts, []).AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Single(starts[goal.Id].Dispatches);
        Assert.Equal(scenario.Primary.Id.Value, goal.Tasks.Single().LastDispatch!.AssignedAgentId);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void CliPinWaitsOnHeldAgentOrDispatchesThePinnedAlternate(bool held)
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        var pinned = held ? scenario.Primary : scenario.Alternate;
        IReadOnlyList<AgentDefinition> agents = scenario.Agents;
        var profiles = scenario.Profiles;
        Goal? currentGoal = goal;
        InfrastructureTestSupport.CaptureConsole(() => Assert.True(CliCommandDispatcher.ExecuteCommand(
            ["reassign-agent", "1", pinned.Id.Value], scenario.Kernel, scenario.Workspace,
            ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal)));
        var starts = new Dictionary<GoalId, SubscriptionStartResult>();
        Driver(scenario, starts, []).AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(pinned.Id, goal.Tasks.Single().AssignedAgentId);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
        if (held)
        {
            AssertBlocked(scenario, goal, starts[goal.Id]);
            scenario.Exhaust(goal, pinned);
            Assert.IsType<ConductorAdvanceOutcome.Escalated>(
                Driver(scenario, starts, []).AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);
            Assert.Equal(pinned.Id, goal.Tasks.Single().AssignedAgentId);
            Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
        }
        else
        {
            Assert.Single(starts[goal.Id].Dispatches);
            Assert.Equal(pinned.Id.Value, goal.Tasks.Single().LastDispatch!.AssignedAgentId);
        }
    }

    [Xunit.Fact]
    public void SecondExhaustionEscalatesDespiteAnEligibleThirdAgent()
    {
        using var scenario = new Scenario();
        var third = Scenario.Agent("ollama-tester", "Ollama", "codex-oss-cli", "qwen3-coder");
        scenario.Agents = [scenario.Primary, scenario.Alternate, third];
        var starts = new Dictionary<GoalId, SubscriptionStartResult>();
        var causes = new List<RetryCause>();
        var driver = Driver(scenario, starts, causes);
        driver.AdvanceOnce(scenario.Source, ConductorAutonomyPolicy.Permissive);
        AssertSubstituted(scenario, scenario.Source, starts[scenario.Source.Id]);
        Assert.Equal([RetryCause.ProviderInterruption], causes);
        Assert.Null(scenario.Source.Tasks.Single().LatestProviderBudgetRecoveryAt);
        var source = scenario.Kernel.GetGoal(scenario.Source.Id);
        scenario.Exhaust(source, scenario.Alternate);
        var exhausted = source.Tasks.Single();
        // Pair the simulated provider verification with its exited process, as real completion does.
        scenario.Kernel.RecordTaskProcessRefreshed(source.Id, exhausted.Id, exhausted.LastProcess! with
        {
            CompletedAt = exhausted.LastVerification!.CompletedAt,
            ExitCode = exhausted.LastVerification.ExitCode
        }, verification: null);
        var another = scenario.NewGoal();
        var eligible = Assert.IsType<ProviderHoldAgentSubstitution.Result.Substitute>(scenario.Resolve(another));
        Assert.Equal(third.Id, eligible.Agent.Id);

        var result = driver.AdvanceOnce(source, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(GoalStatus.Failed, source.Status);
        Assert.Single(source.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
        Assert.Equal([RetryCause.ProviderInterruption], causes);
        Assert.Null(source.Tasks.Single().LatestProviderBudgetRecoveryAt);
        Assert.Equal("xai::<provider-default>", scenario.Hold.BindingKey);
    }

    private static ConductorDriver Driver(Scenario scenario,
        Dictionary<GoalId, SubscriptionStartResult> starts, List<RetryCause> causes)
    {
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(scenario.Workspace.SqliteStatePath);
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal =>
            {
                using var worker = new PreparationWorker();
                var result = new GoalDispatchOperations().StartSubscriptionReadyTasks(scenario.Kernel,
                    scenario.Workspace, goal, scenario.Agents, scenario.Profiles,
                    checkpointBeforeWorkerStart: (kernel, _, _, _) => repository.SaveAsync(kernel).GetAwaiter().GetResult(),
                    runner: new BackgroundDispatchRunner(disableProcessStart: false, startProcess: worker.Start),
                    sandboxOptions: scenario.Sandbox,
                    commandExists: _ => true, cascadeTesterCheapFirst: false);
                starts[goal.Id] = result;
                Assert.Equal(result.Dispatches.Count, result.Processes.Tasks.Count);
                foreach (var dispatch in result.Dispatches)
                {
                    var current = scenario.Kernel.GetTask(goal.Id, dispatch.Task.Id);
                    Assert.NotNull(current.LastProcess);
                    if (current.LatestRetryAt is not null)
                    {
                        var receipt = Assert.Single(current.RetryAdmissionHistory,
                            item => item.LinkedDispatchAt == current.LastDispatch!.DispatchedAt);
                        Assert.NotNull(receipt.WorkerStartedAt);
                        var stored = repository.LoadAsync().GetAwaiter().GetResult().GetTask(goal.Id, current.Id);
                        Assert.Equal(current.LastProcess.ProcessId, stored.LastProcess!.ProcessId);
                    }
                }
                return result.Dispatches.Count > 0 ? DispatchStartOutcome.Started(result.Dispatches.Select(item => item.Task).ToArray())
                    : DispatchStartOutcome.EmptyBatch(string.Join("\n", result.BlockedDiagnostics.Select(item => item.ToLine())));
            },
            retryTaskWithCause: (goalId, taskId, message, round, cause) =>
            {
                causes.Add(cause);
                return scenario.Kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: round);
            });
        driver.ProviderHoldRedispatch = new ProviderHoldFailedGoalRedispatch(scenario.Kernel,
            (kernel, _) => scenario.Resolver(kernel));
        return driver;
    }

    private static void AssertSubstituted(Scenario scenario, Goal goal, SubscriptionStartResult start)
    {
        Assert.Single(start.Dispatches);
        Assert.Single(start.Processes.Tasks);
        // Retry admission hydrates a new authoritative goal instance from SQLite.
        goal = scenario.Kernel.GetGoal(goal.Id);
        var task = goal.Tasks.Single();
        Assert.Equal(scenario.Alternate.Id, task.AssignedAgentId);
        Assert.Equal(scenario.Alternate.Id.Value, task.LastDispatch!.AssignedAgentId);
        Assert.Equal("codex-cli", task.LastDispatch.WorkerName);
        var evt = Assert.Single(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
        Assert.Contains($"held binding={scenario.Hold.BindingKey}", evt.Message, StringComparison.Ordinal);
        Assert.Contains($"substitute={scenario.Alternate.Id.Value}", evt.Message, StringComparison.Ordinal);
        Assert.Contains($"receipt={scenario.Hold.EvidenceReceipt}", evt.Message, StringComparison.Ordinal);
    }

    private static void AssertBlocked(Scenario scenario, Goal goal, SubscriptionStartResult start)
    {
        Assert.Empty(start.Dispatches);
        Assert.Equal("provider-budget-exhausted", Assert.Single(start.BlockedDiagnostics).Reason);
        Assert.Equal(scenario.Primary.Id, goal.Tasks.Single().AssignedAgentId);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
    }

    private sealed class PreparationWorker : IDisposable
    {
        private Process? _process;
        private TestChildProcessCapture? _capture;

        internal Process Start(ProcessStartInfo requested)
        {
            Assert.Null(_process);
            var start = new ProcessStartInfo(WorkerShell.Executable)
            {
                WorkingDirectory = requested.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP" })
                if (Environment.GetEnvironmentVariable(name) is { } value)
                    start.Environment[name] = value;
            foreach (var argument in WorkerShell.BaseArguments())
                start.ArgumentList.Add(argument);
            start.ArgumentList.Add("$null = [Console]::ReadLine(); Write-Output 'fixture-worker-exit'");
            _capture = TestChildProcessCapture.Start(start, start: info => _process = Process.Start(info));
            Assert.NotNull(_process);
            Assert.False(_process.HasExited);
            return _process;
        }

        public void Dispose()
        {
            if (_process is null) return;
            try
            {
                _process.StandardInput.WriteLine("release");
                var exit = _capture!.Complete();
                Assert.Equal(0, exit.ExitCode);
                Assert.Equal("fixture-worker-exit", exit.Stdout.Trim());
                Assert.Equal(string.Empty, exit.Stderr);
            }
            finally
            {
                WorkerProcessJobs.Release(_process.Id);
                _capture?.Dispose();
            }
        }
    }
}
