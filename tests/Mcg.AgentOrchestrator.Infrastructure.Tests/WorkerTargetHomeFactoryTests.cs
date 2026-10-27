using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: private filesystem roots, injected home lookup, and no processes or environment mutation.
public sealed class WorkerTargetHomeFactoryTests
{
    [Fact]
    public void For_ProjectWorkspace_CarriesProjectHomeAndManifestResolver()
    {
        using var homeFixture = CreateHomeFixture();
        using var targetFixture = new OrchestratorHomeRootSeparationTests.Fixture();
        using var dataFixture = new OrchestratorHomeRootSeparationTests.Fixture();
        var home = OrchestratorHome.ResolveForLaunch(homeFixture.Root, _ => null);
        var workspace = OrchestratorWorkspace.ForProject("foreign", targetFixture.Root,
            targetFixture.Root, dataRootDirectory: dataFixture.Root);

        var targetHome = WorkerTargetHomeFactory.For(home, workspace);

        Assert.NotNull(workspace.ProjectHomeDirectoryOrNull);
        Assert.Equal(workspace.ProjectHomeDirectoryOrNull, targetHome.ProjectHomeDirectory);
        Assert.Equal(home.IsHome(workspace), targetHome.IsOrchestratorHome);
        Assert.False(targetHome.IsOrchestratorHome);
        Assert.NotNull(targetHome.AcceptanceManifestPath);
        Assert.Equal(WorkerAcceptanceManifestResolver.For(workspace)(targetFixture.Root),
            targetHome.AcceptanceManifestPath(targetFixture.Root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void For_DirectoryWorkspace_HasNoProjectHome(bool isHome)
    {
        using var homeFixture = CreateHomeFixture();
        using var targetFixture = new OrchestratorHomeRootSeparationTests.Fixture();
        var home = OrchestratorHome.ResolveForLaunch(homeFixture.Root, _ => null);
        var workspace = OrchestratorWorkspace.ForDirectory(isHome ? homeFixture.Root : targetFixture.Root);

        var targetHome = WorkerTargetHomeFactory.For(home, workspace);

        Assert.Null(targetHome.ProjectHomeDirectory);
        Assert.Equal(home.IsHome(workspace), targetHome.IsOrchestratorHome);
        Assert.Equal(isHome, targetHome.IsOrchestratorHome);
        Assert.NotNull(targetHome.AcceptanceManifestPath);
    }

    private static OrchestratorHomeRootSeparationTests.Fixture CreateHomeFixture()
    {
        var fixture = new OrchestratorHomeRootSeparationTests.Fixture();
        fixture.WriteFile("src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj");
        fixture.WriteFile("scripts", "Update-AppDllGitHeadMarker.ps1");
        return fixture;
    }
}
