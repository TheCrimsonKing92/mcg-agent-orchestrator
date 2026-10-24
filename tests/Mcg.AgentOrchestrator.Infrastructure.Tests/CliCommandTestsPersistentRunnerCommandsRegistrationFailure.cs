using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerCommandsRegistrationFailure : CliCommandTestBase
{
    [Xunit.Theory(DisplayName = "Cli_dispatch_start_registration_failure_commits_before_nonzero_exit")]
    [Xunit.InlineData("start-dispatch", "--confirm-dispatch-start")]
    [Xunit.InlineData("start-dispatches", "--confirm-batch-start")]
    [Xunit.InlineData("advance-subscription", "--confirm-subscription-advance")]
    [Xunit.InlineData("run-goal", "--confirm-batch-start")]
    public async Task CliDispatchStartRegistrationFailureCommitsBeforeNonzeroExit(
        string command,
        string confirmation)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        var previousDisableStart = Environment.GetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable);
        try
        {
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, null);
            var workspace = CreateRefinedWorkspace(root);
            const string profileName = "registration-failure-fixture";
            WorkerProfileStore.Save(
                workspace.WorkerProfilePath,
                WorkerProfileCatalog.Default().Upsert(new WorkerProfile(profileName, "Write-Output ok")));
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(
                "Commit registration failure before CLI exit",
                [new TaskSpec(TaskId.New(), "Inspect harmless fixture", AgentRole.Planner)]);
            MarkGoalRefined(kernel, goal);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskDispatch(
                goal.Id,
                task.Id,
                new TaskDispatchRecord(profileName, "Write-Output ok", root, DateTimeOffset.UtcNow));
            var other = kernel.CreateGoal("Preserve unrelated goal during registration failure",
                [new TaskSpec(TaskId.New(), "Unrelated progress", AgentRole.Developer)]);
            kernel.RequestHumanInput(other.Id, other.Tasks[0].Id, "Keep unrelated request");
            var otherBefore = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(other.Id));
            var humansBefore = JsonSerializer.Serialize(kernel.ExportSnapshot().HumanInputRequests);
            await repository.SaveAsync(kernel);

            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={workspace.SqliteStatePath};Pooling=False"))
            {
                connection.Open();
                using var trigger = connection.CreateCommand();
                trigger.CommandText = """
                    CREATE TRIGGER fail_spawn_registration
                    BEFORE INSERT ON spawn_registry
                    BEGIN
                        SELECT RAISE(ABORT, 'forced registration failure');
                    END
                    """;
                trigger.ExecuteNonQuery();
            }

            string[] arguments = command switch
            {
                "start-dispatch" =>
                [
                    command,
                    "--goal",
                    goal.Id.Value[..8],
                    "1",
                    confirmation,
                    "--confirm-large-paid-subscription-start"
                ],
                "start-dispatches" =>
                [
                    command,
                    "--goal",
                    goal.Id.Value[..8],
                    confirmation,
                    "--confirm-large-paid-subscription-start"
                ],
                "advance-subscription" =>
                [
                    command,
                    goal.Id.Value[..8],
                    confirmation,
                    "--confirm-large-paid-subscription-start"
                ],
                "run-goal" =>
                [
                    command,
                    goal.Id.Value[..8],
                    confirmation,
                    "--confirm-readiness-risk",
                    "--confirm-large-paid-subscription-start"
                ],
                _ => throw new InvalidOperationException($"Unsupported fixture command '{command}'.")
            };
            var result = RunAppCommand(root, arguments);

            Xunit.Assert.Equal(1, result.ExitCode);
            Xunit.Assert.Contains("worker-process-registration-failed", result.Stderr, StringComparison.Ordinal);
            Xunit.Assert.Contains("stage=durable-registry-write", result.Stderr, StringComparison.Ordinal);

            var restored = await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).LoadAsync();
            var restoredGoal = restored.GetGoal(goal.Id);
            var restoredTask = restoredGoal.Tasks.Single(candidate => candidate.Id == task.Id);
            Xunit.Assert.Equal(WorkTaskStatus.Failed, restoredTask.Status);
            Xunit.Assert.Null(restoredTask.LastProcess);
            Xunit.Assert.NotNull(restoredTask.LastDispatch);
            Xunit.Assert.Contains(restoredGoal.Timeline, evt =>
                evt.TaskId == restoredTask.Id &&
                evt.Kind == ProgressKind.TaskFailed &&
                evt.Message.Contains("worker-process-registration-failed", StringComparison.Ordinal));
            Xunit.Assert.Equal(otherBefore, JsonSerializer.Serialize(restored.ExportGoalSnapshot(other.Id)));
            Xunit.Assert.Equal(humansBefore, JsonSerializer.Serialize(restored.ExportSnapshot().HumanInputRequests));
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, previousDisableStart);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
