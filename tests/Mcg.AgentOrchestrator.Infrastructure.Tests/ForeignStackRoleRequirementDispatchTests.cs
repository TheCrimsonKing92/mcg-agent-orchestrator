using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: independently owned directories and seeded dispatch repositories; no worker launch.
public sealed class ForeignStackRoleRequirementDispatchTests : WorkerDispatchTestSupport, IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("role-stack-guidance-");

    private static readonly string[] DotnetDeveloper =
    [
        "- Name the failing test in `tests: deferred`; never run tests.",
        "- Build changed projects with the worker build check."
    ];
    private static readonly string[] DotnetTester =
    [
        "- Name the test classes to run in evidence_request; never run tests.",
        "- Build only with the worker build check; do not modify source files.",
        "- Conductor-run selections use FullyQualifiedName~Class or Name~Method-symbol syntax and reject DisplayName text and a bare class name."
    ];
    private static readonly string[] GoDeveloper =
    [
        "- Report each go command and its result in `tests`.",
        "- Build and test with the go commands in the Standing Rules."
    ];
    private static readonly string[] GoTester =
    [
        "- Name the packages and tests to run in evidence_request.",
        "- Build only with `go build ./...`; do not modify source files.",
        "- Go selections: test_project is a package directory relative to go.mod; test_class is test names joined by `|`, such as `TestParse|TestFormat`."
    ];
    private static readonly string[] NeutralDeveloper =
    [
        "- Report each command and its result in `tests`.",
        "- Build and test with the commands in the Standing Rules."
    ];
    private static readonly string[] NeutralTester =
    [
        "- Name the tests to run in evidence_request.",
        "- Build only with the project's own build command; do not modify source files."
    ];

    [Fact]
    public void GuidanceStackFor_ResolvesMarkersDeclarationsAndTargetHome()
    {
        var go = CreateDirectory("go");
        File.WriteAllText(Path.Combine(go, "go.mod"), "module example.test/role-guidance\n");
        AssertStack(go, null, ProjectStack.Go);

        var node = CreateDirectory("node");
        File.WriteAllText(Path.Combine(node, "package.json"), "{}");
        AssertStack(node, WorkerTargetHome.NotHome, null);
        AssertStack(node, null, ProjectStack.Dotnet);

        var empty = CreateDirectory("empty");
        AssertStack(empty, WorkerTargetHome.NotHome, null);

        var dotnet = CreateDirectory("dotnet");
        File.WriteAllText(Path.Combine(dotnet, "App.sln"), string.Empty);
        AssertStack(dotnet, WorkerTargetHome.NotHome, ProjectStack.Dotnet);

        var projectHome = CreateDirectory("project-home");
        File.WriteAllText(Path.Combine(projectHome, "project-model.json"), "{\"schemaVersion\":4,\"stack\":\"go\"}");
        AssertStack(dotnet, new WorkerTargetHome(false, null, projectHome), ProjectStack.Go);

        AssertStack(VerifiedRepositoryRoot.Find(), WorkerTargetHome.Home, ProjectStack.Dotnet);
    }

    [Theory]
    [InlineData(AgentRole.Developer, ProjectStack.Go)]
    [InlineData(AgentRole.Tester, ProjectStack.Go)]
    [InlineData(AgentRole.Developer, null)]
    [InlineData(AgentRole.Tester, null)]
    public void PreparedPrompt_ForeignStackUsesExactRoleGuidance(AgentRole role, ProjectStack? stack)
    {
        var root = CreateSeededDispatchRepository();
        var marker = stack == ProjectStack.Go ? "go.mod" : "package.json";
        File.WriteAllText(Path.Combine(root, marker), stack == ProjectStack.Go
            ? "module example.test/role-guidance\n" : "{}");
        var targetHome = stack == ProjectStack.Go ? null : WorkerTargetHome.NotHome;
        Assert.Equal(stack, ProjectStackResolver.GuidanceStackFor(root, targetHome));

        var prompt = PreparePrompt(root, role, targetHome);
        var lines = prompt.ReplaceLineEndings("\n").Split('\n');
        var expected = stack == ProjectStack.Go
            ? role == AgentRole.Developer ? GoDeveloper : GoTester
            : role == AgentRole.Developer ? NeutralDeveloper : NeutralTester;
        AssertLinesOnce(lines, expected);
        AssertAbsent(lines, role == AgentRole.Developer ? DotnetDeveloper : DotnetTester);

        if (stack is null && role == AgentRole.Tester)
        {
            Assert.DoesNotContain("FullyQualifiedName~Class", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("test_project is a package directory", prompt, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(AgentRole.Developer, false)]
    [InlineData(AgentRole.Tester, false)]
    [InlineData(AgentRole.Developer, true)]
    [InlineData(AgentRole.Tester, true)]
    public void PreparedPrompt_HomeAndForeignDotnetKeepOriginalLines(AgentRole role, bool foreignDotnet)
    {
        var root = CreateSeededDispatchRepository();
        if (foreignDotnet)
            File.WriteAllText(Path.Combine(root, "App.sln"), string.Empty);
        var targetHome = foreignDotnet ? WorkerTargetHome.NotHome : null;
        Assert.Equal(ProjectStack.Dotnet, ProjectStackResolver.GuidanceStackFor(root, targetHome));

        var lines = PreparePrompt(root, role, targetHome).ReplaceLineEndings("\n").Split('\n');
        AssertLinesOnce(lines, role == AgentRole.Developer ? DotnetDeveloper : DotnetTester);
        AssertAbsent(lines, GoDeveloper.Concat(GoTester).Concat(NeutralDeveloper).Concat(NeutralTester));
    }

    private static string PreparePrompt(string root, AgentRole role, WorkerTargetHome? targetHome)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Update the label.", role);
        var goal = kernel.CreateGoal("Maintain the label.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var prepared = WorkerProfileDispatcher.PrepareTask(
            kernel, goal, task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(root, "prompts"), root, DateTimeOffset.UtcNow,
            providerName: "Anthropic", modelName: "claude-sonnet-4-6", targetHome: targetHome);
        return File.ReadAllText(prepared.PromptPath);
    }

    private string CreateDirectory(string name) => Directory.CreateDirectory(Path.Combine(_root.FullName, name)).FullName;

    private static void AssertStack(string directory, WorkerTargetHome? targetHome, ProjectStack? expected)
    {
        var actual = ProjectStackResolver.GuidanceStackFor(directory, targetHome);
        Assert.Equal(expected, actual);
        var home = targetHome ?? WorkerTargetHome.Home;
        Assert.Equal(ProjectStackResolver.GuidanceStack(
            ProjectStackResolver.Resolve(directory, home.ProjectHomeDirectory), home.IsOrchestratorHome), actual);
    }

    private static void AssertLinesOnce(string[] lines, IEnumerable<string> expected)
    {
        foreach (var line in expected)
            Assert.Equal(1, lines.Count(candidate => string.Equals(candidate, line, StringComparison.Ordinal)));
    }

    private static void AssertAbsent(string[] lines, IEnumerable<string> forbidden)
    {
        foreach (var line in forbidden)
            Assert.DoesNotContain(line, lines);
    }

    public void Dispose() => _root.Delete(recursive: true);
}
