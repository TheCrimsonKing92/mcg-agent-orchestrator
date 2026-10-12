using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private roots and query repositories; console capture is async-local.
public sealed class CliStatusHumanOutputPinTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeededHumanForm_PinsEveryOutputByte(bool tasksOnly)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var id = new GoalId("abc12345aaaaaaaaaaaaaaaaaaaaaaaa");
            kernel.CreateGoal(id, "Pinned objective",
            [
                new TaskSpec(TaskId.New(), "Unassigned task", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Assigned task", AgentRole.Tester)
            ]);
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal with
                {
                    Tasks = goal.Tasks.Select((task, index) => index == 0 ? task : task with
                    {
                        AssignedAgentId = "tester12-long-agent-id", Status = WorkTaskStatus.Assigned
                    }).ToArray()
                }).ToArray()
            });
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            string[] args = tasksOnly ? ["status", "abc12345", "--tasks-only"] : ["status", "abc12345"];
            var output = CaptureConsole(() => CliStatusQueryCommand.Execute(args, new QueryOnlyStateRepository(kernel),
                OrchestratorWorkspace.ForDirectory(root), new InMemoryModelProviderRegistry([]), null,
                ref agents, ref profiles, ref currentGoal));
            var lines = tasksOnly ? new[]
            {
                "",
                "Goal abc12345aaaaaaaaaaaaaaaaaaaaaaaa",
                "Status: Draft",
                "Tasks:",
                "  1. [Pending] Developer: Unassigned task (unassigned)",
                "  2. [Assigned] Tester: Assigned task (tester12)",
                "",
                ""
            } : new[]
            {
                "",
                "Goal abc12345aaaaaaaaaaaaaaaaaaaaaaaa",
                "Objective: Pinned objective",
                "Status: Draft",
                "Tasks:",
                "  1. [Pending] Developer: Unassigned task (unassigned)",
                "  2. [Assigned] Tester: Assigned task (tester12)",
                "",
                ""
            };
            Assert.Equal(string.Join(Environment.NewLine, lines), output);
            Assert.Equal(id, currentGoal?.Id);
            using var json = JsonDocument.Parse(CaptureConsole(() => CliStatusJsonQueryCommand.Execute(
                ["status", "abc12345", "--json"], new QueryOnlyStateRepository(kernel),
                OrchestratorWorkspace.ForDirectory(root))));
            Assert.Equal("Draft", json.RootElement.GetProperty("payload").GetProperty("status").GetString());
        }
        finally { Directory.Delete(root, true); }
    }
}
