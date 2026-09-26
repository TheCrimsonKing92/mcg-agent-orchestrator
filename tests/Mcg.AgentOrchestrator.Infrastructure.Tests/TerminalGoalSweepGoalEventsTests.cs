using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TerminalGoalSweepGoalEventsTests : CliCommandTestBase
{
    [Xunit.Fact]
    public void StartupSweepPersistsTerminalTaskClosureEventsToGoalJsonl()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Stale work", AgentRole.Developer);
        var failedTask = new TaskSpec(TaskId.New(), "Earlier failed work", AgentRole.Tester);
        var goal = kernel.CreateGoal("Superseded stale task", [task, failedTask]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, failedTask.Id, WorkTaskStatus.Failed, "Earlier failure");
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Superseded);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["conduct", "--loop", "--max-iterations", "1"],
            repository, workspace, ref agents, providers, ref profiles, ref currentGoal));
        Xunit.Assert.DoesNotContain("blocked by stale dispatch recovery", output, StringComparison.Ordinal);

        var eventsPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl");
        Xunit.Assert.True(File.Exists(eventsPath));
        var events = File.ReadAllLines(eventsPath).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Xunit.Assert.Contains(events, evt =>
                evt.RootElement.GetProperty("eventType").GetString() == "GoalLifecycleDecision" &&
                evt.RootElement.TryGetProperty("message", out var message) &&
                message.GetString()!.Contains("Superseded", StringComparison.Ordinal));
            Xunit.Assert.Contains(events, evt =>
                evt.RootElement.TryGetProperty("progressKind", out var kind) && kind.GetString() == "TaskCancelled" &&
                evt.RootElement.GetProperty("taskId").GetString() == task.Id.Value);
            Xunit.Assert.Contains(events, evt =>
                evt.RootElement.TryGetProperty("progressKind", out var kind) && kind.GetString() == "TaskCancelled" &&
                evt.RootElement.GetProperty("taskId").GetString() == failedTask.Id.Value);
        }
        finally
        {
            foreach (var evt in events) evt.Dispose();
        }
    }
}
