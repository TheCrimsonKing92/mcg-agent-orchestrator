using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class ExecutionTestSupport
{
    public static (AgentOrchestratorKernel Kernel, Goal Goal) SimpleGoal(string objective = "Test goal")
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(objective,
        [
            new TaskSpec(TaskId.New(), "Implement the requested change and record evidence.",
                AgentRole.Developer, "Record manual verification evidence tied to the objective.")
        ]);
        var model = new ModelProfile("OpenAI", "fixture-model",
            ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, "medium");
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(AgentId.New(), "Developer", AgentRole.Developer, model)]);
        return (kernel, goal);
    }

    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-orchestrator-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static SqliteOrchestratorStateRepository CreateMigratedStateRepository(
        string databasePath,
        Action<string>? statementObserver = null,
        SqliteWriteTelemetryOptions? telemetryOptions = null,
        Action? beforeOutboxCommit = null)
    {
        _ = StateDbMigrations.EnsureUpToDate(databasePath);
        return new SqliteOrchestratorStateRepository(
            databasePath,
            statementObserver,
            telemetryOptions,
            beforeOutboxCommit);
    }
}
