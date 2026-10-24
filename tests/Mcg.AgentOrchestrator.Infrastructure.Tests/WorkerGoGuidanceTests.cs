using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every test owns its temporary roots, skill fixtures and context artifacts.
public sealed class WorkerGoGuidanceTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("worker-go-guidance-");
    private string GoRoot => Path.Combine(_root.FullName, "go");
    private string DotnetRoot => Path.Combine(_root.FullName, "dotnet");
    private string OrchestratorSkills => Path.Combine(_root.FullName, "orchestrator-skills");

    public WorkerGoGuidanceTests()
    {
        Directory.CreateDirectory(OrchestratorSkills);
        foreach (var root in new[] { GoRoot, DotnetRoot })
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../something");
            // A local copy must not cause the .NET skill to be selected for a Go root.
            var skillDirectory = Path.Combine(root, ".agents", "skills", "dotnet-windows-build-hygiene");
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "# Build hygiene fixture");
        }

        File.WriteAllText(Path.Combine(GoRoot, "go.mod"), "module example.com/myapp\n\ngo 1.21\n");
        File.WriteAllText(Path.Combine(DotnetRoot, "Control.sln"), string.Empty);
    }

    [Fact]
    public void GoDeveloperCurrentTaskNamesGoChecksAndNoDotnetHelper()
    {
        var (goal, task) = CreateTask(AgentRole.Developer);
        var goContext = Write(goal, task, GoRoot);
        var currentTask = Read(goContext, "current-task.md");

        AssertGoVerification(currentTask);
        Assert.Equal(WorkerStandingRules.RulesForRole(AgentRole.Developer, ProjectStack.Go), ReadRules(goContext));

        var dotnetCurrentTask = Read(Write(goal, task, DotnetRoot), "current-task.md");
        var invocation = OrchestratorBuildEvidenceCheck.FormatScriptInvocation(OrchestratorBuildEvidenceCheck.ResolveScriptPath());
        Assert.Contains(invocation + " <project.csproj> [project.csproj...]", dotnetCurrentTask, StringComparison.Ordinal);
        Assert.Contains("only sanctioned worker-side .NET build check", dotnetCurrentTask, StringComparison.Ordinal);
        Assert.DoesNotContain("go vet", dotnetCurrentTask, StringComparison.Ordinal);
    }

    [Fact]
    public void GoTesterReceivesGoGuidanceToo()
    {
        var (developerGoal, developerTask) = CreateTask(AgentRole.Developer);
        var developerCurrentTask = Read(Write(developerGoal, developerTask, GoRoot), "current-task.md");
        var (goal, task) = CreateTask(AgentRole.Tester);
        var context = Write(goal, task, GoRoot);
        var currentTask = Read(context, "current-task.md");

        AssertGoVerification(currentTask);
        Assert.Equal(BuildTestSection(developerCurrentTask), BuildTestSection(currentTask));
        Assert.Equal(WorkerStandingRules.RulesForRole(AgentRole.Tester, ProjectStack.Go), ReadRules(context));
    }

    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    public void GoRootSkillsOmitDotnetBuildHygiene(AgentRole role)
    {
        var (goal, task) = CreateTask(role);

        Assert.DoesNotContain("dotnet-windows-build-hygiene", Read(Write(goal, task, GoRoot), "selected-skills.md"), StringComparison.Ordinal);
        Assert.Contains("dotnet-windows-build-hygiene", Read(Write(goal, task, DotnetRoot), "selected-skills.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void GoDeterministicVerificationListsVet()
    {
        var (goal, task) = CreateTask(AgentRole.Developer);
        var verification = Read(Write(goal, task, GoRoot), "deterministic-verification.md");

        Assert.Contains("go vet ./...", verification, StringComparison.Ordinal);
        Assert.Contains("go test ./...", verification, StringComparison.Ordinal);
    }

    public void Dispose() => _root.Delete(recursive: true);

    private string Write(Goal goal, TaskSpec task, string root) =>
        WorkerContextArtifacts.Write(goal, task, root, orchestratorSkillDirectory: OrchestratorSkills);

    private static (Goal Goal, TaskSpec Task) CreateTask(AgentRole role)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Update the handler.", role);
        var goal = kernel.CreateGoal("Maintain the handler.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return (goal, task);
    }

    private static string Read(string context, string fileName) => File.ReadAllText(Path.Combine(context, fileName));

    private static string[] ReadRules(string context) =>
        File.ReadAllLines(Path.Combine(context, WorkerStandingRules.ContextFileName))
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..]).ToArray();

    private static string[] BuildTestSection(string currentTask)
    {
        var lines = currentTask.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var heading = Array.IndexOf(lines, "## Build/Test Verification");
        Assert.True(heading >= 0, "Current task must include build/test verification.");
        var section = lines.Skip(heading + 1).TakeWhile(line => line.Length > 0).ToArray();
        Assert.Equal(4, section.Length);
        return section;
    }

    private static void AssertGoVerification(string currentTask)
    {
        Assert.Contains("go build ./...", currentTask, StringComparison.Ordinal);
        Assert.Contains("go vet ./...", currentTask, StringComparison.Ordinal);
        Assert.Contains("go test ./...", currentTask, StringComparison.Ordinal);
        Assert.Contains("go test ./<package> -run '<TestRegex>'", currentTask, StringComparison.Ordinal);
        Assert.Contains("Report each result in WORKER_RESULT `tests`", currentTask, StringComparison.Ordinal);
        Assert.Contains("Do not run the .NET worker build helper or any `dotnet` command", currentTask, StringComparison.Ordinal);
        Assert.Contains("The acceptance gate re-runs the Go checks and decides the verdict", currentTask, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-WorkerBuildCheck", currentTask, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet test", currentTask, StringComparison.Ordinal);
        Assert.DoesNotContain("tests: not-run", currentTask, StringComparison.Ordinal);
    }
}
