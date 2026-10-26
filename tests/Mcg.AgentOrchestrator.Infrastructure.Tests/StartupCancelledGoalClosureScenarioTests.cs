using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Each fixture owns its store; this collection protects command console and environment state.
[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class StartupCancelledGoalClosureScenarioTests : CliCommandTestBase
{
    [Fact]
    public async Task ConductLoop_TwoStartups_ClosesRetiredCancelledGoalTasksOnceAndKeepsLiveProcessGoal()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var assigned = new TaskSpec(TaskId.New(), "Assigned stale work", AgentRole.Developer);
            var failed = new TaskSpec(TaskId.New(), "Failed stale work", AgentRole.Tester);
            var waiting = new TaskSpec(TaskId.New(), "Await operator", AgentRole.Reviewer);
            var stale = kernel.CreateGoal("Retired cancelled stale work", [assigned, failed, waiting]);
            var liveAssigned = new TaskSpec(TaskId.New(), "Assigned live-goal work", AgentRole.Developer);
            var running = new TaskSpec(TaskId.New(), "Live dispatch", AgentRole.Tester);
            var live = kernel.CreateGoal("Retired cancelled live work", [liveAssigned, running]);
            kernel.ActivateGoal(stale.Id, AgentCatalog.Default().Agents);
            kernel.ActivateGoal(live.Id, AgentCatalog.Default().Agents);
            var request = kernel.RequestHumanInput(stale.Id, waiting.Id, "Continue?", HumanWaitKind.SpecClarification);
            RecordRunningProcess(kernel, live, running, root, Environment.ProcessId);
            kernel.ReportTaskProgress(stale.Id, failed.Id, WorkTaskStatus.Failed, "Earlier failure");
            kernel = WithGoalStatus(WithGoalStatus(kernel, stale.Id, GoalStatus.Cancelled), live.Id, GoalStatus.Cancelled);
            foreach (var id in new[] { stale.Id, live.Id })
            {
                GoalOperationJournal.RecordTerminalDisposition(workspace.ExecutionDirectory, kernel.GetGoal(id),
                    new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "Abandoned by operator"));
            }
            await repository.SaveAsync(kernel);

            var savedStale = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(stale.Id));
            Assert.Equal(new[] { WorkTaskStatus.Assigned, WorkTaskStatus.Failed, WorkTaskStatus.WaitingForHuman },
                savedStale.Tasks.Select(task => task.Status).ToArray());
            Assert.False(kernel.HumanInputRequests.Single(item => item.Id == request.Id).IsCompleted);
            var savedLive = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(live.Id));
            var liveStatuses = savedLive.Tasks.Select(task => (task.Id, task.Status)).ToArray();
            Assert.Equal(new[] { WorkTaskStatus.Assigned, WorkTaskStatus.Running }, liveStatuses.Select(task => task.Status).ToArray());
            Assert.Equal(Environment.ProcessId, savedLive.Tasks.Single(task => task.Id == running.Id.Value).LastProcess!.ProcessId);
            var initialCandidates = await repository.ListTerminalGoalIdsWithNonTerminalTasksAsync();
            Assert.Contains(stale.Id, initialCandidates);
            Assert.Contains(live.Id, initialCandidates);
            var stalePrefix = stale.Id.Value[..8];
            var livePrefix = live.Id.Value[..8];

            var firstOutput = ExecutePass(repository, workspace);

            var closed = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(stale.Id));
            Assert.Equal(GoalStatus.Cancelled, closed.Status);
            Assert.Equal(savedStale.Tasks.Select(task => task.Id).ToArray(), closed.Tasks.Select(task => task.Id).ToArray());
            Assert.All(closed.Tasks, task => Assert.Equal(WorkTaskStatus.Cancelled, task.Status));
            Assert.Single(Lines(firstOutput).Where(line => line.StartsWith(
                $"SWEEP_REPAIR goal={stalePrefix} kind=terminal-task-desync", StringComparison.Ordinal)));
            var firstLive = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(live.Id));
            Assert.Equal(GoalStatus.Cancelled, firstLive.Status);
            Assert.Equal(liveStatuses, firstLive.Tasks.Select(task => (task.Id, task.Status)).ToArray());
            AssertNoRepair(firstOutput, livePrefix);
            var candidates = await repository.ListTerminalGoalIdsWithNonTerminalTasksAsync();
            Assert.DoesNotContain(stale.Id, candidates);
            Assert.Contains(live.Id, candidates);
            Assert.Equal(0L, OpenWorkFlag(workspace.SqliteStatePath, stale.Id));
            Assert.Equal(1L, OpenWorkFlag(workspace.SqliteStatePath, live.Id));
            var firstSteps = Assert.Single(Lines(firstOutput).Where(line => line.StartsWith(StartupSweepStepsLine.Prefix, StringComparison.Ordinal)));
            var firstSlowest = SlowestPrefixes(firstSteps);
            Assert.Contains(stalePrefix, firstSlowest);
            Assert.Contains(livePrefix, firstSlowest);

            var secondOutput = ExecutePass(repository, workspace);

            var secondLive = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(live.Id));
            Assert.Equal(GoalStatus.Cancelled, secondLive.Status);
            Assert.Equal(liveStatuses, secondLive.Tasks.Select(task => (task.Id, task.Status)).ToArray());
            AssertNoRepair(secondOutput, livePrefix);
            AssertNoRepair(secondOutput, stalePrefix);
            var secondSteps = Assert.Single(Lines(secondOutput).Where(line => line.StartsWith(StartupSweepStepsLine.Prefix, StringComparison.Ordinal)));
            var secondSlowest = SlowestPrefixes(secondSteps);
            Assert.Contains(livePrefix, secondSlowest);
            Assert.DoesNotContain(stalePrefix, secondSlowest);
            Assert.True(int.Parse(Field(secondSteps, "terminal_goals_swept"), CultureInfo.InvariantCulture) <
                        int.Parse(Field(firstSteps, "terminal_goals_swept"), CultureInfo.InvariantCulture),
                "The second startup must sweep fewer terminal goals after stale tasks are persisted closed.");
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static string ExecutePass(ITransactionalOrchestratorStateRepository repository, OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        return CaptureConsole(() => CaptureConsoleError(() => CliPersistentStateRunner.ExecuteCommand(
            ["conduct", "--loop", "--max-iterations", "1"], repository, workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
    }

    private static string[] Lines(string output) => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static void AssertNoRepair(string output, string prefix) => Assert.DoesNotContain(Lines(output),
        line => line.StartsWith($"SWEEP_REPAIR goal={prefix} ", StringComparison.Ordinal));

    private static string Field(string line, string name) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Single(field => field.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];

    private static string[] SlowestPrefixes(string line) => Field(line, "slowest").Split(',')
        .Select(item => item.Split(':')[0]).ToArray();

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
