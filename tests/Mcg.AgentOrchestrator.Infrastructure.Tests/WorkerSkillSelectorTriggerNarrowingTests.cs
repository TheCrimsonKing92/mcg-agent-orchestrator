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

    [Xunit.Fact]
    public void DefaultAndExplicitHomeKeepBuildHygieneFallback()
    {
        var target = CreateEmptyTarget();
        var (goal, task) = CreateDeveloper("Improve the parser.");
        var skillDirectory = Path.Combine(workingDirectory, ".agents", "skills");
        Assert.True(new WorkerTargetHome().IsOrchestratorHome);

        var defaultSkills = WorkerContextArtifacts.SelectSkillRequirements(goal, task, target, skillDirectory);
        var homeSkills = WorkerContextArtifacts.SelectSkillRequirements(
            goal, task, target, skillDirectory, WorkerTargetHome.Home);

        Assert.Equal(defaultSkills, homeSkills);
        var hygiene = Assert.Single(defaultSkills, skill => skill.Name == "dotnet-windows-build-hygiene");
        Assert.Equal(WorkerSkillSource.Orchestrator, hygiene.ResolvedSource);
        Assert.Equal(Path.Combine(skillDirectory, hygiene.Name, "SKILL.md"), hygiene.ResolvedPath);
    }

    [Xunit.Fact]
    public void NonHomeOmitsHomeSkillsEvenWithSeededInstallDirectory()
    {
        var target = CreateEmptyTarget();
        var (goal, task) = CreateDeveloper("Run dogfood-log for the parser.");
        var skillDirectory = Path.Combine(workingDirectory, ".agents", "skills");
        var requirements = WorkerContextArtifacts.SelectSkillRequirements(
            goal, task, target, skillDirectory, WorkerTargetHome.NotHome);
        var context = WorkerContextArtifacts.Write(goal, task, target,
            orchestratorSkillDirectory: skillDirectory, targetHome: WorkerTargetHome.NotHome);
        var manifest = File.ReadAllText(Path.Combine(context, "selected-skills.md"));

        foreach (var name in new[] { "dotnet-windows-build-hygiene", "orchestrator-dogfood" })
        {
            Assert.DoesNotContain(requirements, skill => skill.Name == name);
            Assert.DoesNotContain(name, manifest, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(context, ".agents", "skills", name, "SKILL.md")));
        }
        // The unrelated standing skill retains its install fallback.
        var verification = Assert.Single(requirements, skill => skill.Name == "verification-before-completion");
        Assert.Equal(WorkerSkillSource.Orchestrator, verification.ResolvedSource);
    }

    [Xunit.Theory]
    [Xunit.InlineData("dotnet-windows-build-hygiene")]
    [Xunit.InlineData("orchestrator-dogfood")]
    public void NonHomeKeepsOnlyTheWorktreeProvidedSkill(string name)
    {
        var target = CreateEmptyTarget();
        var (goal, task) = CreateDeveloper("Run dogfood-log for the parser.");
        var localPath = Path.Combine(target, ".agents", "skills", name, "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllText(localPath, "Target-owned instructions");
        var skillDirectory = Path.Combine(workingDirectory, ".agents", "skills");

        var requirements = WorkerContextArtifacts.SelectSkillRequirements(
            goal, task, target, skillDirectory, WorkerTargetHome.NotHome);
        var skill = Assert.Single(requirements, skill => skill.Name == name);
        Assert.Equal(WorkerSkillSource.WorkingDirectory, skill.ResolvedSource);
        Assert.Equal(localPath, skill.ResolvedPath);
        Assert.Null(skill.OrchestratorCandidatePath);
        Assert.DoesNotContain(requirements, other => other.Name != name &&
            WorkerSkillSelector.RequiresHomeOrWorktreeCopy(other.Name));

        var context = WorkerContextArtifacts.Write(goal, task, target,
            orchestratorSkillDirectory: skillDirectory, targetHome: WorkerTargetHome.NotHome);
        Assert.Contains($"- {name}{Environment.NewLine}  Path: {skill.RelativePath}" +
            $"{Environment.NewLine}  Status: available",
            File.ReadAllText(Path.Combine(context, "selected-skills.md")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(context, skill.RelativePath)));
    }

    [Xunit.Fact]
    public void NonHomeLocalDogfoodStillRequiresKeywordTrigger()
    {
        var (goal, task) = CreateDeveloper("Improve the parser.");
        var requirements = WorkerContextArtifacts.SelectSkillRequirements(goal, task, workingDirectory,
            targetHome: WorkerTargetHome.NotHome);

        Assert.DoesNotContain(requirements, skill => skill.Name == "orchestrator-dogfood");
        Assert.Contains(requirements, skill => skill.Name == "dotnet-windows-build-hygiene");
    }

    [Xunit.Theory]
    [Xunit.InlineData("dotnet-windows-build-hygiene")]
    [Xunit.InlineData("orchestrator-dogfood")]
    public void NonHomeResolverNeverOffersInstallFallback(string name)
    {
        var target = CreateEmptyTarget();
        var relativePath = Path.Combine(".agents", "skills", name, "SKILL.md");
        var resolver = new WorkerSkillResolver(Path.Combine(workingDirectory, ".agents", "skills"),
            WorkerTargetHome.NotHome);

        var skill = resolver.Resolve(target, name, relativePath, "usage", "reason");

        Assert.Equal(WorkerSkillSource.Missing, skill.ResolvedSource);
        Assert.Null(skill.ResolvedPath);
        Assert.Null(skill.OrchestratorCandidatePath);
    }

    private string CreateEmptyTarget()
    {
        var target = Path.Combine(workingDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        return target;
    }

    private static (Goal Goal, TaskSpec Task) CreateDeveloper(string description)
    {
        var task = new TaskSpec(TaskId.New(), description, AgentRole.Developer);
        return (new AgentOrchestratorKernel().CreateGoal("Improve the parser", [task]), task);
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
