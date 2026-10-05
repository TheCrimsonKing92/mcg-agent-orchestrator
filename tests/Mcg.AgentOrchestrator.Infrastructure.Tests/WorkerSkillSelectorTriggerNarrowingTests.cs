using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerSkillSelectorTriggerNarrowingTests : IDisposable
{
    private readonly string workingDirectory = CreateSeededWorkingDirectory();

    [Xunit.Fact]
    public void WeeklyGoalReportKeepsTesterDefaultsWithoutDogfood()
    {
        var task = new TaskSpec(TaskId.New(),
            "Correct the goal count shown in the weekly report.", AgentRole.Tester,
            "Check the printed goal count.");
        var goal = new AgentOrchestratorKernel().CreateGoal(
            "Fix the goal total in the weekly report", [task]);

        var requirements = new WorkerSkillSelector().SelectSkillRequirements(goal, task, workingDirectory);
        var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory);
        var manifest = File.ReadAllText(Path.Combine(contextDirectory, "selected-skills.md"));

        Assert.DoesNotContain(requirements, skill => skill.Name == "orchestrator-dogfood");
        Assert.DoesNotContain("orchestrator-dogfood", manifest, StringComparison.Ordinal);
        foreach (var name in new[] { "dotnet-windows-build-hygiene", "orchestrator-worker-verification" })
        {
            Assert.Contains(requirements, skill => skill.Name == name);
            Assert.Contains($"- {name}{Environment.NewLine}", manifest, StringComparison.Ordinal);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("Run simple-goal and record dogfood-log evidence for the parser change.",
        "Check the dogfood-log entry.")]
    [Xunit.InlineData("Add a backlog-add entry for the parser follow-up.", "Check the parser output.")]
    public void OperatorCommandsSelectAvailableDogfood(string description, string verificationPlan)
    {
        var task = new TaskSpec(TaskId.New(), description, AgentRole.Developer, verificationPlan);
        var goal = new AgentOrchestratorKernel().CreateGoal("Tidy the parser", [task]);

        var requirements = new WorkerSkillSelector().SelectSkillRequirements(goal, task, workingDirectory);
        var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory);
        var manifest = File.ReadAllText(Path.Combine(contextDirectory, "selected-skills.md"));

        var dogfood = Assert.Single(requirements, skill => skill.Name == "orchestrator-dogfood");
        Assert.True(dogfood.Available);
        Assert.True(File.Exists(Path.Combine(workingDirectory, dogfood.RelativePath)));
        Assert.Contains($"- orchestrator-dogfood{Environment.NewLine}  Path: {dogfood.RelativePath}" +
            $"{Environment.NewLine}  Status: available", manifest, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ParserControllerAndScreenshotDoNotSelectRetiredSkills()
    {
        var task = new TaskSpec(TaskId.New(),
            "Rename the controller class in the report parser and add an e2e check with a screenshot.",
            AgentRole.Developer, "Check the parser output.");
        var goal = new AgentOrchestratorKernel().CreateGoal("Tidy the parser", [task]);

        var requirements = new WorkerSkillSelector().SelectSkillRequirements(goal, task, workingDirectory);
        var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory);
        var manifest = File.ReadAllText(Path.Combine(contextDirectory, "selected-skills.md"));

        foreach (var name in new[] { "aspnet-core", "playwright" })
        {
            Assert.DoesNotContain(requirements, skill => skill.Name == name);
            Assert.DoesNotContain(name, manifest, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact]
    public void DashboardNotesDoNotSelectBuildHygieneOrDogfood()
    {
        var task = new TaskSpec(TaskId.New(), "Read the dashboard layout notes.",
            AgentRole.Reviewer, "Read the notes.");
        var goal = new AgentOrchestratorKernel().CreateGoal("Review the dashboard layout notes", [task]);

        var requirements = new WorkerSkillSelector().SelectSkillRequirements(goal, task, workingDirectory);

        Assert.DoesNotContain(requirements, skill => skill.Name == "dotnet-windows-build-hygiene");
        Assert.DoesNotContain(requirements, skill => skill.Name == "orchestrator-dogfood");
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
