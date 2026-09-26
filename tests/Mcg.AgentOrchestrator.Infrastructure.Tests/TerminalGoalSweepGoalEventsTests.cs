using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TerminalGoalSweepGoalEventsTests : CliCommandTestBase
{
    [Xunit.Fact]
    public void FailedEventAppendWarnsWithoutUndoingTerminalTaskClosure()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.GoalLifecycleEventsDirectory)!);
        File.WriteAllText(workspace.GoalLifecycleEventsDirectory, "blocks directory creation");
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Stale work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Superseded stale work", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Superseded);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["conduct", "--loop", "--max-iterations", "0"],
            repository, workspace, ref agents, providers, ref profiles, ref currentGoal));
        var persisted = repository.LoadGoalAsync(goal.Id).GetAwaiter().GetResult()!;

        Xunit.Assert.Equal(GoalStatus.Superseded, persisted.Status);
        Xunit.Assert.Equal(WorkTaskStatus.Cancelled, persisted.Tasks.Single().Status);
        Xunit.Assert.Contains("SWEEP_WARNING kind=goal-events-append-failed", output, StringComparison.Ordinal);
        Xunit.Assert.Contains($"goal={goal.Id.Value[..8]}", output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void StartupSweepPersistsFailedGoalReopenDecisionToGoalJsonl()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Stale assigned work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Failed stale task", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Failed);
        CommitGoalWork(root, goal.Id, "src/failed-goal-work.txt", "unmerged goal work");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["conduct", goal.Id.Value[..8], "--loop", "--max-iterations", "1"],
            repository, workspace, ref agents, providers, ref profiles, ref currentGoal));

        var persisted = repository.LoadAsync().GetAwaiter().GetResult().GetGoal(goal.Id);
        Xunit.Assert.Equal(GoalStatus.Active, persisted.Status);
        var eventsPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl");
        Xunit.Assert.True(File.Exists(eventsPath));
        var events = File.ReadAllLines(eventsPath).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Xunit.Assert.Contains(events, evt =>
                evt.RootElement.GetProperty("eventType").GetString() == "GoalLifecycleDecision" &&
                evt.RootElement.TryGetProperty("message", out var message) &&
                message.GetString()!.Contains("reopened terminal goal", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var evt in events) evt.Dispose();
        }
    }

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
