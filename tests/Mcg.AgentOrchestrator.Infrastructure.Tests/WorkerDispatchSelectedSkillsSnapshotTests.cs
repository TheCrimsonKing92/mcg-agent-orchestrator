using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: isolated context roots and per-test kernels.
public sealed class WorkerDispatchSelectedSkillsSnapshotTests
{
    [Fact]
    public void DispatchSelectionSurvivesContextRemovalAndOldSnapshotLoadsWithoutSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "skill-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement bounded round skill evidence.", AgentRole.Developer);
            var goal = kernel.CreateGoal("Preserve dispatch skill evidence", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var expected = WorkerContextArtifacts.SelectSkillRequirements(goal, task, root).Select(s => s.Name).ToArray();
            WorkerProfileDispatcher.PrepareTask(kernel, goal, task, new WorkerProfile("subscription", "worker --cd {workingDirectory}"),
                Path.Combine(root, "prompts"), root, DateTimeOffset.UtcNow, providerName: "Anthropic", modelName: "fixture");
            Assert.NotEmpty(expected);
            Assert.Equal(expected, task.LastDispatch!.SelectedSkills);
            var context = Path.Combine(root, ".orchestrator-context");
            Assert.NotEmpty(Directory.GetFiles(context, "selected-skills.md", SearchOption.AllDirectories));
            Directory.Delete(context, recursive: true);
            var json = JsonSerializer.Serialize(kernel.ExportSnapshot());
            var restored = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(json)!);
            Assert.Equal(expected, restored.GetTask(goal.Id, task.Id).LastDispatch!.SelectedSkills);
            Assert.Equal(expected, Assert.Single(restored.GetTask(goal.Id, task.Id).DispatchHistory).SelectedSkills);

            var old = JsonNode.Parse(json)!;
            RemoveSelection(old);
            Assert.DoesNotContain("SelectedSkills", old.ToJsonString());
            var legacy = AgentOrchestratorKernel.FromSnapshot(old.Deserialize<OrchestratorSnapshot>()!);
            Assert.Empty(legacy.GetTask(goal.Id, task.Id).LastDispatch!.SelectedSkills ?? []);
            Assert.Empty(Assert.Single(legacy.GetTask(goal.Id, task.Id).DispatchHistory).SelectedSkills ?? []);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void RemoveSelection(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("SelectedSkills");
            foreach (var child in obj.Select(p => p.Value).OfType<JsonNode>()) RemoveSelection(child);
        }
        else if (node is JsonArray array)
            foreach (var child in array.OfType<JsonNode>()) RemoveSelection(child);
    }
}
