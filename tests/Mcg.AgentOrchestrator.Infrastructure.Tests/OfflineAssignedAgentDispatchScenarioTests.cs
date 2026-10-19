using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Scenario = ProviderHoldAgentSubstitutionTests.Scenario;

// Parallel-safe: each fixture owns its repositories, databases and stdin-gated shell process.
// The launcher records the requested command but never executes a provider command.
public sealed class OfflineAssignedAgentDispatchScenarioTests
{
    [Xunit.Fact]
    public void OfflineTesterIsRedelegatedBeforeDispatchOnTheSameTick()
    {
        using var fixture = new Fixture();
        fixture.Agents = [fixture.Primary with { Status = AgentStatus.Offline }, fixture.Alternate];
        var revision = fixture.Current.Tasks.Single().ConductorRoutingRevision;

        var result = fixture.Tick();

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        var goal = fixture.Current;
        var task = goal.Tasks.Single();
        var redelegated = Assert.Single(goal.Timeline,
            evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRedelegated);
        Assert.Contains("from agent 'xai-tester'", redelegated.Message, StringComparison.Ordinal);
        Assert.Contains("to agent 'openai-tester'", redelegated.Message, StringComparison.Ordinal);
        Assert.Contains("OFFLINE_AGENT_REDELEGATED from=xai-tester from_status=Offline to=openai-tester role=Tester",
            redelegated.Message, StringComparison.Ordinal);
        var dispatched = Assert.Single(goal.Timeline,
            evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskDispatchRecorded);
        Assert.True(Array.IndexOf(goal.Timeline.ToArray(), redelegated) < Array.IndexOf(goal.Timeline.ToArray(), dispatched));
        Assert.Equal(fixture.Alternate.Id, task.AssignedAgentId);
        Assert.Equal(fixture.Alternate.Id.Value, task.LastDispatch!.AssignedAgentId);
        Assert.Equal("codex-cli", task.LastDispatch.WorkerName);
        Assert.Equal(revision + 1, task.ConductorRoutingRevision);
        var requested = Assert.Single(fixture.RequestedStarts);
        Assert.DoesNotContain("grok", string.Join(" ", new[] { requested.FileName, requested.Arguments }
            .Concat(requested.ArgumentList)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grok", Assert.Single(fixture.RequestedCommands), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Escalations);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Message.Contains("operator-intent:", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void MissingTesterSkipsTheFirstOfflineAgentInsteadOfUsingStaleRepair()
    {
        using var fixture = new Fixture();
        fixture.Agents = [Scenario.Agent("offline-tester", "xAI", "grok-cli", "grok-4.6")
            with { Status = AgentStatus.Offline }, fixture.Alternate];

        var result = fixture.Tick();

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        var task = fixture.Current.Tasks.Single();
        var evt = Assert.Single(fixture.Current.Timeline,
            evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRedelegated);
        Assert.Contains("from_status=absent", evt.Message, StringComparison.Ordinal);
        Assert.Contains("to=openai-tester", evt.Message, StringComparison.Ordinal);
        Assert.Equal(fixture.Alternate.Id, task.AssignedAgentId);
        Assert.Equal(fixture.Alternate.Id.Value, task.LastDispatch!.AssignedAgentId);
        Assert.Equal("codex-cli", task.LastDispatch.WorkerName);
        Assert.Single(fixture.RequestedStarts);
        Assert.DoesNotContain(fixture.Current.Timeline, evt => evt.Message.Contains("repaired stale", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void AvailableOrNoAlternateMatchesTheOriginalDispatch(bool offlineWithoutAlternate)
    {
        using var baseline = new Fixture();
        using var actual = new Fixture();
        baseline.Agents = [baseline.Primary];
        actual.Agents = offlineWithoutAlternate
            ? [actual.Primary with { Status = AgentStatus.Offline }]
            : [actual.Primary, actual.Alternate];

        var expectedTick = baseline.Tick();
        var actualTick = actual.Tick();

        Assert.IsType<ConductorAdvanceOutcome.Executed>(expectedTick.Outcome);
        Assert.Equal(expectedTick.Outcome.GetType(), actualTick.Outcome.GetType());
        var expected = baseline.Current.Tasks.Single();
        var observed = actual.Current.Tasks.Single();
        Assert.NotNull(expected.LastDispatch);
        Assert.NotNull(observed.LastDispatch);
        Assert.Equal(expected.LastDispatch.AssignedAgentId, observed.LastDispatch.AssignedAgentId);
        Assert.Equal(expected.LastDispatch.WorkerName, observed.LastDispatch.WorkerName);
        Assert.Equal(expected.LastDispatch.ModelSelectionReason, observed.LastDispatch.ModelSelectionReason);
        Assert.Equal(expected.ConductorRoutingRevision, observed.ConductorRoutingRevision);
        Assert.Single(baseline.RequestedStarts);
        Assert.Equal(baseline.RequestedStarts.Count, actual.RequestedStarts.Count);
        Assert.Equal(baseline.Current.Timeline.Select(evt => evt.Kind), actual.Current.Timeline.Select(evt => evt.Kind));
        Assert.Equal("grok-cli", observed.LastDispatch.WorkerName);
        Assert.Equal("xai-tester", observed.LastDispatch.AssignedAgentId);
        foreach (var fixture in new[] { baseline, actual })
            Assert.DoesNotContain(fixture.Current.Timeline,
                evt => evt.Message.Contains(OfflineAssignedAgentRedelegation.Marker, StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void CliPinToNowOfflineAgentHoldsWithoutReassignmentOrProcessStart()
    {
        using var fixture = new Fixture();
        IReadOnlyList<AgentDefinition> agents = fixture.Agents;
        var profiles = fixture.Profiles;
        Goal? currentGoal = fixture.Current;
        InfrastructureTestSupport.CaptureConsole(() => Assert.True(CliCommandDispatcher.ExecuteCommand(
            ["reassign-agent", "1", "xai-tester"], fixture.Kernel, fixture.Workspace,
            ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal)));
        var task = fixture.Current.Tasks.Single();
        Assert.True(OfflineAssignedAgentRedelegation.IsOperatorPin(fixture.Current, task.Id));
        Assert.Null(task.LastDispatch);
        var revision = task.ConductorRoutingRevision;
        var redelegations = fixture.Current.Timeline.Count(evt => evt.Kind == ProgressKind.TaskRedelegated);
        fixture.Agents = [fixture.Primary with { Status = AgentStatus.Offline }, fixture.Alternate];

        var result = fixture.Tick();

        Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        task = fixture.Current.Tasks.Single();
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(fixture.Primary.Id, task.AssignedAgentId);
        Assert.Equal(revision, task.ConductorRoutingRevision);
        Assert.Null(task.LastDispatch);
        Assert.Empty(fixture.RequestedStarts);
        Assert.Equal(redelegations, fixture.Current.Timeline.Count(evt => evt.Kind == ProgressKind.TaskRedelegated));
        Assert.NotNull(fixture.StartResult);
        Assert.Empty(fixture.StartResult.Dispatches);
        Assert.Empty(fixture.StartResult.Processes.Tasks);
        var diagnostic = Assert.Single(fixture.StartResult.BlockedDiagnostics);
        Assert.Equal("assigned-agent-unavailable-pinned", diagnostic.Reason);
        Assert.Contains("agent=xai-tester status=Offline", diagnostic.Details!);
    }

    private sealed class Fixture : WorkerDispatchTestSupport, IDisposable
    {
        internal readonly string Root = CreateSeededDispatchRepository();
        internal readonly AgentOrchestratorKernel Kernel = new(new FixedClock());
        internal readonly AgentDefinition Primary = Scenario.Agent("xai-tester", "xAI", "grok-cli", "grok-4.6");
        internal readonly AgentDefinition Alternate = Scenario.Agent("openai-tester", "OpenAI", "codex-cli", "gpt-6.1-sol");
        internal readonly WorkerProfileCatalog Profiles = WorkerProfileCatalog.Default();
        internal readonly WorkerSandboxOptions Sandbox = new(true,
            WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
        internal readonly OrchestratorWorkspace Workspace;
        internal readonly GoalId GoalId;
        internal IReadOnlyList<AgentDefinition> Agents;
        internal readonly List<ProcessStartInfo> RequestedStarts = [];
        internal readonly List<string> RequestedCommands = [];
        internal readonly List<string> Escalations = [];
        internal SubscriptionStartResult? StartResult;
        internal Goal Current => Kernel.GetGoal(GoalId);

        internal Fixture()
        {
            Agents = [Primary, Alternate];
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            var goal = InfrastructureTestSupport.MarkGoalRefined(Kernel, Kernel.CreateGoal("Check the change.",
                [new TaskSpec(TaskId.New(), "Verify the change.", AgentRole.Tester)]));
            Kernel.ActivateGoal(goal.Id, Agents);
            GoalId = goal.Id;
            Assert.Equal(Primary.Id, goal.Tasks.Single().AssignedAgentId);
            Assert.Single(goal.Timeline, evt => evt.Kind == ProgressKind.TaskDelegated);
            GoalWorktrees.Ensure(Root, goal.Id);
        }

        internal ConductorAdvanceResult Tick()
        {
            var repository = InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);
            var driver = ConductorDriverTests.MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                // Production writes GoalEscalated through this callback, outside the kernel timeline.
                writeEscalation: (_, _, reason) => Escalations.Add(reason),
                dispatchAndStart: goal =>
                {
                    using var worker = new PreparationWorker(RequestedStarts, RequestedCommands);
                    var result = new GoalDispatchOperations().StartSubscriptionReadyTasks(Kernel,
                        Workspace, goal, Agents, Profiles,
                        checkpointBeforeWorkerStart: (kernel, _, _, _) => repository.SaveAsync(kernel).GetAwaiter().GetResult(),
                        runner: new BackgroundDispatchRunner(disableProcessStart: false, startProcess: worker.Start),
                        sandboxOptions: Sandbox, commandExists: _ => true, cascadeTesterCheapFirst: false,
                        claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli);
                    StartResult = result;
                    Assert.Equal(result.Dispatches.Count, result.Processes.Tasks.Count);
                    foreach (var dispatch in result.Dispatches)
                        Assert.NotNull(Kernel.GetTask(goal.Id, dispatch.Task.Id).LastProcess);
                    return result.Dispatches.Count > 0
                        ? DispatchStartOutcome.Started(result.Dispatches.Select(item => item.Task).ToArray())
                        : DispatchStartOutcome.EmptyBatch(string.Join("\n", result.BlockedDiagnostics.Select(item => item.ToLine())));
                });
            return driver.AdvanceOnce(Current, ConductorAutonomyPolicy.Permissive);
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Scenario.ObservedAt;
    }

    private sealed class PreparationWorker(List<ProcessStartInfo> requestedStarts, List<string> requestedCommands) : IDisposable
    {
        private Process? _process;
        private TestChildProcessCapture? _capture;

        internal Process Start(ProcessStartInfo requested)
        {
            requestedStarts.Add(requested);
            // The requested shell hosts a payload; inspect its provider command as well as its argv.
            requestedCommands.Add(DispatchProcessHost.ReadParameters(requested.ArgumentList.Last()).Command);
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
