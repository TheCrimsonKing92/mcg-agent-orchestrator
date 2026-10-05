using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the seeded skill files live in this fixture's unique temporary root.
public sealed class ReviewerVerificationSkillSelectionTests : IDisposable
{
    private readonly string workingDirectory = CreateSeededWorkingDirectory();

    [Xunit.Fact]
    public void SelectSkillRequirements_ReviewerSignals_ExcludesVerificationOnlyForReviewer()
    {
        var selector = new WorkerSkillSelector();
        var tasks = new[]
        {
            AgentRole.Planner, AgentRole.Reviewer, AgentRole.Tester,
            AgentRole.Researcher, AgentRole.Developer
        }.Select(role => new TaskSpec(TaskId.New(), "Review worker result contract evidence.",
            role, "Inspect verification records and dispatch logs.")).ToArray();
        var goal = new AgentOrchestratorKernel().CreateGoal("Correct role guidance.", tasks);
        var selections = tasks.ToDictionary(task => task.RequiredRole,
            task => selector.SelectSkillRequirements(goal, task, workingDirectory));

        foreach (var selection in selections.Values)
        {
            Assert.All(selection, skill => Assert.True(skill.Available, skill.RelativePath));
        }
        Assert.Contains(selections[AgentRole.Planner],
            skill => skill.Name == "criterion-ownership-planning");
        Assert.DoesNotContain(selections[AgentRole.Reviewer],
            skill => skill.Name == "orchestrator-worker-verification");
        Assert.Contains(selections[AgentRole.Tester],
            skill => skill.Name == "orchestrator-worker-verification");
        Assert.Contains(selections[AgentRole.Researcher], skill => skill.Name == "research-evidence");
        Assert.Contains(selections[AgentRole.Developer],
            skill => skill.Name == "verification-before-completion");
        Assert.Contains(selections[AgentRole.Developer],
            skill => skill.Name == "orchestrator-worker-verification");
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(workingDirectory);

    private static string CreateSeededWorkingDirectory()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        foreach (var name in new[]
        {
            "criterion-ownership-planning", "dotnet-windows-build-hygiene", "orchestrator-dogfood",
            "orchestrator-worker-verification", "research-evidence", "skill-authoring",
            "systematic-debugging", "verification-before-completion"
        })
        {
            var directory = Path.Combine(root, ".agents", "skills", name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"# {name}\n");
        }
        return root;
    }
}
