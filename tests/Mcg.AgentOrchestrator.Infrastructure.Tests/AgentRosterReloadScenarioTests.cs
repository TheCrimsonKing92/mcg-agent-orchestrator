using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Scenario = ProviderHoldAgentSubstitutionTests.Scenario;

// Parallel-safe: fixtures own repositories, databases, catalogs and stdin-gated shell processes.
// Requested provider commands are captured, never executed.
public sealed class AgentRosterReloadScenarioTests
{
    [Xunit.Fact]
    public void BetweenTicks_OfflineEdit_RedelegatesOnlyTheNextGoal()
    {
        using var fixture = new Fixture();
        var first = fixture.Tick(fixture.GoalA);
        Assert.Equal(AgentRosterRefreshKind.Unchanged, first.Refresh.Kind);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(first.Advance.Outcome);
        var dispatchA = fixture.Current(fixture.GoalA).Tasks.Single().LastDispatch;
        Assert.NotNull(dispatchA);
        Assert.Equal("xai-tester", dispatchA.AssignedAgentId);
        Assert.Equal("grok-cli", dispatchA.WorkerName);
        var revision = fixture.Current(fixture.GoalB).Tasks.Single().ConductorRoutingRevision;
        fixture.WriteOffline();

        var second = fixture.Tick(fixture.GoalB);

        Assert.Equal(AgentRosterRefreshKind.Reloaded, second.Refresh.Kind);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(second.Advance.Outcome);
        var line = Assert.Single(fixture.Lines);
        Assert.StartsWith("AGENT_ROSTER_RELOADED", line, StringComparison.Ordinal);
        Assert.Contains("xai-tester:Available->Offline", line, StringComparison.Ordinal);
        var goal = fixture.Current(fixture.GoalB);
        var task = goal.Tasks.Single();
        var redelegated = Assert.Single(goal.Timeline,
            evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRedelegated);
        Assert.Contains("OFFLINE_AGENT_REDELEGATED from=xai-tester from_status=Offline to=openai-tester role=Tester",
            redelegated.Message, StringComparison.Ordinal);
        var dispatched = Assert.Single(goal.Timeline,
            evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskDispatchRecorded);
        Assert.True(Array.IndexOf(goal.Timeline.ToArray(), redelegated) < Array.IndexOf(goal.Timeline.ToArray(), dispatched));
        Assert.Equal("openai-tester", task.LastDispatch!.AssignedAgentId);
        Assert.Equal("codex-cli", task.LastDispatch.WorkerName);
        Assert.Equal(revision + 1, task.ConductorRoutingRevision);
        Assert.Equal(dispatchA, fixture.Current(fixture.GoalA).Tasks.Single().LastDispatch);
        Assert.Equal(2, fixture.RequestedStarts.Count);
        Assert.Empty(fixture.Escalations);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void UnchangedRoster_MatchesFixedListBaseline(bool rewriteIdenticalBytes)
    {
        using var baseline = new Fixture(fixedRoster: true);
        using var actual = new Fixture();
        Assert.IsType<ConductorAdvanceOutcome.Executed>(baseline.Tick(baseline.GoalA).Advance.Outcome);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(actual.Tick(actual.GoalA).Advance.Outcome);
        Assert.Equal(1, actual.Reads);
        var bytes = File.ReadAllBytes(actual.Workspace.AgentCatalogPath);
        if (rewriteIdenticalBytes)
        {
            File.WriteAllBytes(actual.Workspace.AgentCatalogPath, bytes);
            actual.AdvanceWriteTime();
        }

        var expected = baseline.Tick(baseline.GoalB);
        var observed = actual.Tick(actual.GoalB);

        Assert.Equal(AgentRosterRefreshKind.Unchanged, observed.Refresh.Kind);
        Assert.Equal(rewriteIdenticalBytes ? 2 : 1, actual.Reads);
        Assert.Empty(actual.Lines);
        AssertBaseline(baseline, actual, expected.Advance, observed.Advance);
    }

    [Xunit.Theory]
    [Xunit.InlineData("truncated", "LineNumber:")]
    [Xunit.InlineData("empty", "no agents")]
    [Xunit.InlineData("duplicate", "duplicate agent id 'XAI-TESTER'")]
    [Xunit.InlineData("missing", "agents.json is missing")]
    public void InvalidEdit_KeepsFixedListDispatchAndRecovers(string edit, string error)
    {
        using var baseline = new Fixture(fixedRoster: true);
        using var actual = new Fixture();
        Assert.IsType<ConductorAdvanceOutcome.Executed>(baseline.Tick(baseline.GoalA).Advance.Outcome);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(actual.Tick(actual.GoalA).Advance.Outcome);
        string? invalid = edit switch
        {
            "truncated" => "{\"Agents\":[",
            "empty" => "{\"Agents\":[]}",
            "duplicate" => JsonSerializer.Serialize(new AgentCatalog(
                [actual.Initial[0], actual.Initial[0] with { Id = new AgentId("XAI-TESTER") }])),
            _ => null
        };
        if (invalid is null) File.Delete(actual.Workspace.AgentCatalogPath);
        else
        {
            File.WriteAllText(actual.Workspace.AgentCatalogPath, invalid);
            actual.AdvanceWriteTime();
            if (edit == "truncated")
            {
                Assert.False(AgentCatalogStore.TryParseStrict(invalid, out _, out var parseError));
                error = parseError!;
            }
        }

        var expected = baseline.Tick(baseline.GoalB);
        var observed = actual.Tick(actual.GoalB);

        Assert.Equal(AgentRosterRefreshKind.Rejected, observed.Refresh.Kind);
        var line = Assert.Single(actual.Lines);
        Assert.StartsWith("AGENT_ROSTER_RELOAD_REJECTED", line, StringComparison.Ordinal);
        Assert.Contains("kept_agents=2", line, StringComparison.Ordinal);
        Assert.Contains(error.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\''), line, StringComparison.Ordinal);
        AssertBaseline(baseline, actual, expected.Advance, observed.Advance);
        Assert.Equal(actual.Initial.ToArray(), actual.Reloader.View.ToArray());
        Assert.DoesNotContain(actual.Reloader.View, agent => agent.Id.Value == "openai-planner");
        Assert.Equal(AgentRosterRefreshKind.Unchanged, actual.Reloader.Refresh().Kind);
        Assert.Single(actual.Lines);
        actual.WriteOffline();
        Assert.Equal(AgentRosterRefreshKind.Reloaded, actual.Reloader.Refresh().Kind);
        Assert.Equal(2, actual.Lines.Count);
        Assert.Equal(AgentStatus.Offline, actual.Reloader.View[0].Status);
    }

    private static void AssertBaseline(Fixture baseline, Fixture actual,
        ConductorAdvanceResult expectedTick, ConductorAdvanceResult actualTick)
    {
        Assert.IsType<ConductorAdvanceOutcome.Executed>(expectedTick.Outcome);
        Assert.Equal(expectedTick.Outcome.GetType(), actualTick.Outcome.GetType());
        var expected = baseline.Current(baseline.GoalB).Tasks.Single();
        var observed = actual.Current(actual.GoalB).Tasks.Single();
        Assert.NotNull(expected.LastDispatch);
        Assert.NotNull(observed.LastDispatch);
        Assert.Equal(expected.LastDispatch.AssignedAgentId, observed.LastDispatch.AssignedAgentId);
        Assert.Equal(expected.LastDispatch.WorkerName, observed.LastDispatch.WorkerName);
        Assert.Equal(expected.LastDispatch.ModelSelectionReason, observed.LastDispatch.ModelSelectionReason);
        Assert.Equal(expected.ConductorRoutingRevision, observed.ConductorRoutingRevision);
        Assert.Equal(baseline.Current(baseline.GoalB).Timeline.Select(evt => evt.Kind),
            actual.Current(actual.GoalB).Timeline.Select(evt => evt.Kind));
        Assert.Equal(2, baseline.RequestedStarts.Count);
        Assert.Equal(baseline.RequestedStarts.Count, actual.RequestedStarts.Count);
        Assert.Equal("grok-cli", observed.LastDispatch.WorkerName);
        Assert.Equal("xai-tester", observed.LastDispatch.AssignedAgentId);
        Assert.Empty(actual.Escalations);
    }

    private sealed class Fixture : WorkerDispatchTestSupport, IDisposable
    {
        private readonly string _root = CreateSeededDispatchRepository();
        private readonly AgentOrchestratorKernel _kernel = new(new FixedClock());
        private readonly WorkerProfileCatalog _profiles = WorkerProfileCatalog.Default();
        private readonly WorkerSandboxOptions _sandbox = new(true,
            WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
        private readonly ConductorDriver _driver;
        private int _write;
        internal readonly OrchestratorWorkspace Workspace;
        internal readonly IReadOnlyList<AgentDefinition> Initial;
        internal readonly AgentRosterReloader Reloader;
        internal readonly GoalId GoalA;
        internal readonly GoalId GoalB;
        internal readonly List<string> Lines = [];
        internal readonly List<ProcessStartInfo> RequestedStarts = [];
        internal readonly List<string> RequestedCommands = [];
        internal readonly List<string> Escalations = [];
        internal int Reads;

        internal Fixture(bool fixedRoster = false)
        {
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            AgentCatalogStore.Save(Workspace.AgentCatalogPath, new AgentCatalog([
                Scenario.Agent("xai-tester", "xAI", "grok-cli", "grok-4.6"),
                Scenario.Agent("openai-tester", "OpenAI", "codex-cli", "gpt-6.1-sol")]));
            AdvanceWriteTime();
            Initial = AgentCatalogStore.Load(Workspace.AgentCatalogPath).Agents;
            Reloader = new AgentRosterReloader(Workspace.AgentCatalogPath, Initial, Lines.Add, path =>
            {
                Reads++;
                return File.ReadAllBytes(path);
            });
            GoalA = CreateGoal();
            GoalB = CreateGoal();
            var agents = fixedRoster ? Initial : Reloader.View;
            var repository = InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);
            _driver = ConductorDriverTests.MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                writeEscalation: (_, _, reason) => Escalations.Add(reason),
                dispatchAndStart: goal =>
                {
                    using var worker = new PreparationWorker(RequestedStarts, RequestedCommands);
                    var result = new GoalDispatchOperations().StartSubscriptionReadyTasks(_kernel,
                        Workspace, goal, agents, _profiles,
                        checkpointBeforeWorkerStart: (kernel, _, _, _) => repository.SaveAsync(kernel).GetAwaiter().GetResult(),
                        runner: new BackgroundDispatchRunner(disableProcessStart: false, startProcess: worker.Start),
                        sandboxOptions: _sandbox, commandExists: _ => true, cascadeTesterCheapFirst: false,
                        claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli);
                    Assert.Equal(result.Dispatches.Count, result.Processes.Tasks.Count);
                    foreach (var dispatch in result.Dispatches)
                        Assert.NotNull(_kernel.GetTask(goal.Id, dispatch.Task.Id).LastProcess);
                    return result.Dispatches.Count > 0
                        ? DispatchStartOutcome.Started(result.Dispatches.Select(item => item.Task).ToArray())
                        : DispatchStartOutcome.EmptyBatch(string.Join("\n", result.BlockedDiagnostics.Select(item => item.ToLine())));
                });
        }

        private GoalId CreateGoal()
        {
            var goal = InfrastructureTestSupport.MarkGoalRefined(_kernel, _kernel.CreateGoal("Check the change.",
                [new TaskSpec(TaskId.New(), "Verify the change.", AgentRole.Tester)]));
            _kernel.ActivateGoal(goal.Id, Initial);
            Assert.Equal("xai-tester", goal.Tasks.Single().AssignedAgentId!.Value);
            GoalWorktrees.Ensure(_root, goal.Id);
            return goal.Id;
        }

        internal Goal Current(GoalId id) => _kernel.GetGoal(id);
        internal (AgentRosterRefresh Refresh, ConductorAdvanceResult Advance) Tick(GoalId id) =>
            (Reloader.Refresh(), _driver.AdvanceOnce(Current(id), ConductorAutonomyPolicy.Permissive));
        internal void WriteOffline()
        {
            AgentCatalogStore.Save(Workspace.AgentCatalogPath,
                new AgentCatalog([Initial[0] with { Status = AgentStatus.Offline }, Initial[1]]));
            AdvanceWriteTime();
        }
        internal void AdvanceWriteTime() => File.SetLastWriteTimeUtc(Workspace.AgentCatalogPath,
            Scenario.ObservedAt.UtcDateTime.AddMinutes(++_write));
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
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
