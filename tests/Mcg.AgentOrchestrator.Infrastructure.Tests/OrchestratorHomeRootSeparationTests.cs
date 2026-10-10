using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: injected environment and unique private filesystem fixtures.
public sealed class OrchestratorHomeRootSeparationTests
{
    [Fact]
    public void ForInstallRoot_NoCheckout_DisablesHomeAndStaging()
    {
        using var fixture = new Fixture();
        AssertInstallOnly(OrchestratorHome.ForInstallRoot(fixture.Root), fixture.Root);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ResolveForLaunch_MissingSourceArtifact_DisablesHomeAndStaging(
        bool appProject, bool headMarker)
    {
        using var fixture = new Fixture();
        // Even a Git worktree marker cannot make a target repository a source root.
        File.WriteAllText(Path.Combine(fixture.Root, ".git"), "gitdir: target");
        if (appProject)
            fixture.WriteFile("src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj");
        if (headMarker)
            fixture.WriteFile("scripts", "Update-AppDllGitHeadMarker.ps1");

        AssertInstallOnly(OrchestratorHome.ResolveForLaunch(fixture.Root, _ => null), fixture.Root);
    }

    [Fact]
    public void ResolveForLaunch_Checkout_PreservesBothRootsAndStaging()
    {
        using var fixture = new Fixture();
        fixture.WriteFile("src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj");
        fixture.WriteFile("scripts", "Update-AppDllGitHeadMarker.ps1");

        var home = OrchestratorHome.ResolveForLaunch(fixture.Root, _ => null);
        AssertCheckout(home, fixture.Root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveForLaunch_ConfiguredHome_PreservesBothRootsAndStaging(bool relative)
    {
        using var fixture = new Fixture();
        var configured = relative ? Path.Combine("relative-home", "nested") : fixture.Root + "-home";
        var home = OrchestratorHome.ResolveForLaunch(fixture.Root, name =>
        {
            Assert.Equal(OrchestratorHome.EnvironmentVariable, name);
            return "  " + configured + "  ";
        });

        AssertCheckout(home, Path.GetFullPath(configured));
    }

    [Fact]
    public void ExportForDescendants_InstallOnly_DoesNotDeclareSourceHome()
    {
        using var fixture = new Fixture();
        var exported = new Dictionary<string, string?>();
        var home = OrchestratorHome.ResolveForLaunch(fixture.Root, _ => null);
        OrchestratorHome.ExportForDescendants(home, (name, value) => exported.Add(name, value));

        Assert.Empty(exported);
        var childHome = OrchestratorHome.ResolveForLaunch(fixture.Root,
            name => exported.GetValueOrDefault(name));
        AssertInstallOnly(childHome, fixture.Root);
    }

    [Fact]
    public void ExportForDescendants_Checkout_PreservesRootsAcrossDirectoryChange()
    {
        using var fixture = new Fixture();
        fixture.WriteFile("src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj");
        fixture.WriteFile("scripts", "Update-AppDllGitHeadMarker.ps1");
        var home = OrchestratorHome.ResolveForLaunch(fixture.Root, _ => null);
        var exported = new Dictionary<string, string?>();
        OrchestratorHome.ExportForDescendants(home, (name, value) => exported.Add(name, value));

        Assert.Single(exported);
        Assert.Equal(home.InstallRootDirectory, exported[OrchestratorHome.EnvironmentVariable]);
        var childHome = OrchestratorHome.ResolveForLaunch(fixture.Root + "-target",
            name => exported.GetValueOrDefault(name));
        Assert.Equal(home.InstallRootDirectory, childHome.InstallRootDirectory);
        Assert.Equal(home.SourceRootDirectory, childHome.SourceRootDirectory);
    }

    private static void AssertInstallOnly(OrchestratorHome home, string installRoot)
    {
        Assert.Equal(Path.GetFullPath(installRoot), home.InstallRootDirectory);
        Assert.Null(home.SourceRootDirectory);
        foreach (var workspace in new[]
        {
            OrchestratorWorkspace.ForDirectory(installRoot),
            OrchestratorWorkspace.ForProject("alpha", installRoot),
            OrchestratorWorkspace.ForDirectory(installRoot + "-target"),
            OrchestratorWorkspace.ForDirectory(installRoot, installRoot + "-execution")
        })
        {
            Assert.False(home.IsHome(workspace));
            Assert.False(home.IsHome(workspace.ExecutionDirectory));
            Assert.Null(home.CreateSuccessorStagingOptions(workspace));
        }
    }

    private static void AssertCheckout(OrchestratorHome home, string root)
    {
        var legacyHome = OrchestratorHome.Resolve(root, _ => null);
        Assert.Equal(Path.GetFullPath(root), home.InstallRootDirectory);
        Assert.Equal(legacyHome.InstallRootDirectory, home.SourceRootDirectory);
        var workspace = OrchestratorWorkspace.ForProject("alpha", root);
        Assert.True(home.IsHome(workspace));
        var staging = Assert.IsType<ConductorSuccessorStagingOptions>(home.CreateSuccessorStagingOptions(workspace));
        var legacy = Assert.IsType<ConductorSuccessorStagingOptions>(legacyHome.CreateSuccessorStagingOptions(workspace));
        Assert.Equal(root, staging.RepositoryRoot);
        Assert.Equal(Path.Combine(root, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"),
            staging.AppProjectPath);
        Assert.Equal(Path.Combine(root, "scripts", "Update-AppDllGitHeadMarker.ps1"), staging.UpdateHeadMarkerScriptPath);
        Assert.Equal(Path.Combine(root, "scripts", "resolve-run-dir.ps1"), staging.ResolveRunDirectoryScriptPath);
        var buildKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root))))[..16];
        Assert.Equal(Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("self-relaunch-build"),
            buildKey, "Mcg.AgentOrchestrator.App.dll"), staging.AppDllPath);
        Assert.Equal(workspace.SqliteStatePath, staging.StateStorePath);
        Assert.Equal(workspace.AgentCatalogPath, staging.AgentCatalogPath);
        Assert.Equal(workspace.WorkerProfilePath, staging.WorkerProfilePath);
        Assert.Equal(workspace.ModelFunctionCatalogPath, staging.ModelFunctionCatalogPath);
        Assert.NotNull(staging.LandingAppBuildStore);
        Assert.Equal(legacy.RepositoryRoot, staging.RepositoryRoot);
        Assert.Equal(legacy.AppProjectPath, staging.AppProjectPath);
        Assert.Equal(legacy.AppDllPath, staging.AppDllPath);
        Assert.Equal(legacy.UpdateHeadMarkerScriptPath, staging.UpdateHeadMarkerScriptPath);
        Assert.Equal(legacy.ResolveRunDirectoryScriptPath, staging.ResolveRunDirectoryScriptPath);
        Assert.Equal(legacy.StateStorePath, staging.StateStorePath);
        Assert.Equal(legacy.AgentCatalogPath, staging.AgentCatalogPath);
        Assert.Equal(legacy.WorkerProfilePath, staging.WorkerProfilePath);
        Assert.Equal(legacy.ModelFunctionCatalogPath, staging.ModelFunctionCatalogPath);
        Assert.Equal(legacy.DotnetPath, staging.DotnetPath);
        Assert.Equal(legacy.PowerShellPath, staging.PowerShellPath);
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "home-roots-" + Guid.NewGuid().ToString("N"));

        internal Fixture() => Directory.CreateDirectory(Root);

        internal void WriteFile(params string[] segments)
        {
            var path = Path.Combine([Root, .. segments]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
