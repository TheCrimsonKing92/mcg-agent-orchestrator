using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsConductLoopExternalTerminalEviction : CliCommandTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Cancelled, false)]
    [Xunit.InlineData(GoalStatus.Cancelled, true)]
    [Xunit.InlineData(GoalStatus.Superseded, false)]
    [Xunit.InlineData(GoalStatus.Failed, false)]
    public async Task ConductLoop_ExternalTerminalStatus_EvictsBeforePrewalk(
        GoalStatus storedStatus,
        bool enqueueIntent)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var completedTask = new TaskSpec(TaskId.New(), "Completed predecessor.", AgentRole.Developer);
            var failedTask = new TaskSpec(TaskId.New(), "Failed successor.", AgentRole.Tester);
            var goal = kernel.CreateGoal("Externally terminalized conductor goal", [completedTask, failedTask]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, completedTask.Id, WorkTaskStatus.Completed, "done");
            kernel.ReportTaskProgress(goal.Id, failedTask.Id, WorkTaskStatus.Failed, "failed");
            var repository = new InMemoryTransactionalStateRepository(kernel);
            var loopKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);
            var staleLoopGoal = loopKernel.GetGoal(goal.Id);
            var terminalKernel = WithGoalStatus(kernel, goal.Id, storedStatus);
            await repository.SaveGoalSnapshotsAsync(terminalKernel.ExportSnapshot().Goals);
            var nonTargetedReload = CliPersistentStateRunner.LoadConductLoopKernel(repository);
            Xunit.Assert.Equal(GoalStatus.Active, staleLoopGoal.Status);
            Xunit.Assert.DoesNotContain(nonTargetedReload.Goals, candidate => candidate.Id == goal.Id);

            var intentStore = SqliteOperatorIntentStore.ForDirectories(
                workspace.OrchestratorDirectory,
                workspace.LogDirectory);
            var intent = new OperatorIntentRecord(
                $"terminal-eviction-{storedStatus}",
                $"terminal-eviction-key-{storedStatus}",
                OperatorIntentVerbs.Progress,
                goal.Id.Value,
                failedTask.Id.Value,
                JsonSerializer.Serialize(
                    new ProgressOperatorIntentPayload(WorkTaskStatus.Running, "must not resurrect"),
                    OperatorIntentJson.Options),
                [],
                "operator",
                "test",
                "test",
                DateTimeOffset.UtcNow);
            if (enqueueIntent)
                await intentStore.EnqueueAsync(intent);

            var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            var context = new CliExecutionContext(
                loopKernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                currentGoal: null,
                reloadKernel: () => CliPersistentStateRunner.LoadConductLoopKernel(repository),
                loadOperatorIntentTargets: ids => repository.LoadGoalsAsync(ids).GetAwaiter().GetResult(),
                persistKernel: _ => { },
                persistGoalKernel: (_, _) => { },
                reloadResolvedParkedHumanWaitKernel: () => new AgentOrchestratorKernel(),
                reloadParkedGoalSafetyNetKernel: () => new AgentOrchestratorKernel(),
                reloadKernelForGoals: trackedIds =>
                    CliPersistentStateRunner.LoadConductLoopKernel(repository, trackedIds))
            {
                EventWriter = eventWriter
            };

            _ = CaptureConsole(() => CliCommandHandlers.Execute(
                ["conduct", "--loop", "--max-iterations", "1"],
                context));

            var eventsPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl");
            var events = File.ReadAllLines(eventsPath)
                .Select(line => JsonDocument.Parse(line))
                .ToArray();
            try
            {
                var escalations = events.Where(document =>
                    document.RootElement.GetProperty("eventType").GetString() == "GoalEscalated").ToArray();
                Xunit.Assert.All(escalations, escalation => Xunit.Assert.Equal(
                    "Goal is in Failed state; operator action required",
                    escalation.RootElement.GetProperty("reason").GetString()));
                Xunit.Assert.Empty(escalations);
                var eviction = Xunit.Assert.Single(events.Where(document =>
                    document.RootElement.GetProperty("eventType").GetString() == "GoalEvictedFromConductor"));
                Xunit.Assert.Equal(storedStatus.ToString(), eviction.RootElement.GetProperty("status").GetString());
                Xunit.Assert.Equal(
                    // Target preparation already hydrates the terminal snapshot before the
                    // ordinary sweep. Eviction therefore uses the scheduled reload path.
                    "scheduled-reload",
                    eviction.RootElement.GetProperty("trigger").GetString());
            }
            finally
            {
                foreach (var document in events)
                    document.Dispose();
            }

            Xunit.Assert.Empty(loopKernel.Goals);
            Xunit.Assert.All(staleLoopGoal.Tasks, task => Xunit.Assert.Null(task.LastDispatch));
            Xunit.Assert.True(loopKernel.TryGetKnownDependencyGoalStatus(goal.Id, out var reconciledStatus));
            Xunit.Assert.Equal(storedStatus.ToString(), reconciledStatus);
            Xunit.Assert.Contains(repository.LoadGoalBatches, batch => batch.Contains(goal.Id.Value));

            if (enqueueIntent)
            {
                var intentOutcome = await intentStore.GetAsync(intent.Id);
                Xunit.Assert.Equal(OperatorIntentStatus.Rejected, intentOutcome!.Status);
                Xunit.Assert.Contains("reasonCode=goal-terminal-evicted", intentOutcome.Outcome, StringComparison.Ordinal);
                Xunit.Assert.Contains($"stored status is {storedStatus}", intentOutcome.Outcome, StringComparison.Ordinal);
            }

            var storedKernel = await repository.LoadAsync();
            var storedGoal = storedKernel.GetGoal(goal.Id);
            var stopPlan = GoalAbandonPlanner.Build(storedKernel, storedGoal, workspace, "operator stop", new());
            Xunit.Assert.Equal(storedStatus, stopPlan.GoalStatus);
            Xunit.Assert.Contains(stopPlan.Steps, step =>
                step.Kind == GoalAbandonStepKind.GoalStatus &&
                step.Detail == $"Goal is already {storedStatus}.");

            IReadOnlyList<AgentDefinition> readerAgents = AgentCatalog.Default().Agents;
            var readerProfiles = WorkerProfileCatalog.Default();
            Goal? readerCurrentGoal = null;
            var goalsOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["goals"],
                repository,
                workspace,
                ref readerAgents,
                new InMemoryModelProviderRegistry([]),
                ref readerProfiles,
                ref readerCurrentGoal));
            var statusOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["status", goal.Id.Value[..8]],
                repository,
                workspace,
                ref readerAgents,
                new InMemoryModelProviderRegistry([]),
                ref readerProfiles,
                ref readerCurrentGoal));
            Xunit.Assert.Contains($"{goal.Id.Value[..8]} {storedStatus}", goalsOutput, StringComparison.Ordinal);
            Xunit.Assert.Contains(storedStatus.ToString(), statusOutput, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

}
