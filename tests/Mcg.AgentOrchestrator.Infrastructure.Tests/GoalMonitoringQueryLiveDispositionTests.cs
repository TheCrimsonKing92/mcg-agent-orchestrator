using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the database and all workspace files belong to a unique test root.
public sealed class GoalMonitoringQueryLiveDispositionTests
{
    [Xunit.Fact]
    public async Task BuildBatchAndPrint_LegacyTick_UseLiveDisposition()
    {
        var root = Directory.CreateTempSubdirectory("live-disposition-").FullName;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var agents = AgentCatalog.Default().Agents;
            var goal = kernel.CreateGoal("Live monitoring disposition", [
                new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer)
            ]);
            kernel.ActivateGoal(goal.Id, agents);
            goal = kernel.GetGoal(goal.Id);
            var payload = JsonSerializer.Serialize(new
            {
                OperatorDispositions = new[]
                {
                    new ConductorOperatorDispositionSnapshot(
                        goal.Id.Value,
                        OperatorDispositionState.ProductBug,
                        OperatorDispositionConfidence.High,
                        "stale-run-event-disposition",
                        "stale-run-event-command",
                        new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                        [], [], [])
                }
            });
            await new SqliteRunEventStore(workspace.RunEventStorePath).AppendAsync(new RunEventAppend(
                RunEventTypes.ConductorTick, goal.Id.Value, "conduct", "completed", null, payload));

            var monitor = kernel.BuildMonitor(goal.Id);
            var expected = new GoalOperatorDispositionSurface().Evaluate(
                goal,
                monitor.PendingHumanInputCount,
                kernel.BuildVerificationGate(goal.Id).IsSatisfied,
                workspace.ExecutionDirectory,
                ProcessCommandLines.SnapshotOperation(),
                skipTerminalDispatchEvaluation: true,
                skipInactiveDispatchEvaluation: true);
            var batch = GoalMonitoringQuery.BuildBatch(
                kernel, goal, 0, agents, WorkerProfileCatalog.Default(), workspace);

            Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
            Xunit.Assert.Equal(AgentRole.Developer, Xunit.Assert.Single(goal.Tasks).RequiredRole);
            Xunit.Assert.NotNull(batch.Snapshot.OperatorDisposition);
            Xunit.Assert.Equal(expected.State, batch.Snapshot.OperatorDisposition.State);
            Xunit.Assert.Equal(expected.NextSafeCommand, batch.Snapshot.OperatorDisposition.NextSafeCommand);
            Xunit.Assert.DoesNotContain("stale-run-event-disposition", batch.Snapshot.OperatorDisposition.Reason);

            using var output = new StringWriter();
            GoalMonitoringSubscriptionCommand.PrintBatch(batch, output);
            var dispositionLine = Xunit.Assert.Single(output.ToString().Split('\n'),
                line => line.StartsWith("disposition state=", StringComparison.Ordinal));
            Xunit.Assert.DoesNotContain("stale-run-event-command", dispositionLine);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
