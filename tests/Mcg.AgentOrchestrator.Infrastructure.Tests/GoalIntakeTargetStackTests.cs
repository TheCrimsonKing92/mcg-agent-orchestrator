using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private filesystem roots, injected home lookup, and no processes or environment mutation.
public sealed class GoalIntakeTargetStackTests
{
    [Fact]
    public void For_DeclaredGo_OverridesDotnetMarkerAndDrivesPlanner()
    {
        using var target = new OrchestratorHomeRootSeparationTests.Fixture();
        using var data = new OrchestratorHomeRootSeparationTests.Fixture();
        target.WriteFile("App.sln");
        var workspace = OrchestratorWorkspace.ForProject("crosswalk", target.Root,
            target.Root, dataRootDirectory: data.Root);
        WriteProjectModel(workspace, "{\"schemaVersion\":4,\"stack\":\"go\"}");

        var stack = GoalIntakeTargetStack.For(workspace, _ => null);
        var plan = GoalObjectivePlanner.Build(
            "Make the vitest fixture src/view.test.ts::renders parse into the summary and add test coverage",
            null, null, stack);

        Assert.Equal(Toolchain.Go, stack);
        Assert.Equal(new[] { "rg", "go build ./..., go vet ./..., go test ./..." }, plan.RequiredTools);
    }

    [Fact]
    public void For_UndeclaredStack_FallsBackToGoModule()
    {
        using var target = new OrchestratorHomeRootSeparationTests.Fixture();
        using var data = new OrchestratorHomeRootSeparationTests.Fixture();
        target.WriteFile("go.mod");
        var workspace = OrchestratorWorkspace.ForProject("crosswalk", target.Root,
            target.Root, dataRootDirectory: data.Root);
        WriteProjectModel(workspace, "{\"schemaVersion\":4}");

        Assert.Equal(Toolchain.Go, GoalIntakeTargetStack.For(workspace, _ => null));
    }

    [Theory]
    [InlineData(true, Toolchain.Node)]
    [InlineData(false, Toolchain.Unknown)]
    public void For_ForeignDirectory_ResolvesDetectedStack(bool hasPackage, Toolchain expected)
    {
        using var target = new OrchestratorHomeRootSeparationTests.Fixture();
        if (hasPackage)
            target.WriteFile("package.json");
        var workspace = OrchestratorWorkspace.ForDirectory(target.Root);

        Assert.Equal(expected, GoalIntakeTargetStack.For(workspace, _ => null));
    }

    [Fact]
    public void For_HomeMarkers_ReturnsNoStack()
    {
        using var home = CreateHomeFixture();
        var workspace = OrchestratorWorkspace.ForDirectory(home.Root);

        Assert.Null(GoalIntakeTargetStack.For(workspace, _ => null));
    }

    [Fact]
    public void For_ConfiguredHomeWithoutMarkers_ReturnsNoStack()
    {
        using var home = new OrchestratorHomeRootSeparationTests.Fixture();
        var workspace = OrchestratorWorkspace.ForDirectory(home.Root);

        Assert.Null(GoalIntakeTargetStack.For(workspace,
            name => name == OrchestratorHome.EnvironmentVariable ? home.Root : null));
    }

    [Theory]
    [InlineData("Make the vitest fixture src/view.test.ts::renders parse into the summary and add test coverage")]
    [InlineData("Implement src/App/Handler.cs with dotnet tests")]
    [InlineData("Fix the npm build in src/view.ts")]
    [InlineData("Fix the pytest suite in tests/test_parse.py")]
    [InlineData("Fix the golang build in cmd/server/main.go")]
    [InlineData("Inspect docs/usage.md and summarize current behavior")]
    public void Build_HomeTarget_PreservesRequiredTools(string objective)
    {
        using var home = CreateHomeFixture();
        var workspace = OrchestratorWorkspace.ForDirectory(home.Root);
        var baseline = GoalObjectivePlanner.Build(objective, null, null);

        var plan = GoalObjectivePlanner.Build(objective, null, null,
            GoalIntakeTargetStack.For(workspace, _ => null));

        Assert.Equal(baseline.RequiredTools, plan.RequiredTools);
    }

    private static void WriteProjectModel(OrchestratorWorkspace workspace, string json)
    {
        var projectHome = workspace.ProjectHomeDirectoryOrNull!;
        Directory.CreateDirectory(projectHome);
        File.WriteAllText(Path.Combine(projectHome, "project-model.json"), json);
    }

    private static OrchestratorHomeRootSeparationTests.Fixture CreateHomeFixture()
    {
        var fixture = new OrchestratorHomeRootSeparationTests.Fixture();
        fixture.WriteFile("src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj");
        fixture.WriteFile("scripts", "Update-AppDllGitHeadMarker.ps1");
        return fixture;
    }
}
