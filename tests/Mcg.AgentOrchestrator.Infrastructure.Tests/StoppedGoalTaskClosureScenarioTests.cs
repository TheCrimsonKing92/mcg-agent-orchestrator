using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Each fixture owns its store; this collection protects command console and environment state.
[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class StoppedGoalTaskClosureScenarioTests : CliCommandTestBase
{
    [Fact]
    public async Task ConductLoop_AfterAbandonCancelAndSupersede_FindsNoOpenWork()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var aAssigned = new TaskSpec(TaskId.New(), "Assigned abandon work", AgentRole.Developer);
            var aWaiting = new TaskSpec(TaskId.New(), "Await abandon input", AgentRole.Reviewer);
            var exited = new TaskSpec(TaskId.New(), "Exited dispatch", AgentRole.Tester);
            var a = kernel.CreateGoal("Abandon open work", [aAssigned, aWaiting, exited]);
            var cAssigned = new TaskSpec(TaskId.New(), "Assigned cancel work", AgentRole.Developer);
            var cWaiting = new TaskSpec(TaskId.New(), "Await cancel input", AgentRole.Reviewer);
            var c = kernel.CreateGoal("Cancel open work", [cAssigned, cWaiting]);
            var sAssigned = new TaskSpec(TaskId.New(), "Assigned supersede work", AgentRole.Developer);
            var sWaiting = new TaskSpec(TaskId.New(), "Await supersede input", AgentRole.Reviewer);
            var s = kernel.CreateGoal("Supersede open work", [sAssigned, sWaiting]);
            var lAssigned = new TaskSpec(TaskId.New(), "Assigned live-goal work", AgentRole.Developer);
            var live = new TaskSpec(TaskId.New(), "Live dispatch", AgentRole.Tester);
            var l = kernel.CreateGoal("Abandon with live work", [lAssigned, live]);
            var goals = new[] { a, c, s, l };
            foreach (var goal in goals)
                kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var requests = new[]
            {
                kernel.RequestHumanInput(a.Id, aWaiting.Id, "Continue abandon work?", HumanWaitKind.SpecClarification),
                kernel.RequestHumanInput(c.Id, cWaiting.Id, "Continue cancel work?", HumanWaitKind.SpecClarification),
                kernel.RequestHumanInput(s.Id, sWaiting.Id, "Continue supersede work?", HumanWaitKind.SpecClarification)
            };
            RecordRunningProcess(kernel, a, exited, root, processId: 999999);
            RecordRunningProcess(kernel, l, live, root, Environment.ProcessId);
            // RequestHumanInput holds the task and goal; pin the specified Active goal fixtures.
            foreach (var goal in goals)
                kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id) with { Status = GoalStatus.Active });
            await repository.SaveAsync(kernel);

            foreach (var goal in goals)
            {
                var saved = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
                Assert.Equal(GoalStatus.Active, saved.Status);
                Assert.Equal(WorkTaskStatus.Assigned, saved.Tasks[0].Status);
            }
            foreach (var goal in new[] { a, c, s })
            {
                var saved = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
                Assert.Equal(WorkTaskStatus.WaitingForHuman, saved.Tasks[1].Status);
            }
            var pending = await repository.ListOpenHumanInputRequestsAsync();
            Assert.Equal(requests.Select(request => request.Id.Value).OrderBy(id => id).ToArray(),
                pending.Select(request => request.Id).OrderBy(id => id).ToArray());
            var savedA = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(a.Id));
            Assert.Equal(999999, savedA.Tasks.Single(task => task.Id == exited.Id.Value).LastProcess!.ProcessId);
            var savedL = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(l.Id));
            var savedLive = savedL.Tasks.Single(task => task.Id == live.Id.Value);
            var liveStatus = savedLive.Status;
            Assert.Equal(WorkTaskStatus.Running, liveStatus);
            Assert.Equal(Environment.ProcessId, savedLive.LastProcess!.ProcessId);

            ExecuteCommand(["abandon-goal", a.Id.Value[..8], "Stop abandon work.", "--confirm-goal-abandon"], repository, workspace);
            ExecuteCommand(["cancel-goal", c.Id.Value[..8], "Stop cancel work.", "--confirm-goal-stop"], repository, workspace);
            ExecuteCommand(["supersede-goal", s.Id.Value[..8], "Stop supersede work.", "--confirm-goal-stop"], repository, workspace);
            // This route leaves the tracked process running; no stop alias or process signal is used.
            ExecuteCommand(["abandon-goal", l.Id.Value[..8], "Leave live work running.", "--confirm-goal-abandon"], repository, workspace);

            foreach (var goal in new[] { a, c, s })
            {
                var stopped = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
                Assert.Equal(goal.Id == s.Id ? GoalStatus.Superseded : GoalStatus.Cancelled, stopped.Status);
                Assert.All(stopped.Tasks, task => Assert.Equal(WorkTaskStatus.Cancelled, task.Status));
            }
            var stoppedL = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(l.Id));
            Assert.Equal(GoalStatus.Cancelled, stoppedL.Status);
            Assert.Equal(WorkTaskStatus.Cancelled, stoppedL.Tasks.Single(task => task.Id == lAssigned.Id.Value).Status);
            Assert.Equal(liveStatus, stoppedL.Tasks.Single(task => task.Id == live.Id.Value).Status);
            var openRequests = await repository.ListOpenHumanInputRequestsAsync();
            Assert.DoesNotContain(openRequests, request => new[] { a.Id.Value, c.Id.Value, s.Id.Value }.Contains(request.GoalId));
            Assert.Equal(new[] { l.Id }, await repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());

            var output = ExecuteCommand(["conduct", "--loop", "--max-iterations", "1"], repository, workspace);

            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(lines.Where(line => line.StartsWith(StartupSweepStepsLine.Prefix, StringComparison.Ordinal)));
            foreach (var goal in goals)
            {
                Assert.DoesNotContain(lines, line => line.StartsWith($"SWEEP_REPAIR goal={goal.Id.Value[..8]} ", StringComparison.Ordinal));
                Assert.Equal(goal.Id == l.Id ? 1L : 0L, OpenWorkFlag(workspace.SqliteStatePath, goal.Id));
            }
            var afterPassL = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(l.Id));
            Assert.Equal(liveStatus, afterPassL.Tasks.Single(task => task.Id == live.Id.Value).Status);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static string ExecuteCommand(string[] parts, ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        return CaptureConsole(() => CaptureConsoleError(() => CliPersistentStateRunner.ExecuteCommand(
            parts, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
    }

    private static long OpenWorkFlag(string path, GoalId goalId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT terminal_open_work FROM goal_open_work WHERE goal_id = $goal";
        command.Parameters.AddWithValue("$goal", goalId.Value);
        return Assert.IsType<long>(command.ExecuteScalar());
    }
}
