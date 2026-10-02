using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its context directory and uses no external worker.
public sealed class DeveloperCriteriaSelfCheckFileContextTests : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void FileGuidanceIsCompleteRegisteredAndDeliveredToItsRole(AgentRole role)
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Maintain the label.", role);
        var goal = kernel.CreateGoal("Maintain the label.", [task]);
        var directory = new WorkerArtifactWriter().Write(goal, task, root);
        var guidance = File.ReadAllText(Path.Combine(directory, "criteria-self-check.md"));
        var api = kernel.BuildTaskBrief(goal.Id, task.Id).Content;
        foreach (var line in guidance.Split(Environment.NewLine))
            Xunit.Assert.Contains(line, api, StringComparison.Ordinal);
        if (role == AgentRole.Developer)
        {
            foreach (var clause in new[] { "specific named outcome", "not a weaker property", "pre-change code",
                "strengthen", "assigned_scope_complete: false", "criteria_self_check:", "one-line JSON", "<=200", "<3500" })
                Xunit.Assert.Contains(clause, guidance, StringComparison.Ordinal);
        }
        else
        {
            Xunit.Assert.Contains("test exists in the candidate", guidance, StringComparison.Ordinal);
            Xunit.Assert.Contains("assertion checks the named outcome", guidance, StringComparison.Ordinal);
            Xunit.Assert.Contains("finding against that criterion", guidance, StringComparison.Ordinal);
        }

        Xunit.Assert.Contains("criteria-self-check.md", File.ReadAllText(Path.Combine(directory, "manifest.md")), StringComparison.Ordinal);
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "artifact-registry.json")));
        var entry = Xunit.Assert.Single(registry.RootElement.GetProperty("artifacts").EnumerateArray(),
            item => item.GetProperty("path").GetString() == "criteria-self-check.md");
        Xunit.Assert.True(entry.GetProperty("exists").GetBoolean());
        Xunit.Assert.True(entry.GetProperty("hashVerified").GetBoolean());
        Xunit.Assert.Equal(new[] { "Developer", "Tester", "Reviewer" },
            entry.GetProperty("roleVisibility").EnumerateArray().Select(item => item.GetString()));
        Xunit.Assert.Equal(guidance,
            File.ReadAllText(Path.Combine(directory, "packages", task.Id.Value, "criteria-self-check.md")));

        var source = kernel.BuildTaskBriefSource(goal.Id, task.Id, workingDirectory: root, contextDirectory: directory);
        var brief = source.ProjectLegacyMarkedTextV1(emitTypedSourceBoundaries: false);
        Xunit.Assert.Contains("Read criteria-self-check.md in the context directory before WORKER_RESULT.", brief.Content, StringComparison.Ordinal);
        var package = WorkerProfileDispatcher.BuildContextPackage(goal, task, root, directory, brief, typedSource: source);
        var artifact = Xunit.Assert.Single(package.Artifacts,
            item => item.Identity.Value == "context/criteria-self-check.md");
        Xunit.Assert.Equal(ContextDeliveryMode.MandatoryFile, artifact.DeliveryMode);
        var delivered = File.ReadAllText(Path.Combine(root,
            artifact.MandatoryRelativePath!.Replace('/', Path.DirectorySeparatorChar)));
        Xunit.Assert.Equal(guidance, delivered);
        Xunit.Assert.Equal(WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes(delivered)), artifact.ContentHash);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Planner)]
    [Xunit.InlineData(AgentRole.Researcher)]
    public void OtherRolesRemovePriorFileGuidanceInsteadOfReceivingIt(AgentRole role)
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Update the label.", AgentRole.Developer);
        var task = new TaskSpec(TaskId.New(), "Inspect the label.", role);
        var goal = kernel.CreateGoal("Maintain the label.", [developer, task]);
        var writer = new WorkerArtifactWriter();
        var directory = writer.Write(goal, developer, root);
        Xunit.Assert.True(File.Exists(Path.Combine(directory, "criteria-self-check.md")));
        writer.Write(goal, task, root);
        Xunit.Assert.False(File.Exists(Path.Combine(directory, "criteria-self-check.md")));
        Xunit.Assert.DoesNotContain("criteria-self-check.md", File.ReadAllText(Path.Combine(directory, "artifact-registry.json")), StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("criteria-self-check.md",
            kernel.BuildTaskBrief(goal.Id, task.Id, workingDirectory: root, contextDirectory: directory).Content, StringComparison.Ordinal);
    }
}
