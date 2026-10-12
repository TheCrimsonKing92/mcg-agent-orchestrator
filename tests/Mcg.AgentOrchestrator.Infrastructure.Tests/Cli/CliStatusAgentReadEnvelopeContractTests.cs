using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private roots and query repositories; console capture is async-local.
public sealed class CliStatusAgentReadEnvelopeContractTests : CliTaskQueryTestSupport
{
    private static readonly GoalId TargetId = new("abcdef12abcdef12abcdef12abcdef12");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeededCommand_PinsOrderedContractAndHumanValues(bool withLabel)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var objective = "Objective head " + new string('o', 1000) + " objective tail";
            var longDescription = "Description head " + new string('d', 1000) + " description tail";
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(TargetId, objective,
            [
                new TaskSpec(TaskId.New(), longDescription, AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Unassigned verification", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Review output", AgentRole.Reviewer)
            ]);
            if (withLabel)
            {
                var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Friendly status label");
                kernel.SetGoalSourceBacklogItemId(TargetId, item.Id);
            }
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal with
                {
                    Tasks = goal.Tasks.Select((task, index) => index == 1 ? task : task with
                    {
                        AssignedAgentId = index == 0 ? "developer-agent-long-id" : "reviewer-agent-long-id",
                        Status = WorkTaskStatus.Assigned
                    }).ToArray()
                }).ToArray()
            });

            var args = new[] { "status", "abcdef12", "--json" };
            CliCommandHelp.ThrowIfInvalidFlags(args);
            var output = CaptureConsole(() => CliStatusJsonQueryCommand.Execute(args,
                new QueryOnlyStateRepository(kernel), workspace, new FixedTimeProvider()));
            Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            using var json = JsonDocument.Parse(output);
            var payload = AssertEnvelope(json.RootElement);
            Assert.Equal(TargetId.Value, payload.GetProperty("goal").GetString());
            if (withLabel)
                Assert.Equal("Friendly status label", payload.GetProperty("label").GetString());
            else
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("label").ValueKind);

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var human = CaptureConsole(() => CliStatusQueryCommand.Execute(["status", "abcdef12"],
                new QueryOnlyStateRepository(kernel), workspace, new InMemoryModelProviderRegistry([]), null,
                ref agents, ref profiles, ref currentGoal));
            var statusLine = Assert.Single(human.Split(Environment.NewLine),
                line => line.StartsWith("Status: ", StringComparison.Ordinal));
            Assert.Equal(statusLine[8..], payload.GetProperty("status").GetString());
            var objectivePreview = payload.GetProperty("objective").GetString();
            Assert.NotEqual(objective, objectivePreview);
            Assert.Contains($"Objective: {objectivePreview}{Environment.NewLine}", human);
            Assert.Contains(withLabel ? $"Goal {TargetId.Value} (Friendly status label)" :
                $"Goal {TargetId.Value}{Environment.NewLine}", human);

            var tasks = payload.GetProperty("tasks").EnumerateArray().ToArray();
            Assert.Equal(3, tasks.Length);
            string[] statuses = ["Assigned", "Pending", "Assigned"];
            string[] roles = ["Developer", "Tester", "Reviewer"];
            string?[] agentIds = ["develope", null, "reviewer"];
            for (var index = 0; index < tasks.Length; index++)
            {
                var task = tasks[index];
                Assert.Equal(new[] { "number", "status", "role", "description", "agent" },
                    task.EnumerateObject().Select(property => property.Name));
                Assert.Equal(index + 1, task.GetProperty("number").GetInt32());
                Assert.Equal(statuses[index], task.GetProperty("status").GetString());
                Assert.Equal(roles[index], task.GetProperty("role").GetString());
                Assert.Equal(agentIds[index], task.GetProperty("agent").GetString());
                if (index == 1) Assert.Equal(JsonValueKind.Null, task.GetProperty("agent").ValueKind);
                var description = task.GetProperty("description").GetString();
                Assert.Contains($"  {index + 1}. [{statuses[index]}] {roles[index]}: {description} " +
                    $"({agentIds[index] ?? "unassigned"}){Environment.NewLine}", human);
            }
            Assert.NotEqual(longDescription, tasks[0].GetProperty("description").GetString());
            Assert.Equal("Unassigned verification", tasks[1].GetProperty("description").GetString());
            Assert.Equal("Review output", tasks[2].GetProperty("description").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LandedGoal_UsesHumanStatusInsteadOfStoredGoalStatus()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(TargetId, "Landed goal");
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal with { Status = GoalStatus.Completed }).ToArray()
            });
            var goal = kernel.GetGoal(TargetId);
            GoalOperationJournal.Completed(workspace.ExecutionDirectory, goal, "conductor:land", "Landing completed.");
            GoalOperationJournal.Failed(workspace.ExecutionDirectory, goal, "conductor:cleanup", "Cleanup deferred.");
            using var json = JsonDocument.Parse(CaptureConsole(() => CliStatusJsonQueryCommand.Execute(
                ["status", "abcdef12", "--json"], new QueryOnlyStateRepository(kernel), workspace)));
            Assert.Equal("Landed", json.RootElement.GetProperty("payload").GetProperty("status").GetString());
            Assert.Equal(GoalStatus.Completed, goal.Status);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var human = CaptureConsole(() => CliStatusQueryCommand.Execute(["status", "abcdef12"],
                new QueryOnlyStateRepository(kernel), workspace, new InMemoryModelProviderRegistry([]), null,
                ref agents, ref profiles, ref currentGoal));
            Assert.Contains($"Status: Landed{Environment.NewLine}", human);
            var monitoringState = GoalLifecycle.ResolveState(goal,
                GoalMonitoringSubscriptionCommand.ReadLifecycleFacts(workspace, goal));
            Assert.Equal(GoalLifecycleState.Merged, monitoringState);
            Assert.Equal("Landed", CliCommandHandlers.ResolveGoalStatusText(workspace, goal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EmptyGoal_KeepsNullLabelAndEmptyTasks()
    {
        // Active goals require tasks; terminal metadata stubs are the taskless read model.
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot(TargetId.Value, "Empty goal", GoalStatus.Completed, [], [], IsMetadataOnly: true)],
            []));
        var goal = kernel.GetGoal(TargetId);
        Assert.Empty(goal.Tasks);
        using var json = JsonDocument.Parse(StatusAgentReadView.Render(goal, null, "Completed", new FixedTimeProvider()));
        var payload = AssertEnvelope(json.RootElement);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("label").ValueKind);
        Assert.Equal(JsonValueKind.Array, payload.GetProperty("tasks").ValueKind);
        Assert.Empty(payload.GetProperty("tasks").EnumerateArray());
    }

    private static JsonElement AssertEnvelope(JsonElement envelope)
    {
        Assert.Equal(new[] { "schema", "schemaVersion", "generatedAt", "payload" },
            envelope.EnumerateObject().Select(property => property.Name));
        Assert.Equal("mcg.status", envelope.GetProperty("schema").GetString());
        Assert.Equal("mcg.status", AgentReadEnvelope.StatusSchema);
        Assert.Equal(AgentReadEnvelope.StatusSchema, envelope.GetProperty("schema").GetString());
        Assert.Equal(JsonValueKind.Number, envelope.GetProperty("schemaVersion").ValueKind);
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, AgentReadEnvelope.StatusSchemaVersion);
        Assert.Equal(AgentReadEnvelope.StatusSchemaVersion, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-10-12T00:00:00.0000000Z", envelope.GetProperty("generatedAt").GetString());
        var payload = envelope.GetProperty("payload");
        Assert.Equal(new[] { "goal", "label", "objective", "status", "tasks" },
            payload.EnumerateObject().Select(property => property.Name));
        return payload;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    }
}
