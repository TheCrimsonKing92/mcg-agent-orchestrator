using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private target, project-home, skill and artifact roots; no git workspace or worker launches.
public sealed class WorkerNeutralStackGuidanceTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("neutral-guidance-");
    private static readonly string[] ArtifactNames = ["rule.md", "current-task.md", "selected-skills.md",
        "deterministic-verification.md", "workflow-brokers.md"];
    private static readonly string[] Forbidden = ["Invoke-WorkerBuildCheck", "dotnet test",
        "dotnet-windows-build-hygiene", "<project.csproj>", "xunit"];
    private static readonly string[] NeutralRules =
    [
        "Tests never decide pass or fail on wall-clock time; bounded waits are hang-only and name the event that did not happen.",
        "Before changing any message, reason or diagnostic text, search the tests for exact-equality assertions on it, and if one exists put new information in a separate field instead of editing the asserted text.",
        "Run the project's own build and test commands from the worktree root and report each command and its result in `tests`."
    ];
    private static readonly string[] NeutralBuildLines =
    [
        "- Run the project's own build and test commands from the worktree root before WORKER_RESULT; deterministic-verification.md names the commands this checkout suggests.",
        "- Report each command and its result in WORKER_RESULT `tests`, for example `tests: pass - <command> ok` or `tests: fail - <command>: <first failure>`.",
        "- This project has no orchestrator build helper; use only the project's own tooling.",
        "- The acceptance gate re-runs the project's acceptance manifest checks and decides the verdict."
    ];
    private string OrchestratorSkills => Path.Combine(_root.FullName, "install-skills");

    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    public void Write_NonHomeNode_UsesNeutralGuidanceAndNodeVerification(AgentRole role)
    {
        var target = CreateTarget("package.json");
        var (goal, task) = CreateTask(role);

        var context = Write(goal, task, target, new WorkerTargetHome(false));

        AssertNoDotnetGuidance(context, ArtifactNames);
        AssertNeutralGuidance(context, role);
        Assert.Contains("## Node.js Verification", Read(context, "deterministic-verification.md"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentRole.Developer, "pyproject.toml")]
    [InlineData(AgentRole.Tester, "pyproject.toml")]
    [InlineData(AgentRole.Developer, "")]
    [InlineData(AgentRole.Tester, "")]
    public void Write_NonHomePythonOrUnknown_UsesNeutralGuidance(AgentRole role, string marker)
    {
        var target = CreateTarget(marker);
        var (goal, task) = CreateTask(role);

        var context = Write(goal, task, target, new WorkerTargetHome(false));

        AssertNoDotnetGuidance(context, ArtifactNames[..3]);
        AssertNeutralGuidance(context, role);
    }

    [Theory]
    [InlineData(AgentRole.Developer, "")]
    [InlineData(AgentRole.Tester, "")]
    [InlineData(AgentRole.Developer, "package.json")]
    [InlineData(AgentRole.Tester, "package.json")]
    public void Write_DefaultHome_PreservesDotnetGuidance(AgentRole role, string marker)
    {
        var target = CreateTarget(marker);
        var (goal, task) = CreateTask(role);

        var context = Write(goal, task, target);

        Assert.Equal(WorkerStandingRules.RulesForRole(role), ReadRules(context));
        Assert.Contains("- dotnet-windows-build-hygiene", Read(context, "selected-skills.md"), StringComparison.Ordinal);
        Assert.Contains("<project.csproj> [project.csproj...]", Read(context, "current-task.md"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentRole.Developer, ProjectStack.Dotnet, "Control.sln")]
    [InlineData(AgentRole.Tester, ProjectStack.Dotnet, "Control.sln")]
    [InlineData(AgentRole.Developer, ProjectStack.Go, "go.mod")]
    [InlineData(AgentRole.Tester, ProjectStack.Go, "go.mod")]
    public void Write_MatchingDeclaration_PreservesArtifactBytes(
        AgentRole role, ProjectStack stack, string marker)
    {
        var target = CreateTarget(marker);
        var projectHome = CreateProjectHome(stack);
        var (goal, task) = CreateTask(role);
        var context = Write(goal, task, target, new WorkerTargetHome(false));
        var before = ArtifactNames.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(context, name)));

        var afterContext = Write(goal, task, target, new WorkerTargetHome(false, projectHomeDirectory: projectHome));

        Assert.Equal(context, afterContext);
        foreach (var name in ArtifactNames)
            Assert.Equal(before[name], File.ReadAllBytes(Path.Combine(afterContext, name)));
    }

    [Fact]
    public void Write_DeclaredGoWithDotnetMarkers_UsesGoAcrossArtifacts()
    {
        var target = CreateTarget("App.sln");
        var projectHome = CreateProjectHome(ProjectStack.Go);
        var (goal, task) = CreateTask(AgentRole.Developer);
        Assert.Equal(Toolchain.Dotnet, TargetToolchainDetector.Detect(target));

        var context = Write(goal, task, target, new WorkerTargetHome(false, projectHomeDirectory: projectHome));

        Assert.Equal(WorkerStandingRules.RulesForRole(AgentRole.Developer, ProjectStack.Go), ReadRules(context));
        Assert.Contains("go vet ./...", string.Join('\n', BuildTestSection(context)), StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet-windows-build-hygiene", Read(context, "selected-skills.md"), StringComparison.Ordinal);
        Assert.Contains("## Go Verification", Read(context, "deterministic-verification.md"), StringComparison.Ordinal);
        Assert.Contains("Suggested command shape: `go test ./...`", Read(context, "workflow-brokers.md"), StringComparison.Ordinal);
        Assert.DoesNotContain("- build-only", Read(context, "workflow-brokers.md"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Researcher)]
    [InlineData(AgentRole.Reviewer)]
    public void RulesForRole_NeutralNonImplementationRole_RemainsEmpty(AgentRole role)
    {
        Assert.Empty(WorkerStandingRules.RulesForRole(role, null));
    }

    public void Dispose() => _root.Delete(recursive: true);

    private string CreateTarget(string marker)
    {
        var target = Directory.CreateDirectory(Path.Combine(_root.FullName, Guid.NewGuid().ToString("N"))).FullName;
        if (marker.Length > 0)
            File.WriteAllText(Path.Combine(target, marker), string.Empty);
        var skillDirectory = Path.Combine(target, ".agents", "skills", "dotnet-windows-build-hygiene");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "# Target-owned build hygiene fixture");
        Directory.CreateDirectory(OrchestratorSkills);
        return target;
    }

    private string CreateProjectHome(ProjectStack stack)
    {
        var projectHome = Directory.CreateDirectory(Path.Combine(_root.FullName, "project-home")).FullName;
        var model = new ProjectModel(ProjectModel.CurrentSchemaVersion, ".", [], [], [], [], [], [], [], [], Stack: stack);
        File.WriteAllText(Path.Combine(projectHome, "project-model.json"), ProjectModelJson.Serialize(model));
        return projectHome;
    }

    private string Write(Goal goal, TaskSpec task, string target, WorkerTargetHome? targetHome = null) =>
        WorkerContextArtifacts.Write(goal, task, target, orchestratorSkillDirectory: OrchestratorSkills, targetHome: targetHome);

    private static (Goal Goal, TaskSpec Task) CreateTask(AgentRole role)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Update the handler.", role);
        var goal = kernel.CreateGoal("Maintain the handler.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return (goal, task);
    }

    private static string Read(string context, string name) => File.ReadAllText(Path.Combine(context, name));

    private static string[] ReadRules(string context) =>
        File.ReadAllLines(Path.Combine(context, "rule.md"))
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal)).Select(line => line[2..]).ToArray();

    private static string[] BuildTestSection(string context)
    {
        var lines = File.ReadAllLines(Path.Combine(context, "current-task.md"));
        var heading = Array.IndexOf(lines, "## Build/Test Verification");
        Assert.True(heading >= 0, "Current task must include the level-two Build/Test Verification heading.");
        return lines.Skip(heading + 1).TakeWhile(line => line.Length > 0).ToArray();
    }

    private static void AssertNoDotnetGuidance(string context, IEnumerable<string> artifacts)
    {
        foreach (var name in artifacts)
        foreach (var forbidden in Forbidden)
            Assert.DoesNotContain(forbidden, Read(context, name), StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNeutralGuidance(string context, AgentRole role)
    {
        Assert.Equal(NeutralRules, WorkerStandingRules.RulesForRole(role, null));
        Assert.Equal(WorkerStandingRules.RulesForRole(role, null), ReadRules(context));
        Assert.Equal(NeutralBuildLines, WorkerNeutralStackGuidance.BuildTestLines);
        Assert.Equal(NeutralBuildLines, BuildTestSection(context));
    }
}
