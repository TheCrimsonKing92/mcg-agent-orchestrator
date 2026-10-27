using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private target, skill and artifact roots; no repository marker or process launches.
public sealed class WorkerStackVerificationGuidanceWriterParityTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stack-guidance-parity-");
    private string OrchestratorSkills => Path.Combine(_root.FullName, "install-skills");

    [Theory]
    [InlineData("MyApp.csproj", true, AgentRole.Developer)]
    [InlineData("MyApp.csproj", true, AgentRole.Tester)]
    [InlineData("MyApp.csproj", false, AgentRole.Developer)]
    [InlineData("MyApp.csproj", false, AgentRole.Tester)]
    [InlineData("go.mod", true, AgentRole.Developer)]
    [InlineData("go.mod", true, AgentRole.Tester)]
    [InlineData("go.mod", false, AgentRole.Developer)]
    [InlineData("go.mod", false, AgentRole.Tester)]
    [InlineData("package.json", true, AgentRole.Developer)]
    [InlineData("package.json", true, AgentRole.Tester)]
    [InlineData("package.json", false, AgentRole.Developer)]
    [InlineData("package.json", false, AgentRole.Tester)]
    [InlineData("pyproject.toml", true, AgentRole.Developer)]
    [InlineData("pyproject.toml", true, AgentRole.Tester)]
    [InlineData("pyproject.toml", false, AgentRole.Developer)]
    [InlineData("pyproject.toml", false, AgentRole.Tester)]
    [InlineData("", true, AgentRole.Developer)]
    [InlineData("", true, AgentRole.Tester)]
    [InlineData("", false, AgentRole.Developer)]
    [InlineData("", false, AgentRole.Tester)]
    public void Write_StackHomeAndRole_PreservesGuidancePlacement(
        string marker, bool isOrchestratorHome, AgentRole role)
    {
        var target = CreateTarget(marker);
        var (goal, task) = CreateTask(role);
        var toolchain = ProjectStackResolver.Resolve(target, null);
        var stack = ProjectStackResolver.GuidanceStack(toolchain, isOrchestratorHome);

        var context = Write(goal, task, target, isOrchestratorHome ? null : new WorkerTargetHome(false));

        var currentTask = ReadLines(context, "current-task.md");
        Assert.Equal(WorkerStackVerificationGuidance.CurrentTaskSection(stack), currentTask.Skip(6).Take(6));

        var deterministic = ReadLines(context, "deterministic-verification.md");
        var findings = Array.IndexOf(deterministic, "## Findings");
        Assert.True(findings >= 0, "Deterministic verification must include the Findings heading.");
        var section = WorkerStackVerificationGuidance.DeterministicVerificationSection(toolchain, goal.Id);
        Assert.Equal(section, deterministic.Skip(findings + 1).Take(section.Count));

        var brokers = ReadLines(context, "workflow-brokers.md");
        if (marker == "MyApp.csproj" && role == AgentRole.Developer)
        {
            var start = Array.IndexOf(brokers, "- build-only");
            Assert.True(start >= 0, "Dotnet Developer brokers must include the build-only action.");
            var expected = WorkerStackVerificationGuidance.DeveloperBuildOnlyBrokerLines();
            Assert.Equal(expected, brokers.Skip(start).Take(expected.Count));
        }
        else
        {
            Assert.DoesNotContain("- build-only", brokers);
        }
    }

    [Fact]
    public void Write_Reviewer_OmitsImplementationGuidance()
    {
        var target = CreateTarget("MyApp.csproj");
        var (goal, task) = CreateTask(AgentRole.Reviewer);

        var context = Write(goal, task, target);

        Assert.DoesNotContain("## Build/Test Verification", ReadLines(context, "current-task.md"));
        var deterministic = ReadLines(context, "deterministic-verification.md");
        string[] headings =
        [
            "## Worker Build Check", "## Go Verification", "## Node.js Verification",
            "## Python Verification", "## Verification"
        ];
        foreach (var heading in headings)
            Assert.DoesNotContain(heading, deterministic);
        Assert.DoesNotContain("- build-only", ReadLines(context, "workflow-brokers.md"));
    }

    [Fact]
    public void Source_GuidanceText_HasOneOwner()
    {
        var root = VerifiedRepositoryRoot.Find();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !RepositorySourceInventory.IsExcludedRelativePath(Path.GetRelativePath(root, path)))
            .Select(path => (Path: Path.GetRelativePath(root, path).Replace('\\', '/'), Text: File.ReadAllText(path)))
            .ToArray();
        string[] ownedText =
        [
            "Worker Build Check", "Go Verification", "Node.js Verification", "Python Verification",
            "only sanctioned worker-side .NET build check", "README or CI configuration"
        ];

        foreach (var text in ownedText)
        {
            var owner = Assert.Single(sources.Where(source => source.Text.Contains(text, StringComparison.Ordinal)));
            Assert.Equal("src/Mcg.AgentOrchestrator.Execution/Workers/WorkerStackVerificationGuidance.cs", owner.Path);
        }
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

    private static string[] ReadLines(string context, string name) => File.ReadAllLines(Path.Combine(context, name));
}
