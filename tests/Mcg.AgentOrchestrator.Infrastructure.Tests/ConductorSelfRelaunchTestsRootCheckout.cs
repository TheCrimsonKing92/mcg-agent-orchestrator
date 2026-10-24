using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("ProcessSpawningHostBound")]
public sealed class ConductorSelfRelaunchTestsRootCheckout
{
    [Theory]
    [InlineData("feature")]
    [InlineData("detached")]
    [InlineData("dirty")]
    [InlineData("clean")]
    [InlineData("null-branch")]
    public void WrongBranchOrDirtyRootFailsBeforeAnyBuild(string state)
    {
        var root = Directory.CreateTempSubdirectory("root-checkout-relaunch-").FullName;
        try
        {
            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);
            Git(repo, "init", "-b", "main");
            Git(repo, "config", "user.email", "tests@example.invalid");
            Git(repo, "config", "user.name", "Tests");
            File.WriteAllText(Path.Combine(repo, "README.md"), "initial\n");
            Git(repo, "add", "README.md");
            Git(repo, "commit", "-m", "Initial");
            if (state is "feature" or "null-branch") Git(repo, "checkout", "-b", "feature");
            if (state == "detached") Git(repo, "checkout", "--detach", "HEAD");
            if (state is "dirty" or "null-branch") File.WriteAllText(Path.Combine(repo, "README.md"), "user changes\n");
            File.WriteAllText(Path.Combine(repo, "untracked.txt"), "untracked content");
            var calls = 0;
            var store = new LandingAppBuildStore(Path.Combine(root, "store"), build: (_, _) =>
            {
                calls++;
                return new(23, string.Empty, "fixture compiler failure");
            });
            var options = new ConductorSuccessorStagingOptions(
                RepositoryRoot: repo,
                AppProjectPath: Path.Combine(repo, "App.csproj"),
                AppDllPath: Path.Combine(root, "output", "App.dll"),
                UpdateHeadMarkerScriptPath: Path.Combine(root, "marker.ps1"),
                ResolveRunDirectoryScriptPath: Path.Combine(root, "resolver.ps1"),
                StateStorePath: Path.Combine(root, "state.db"),
                AgentCatalogPath: Path.Combine(root, "agents.json"),
                WorkerProfilePath: Path.Combine(root, "workers.json"),
                ModelFunctionCatalogPath: Path.Combine(root, "functions.json"),
                DotnetPath: "dotnet", PowerShellPath: "powershell")
            {
                RequiredCheckoutBranch = state == "null-branch" ? null : "main",
                LandingAppBuildStore = store
            };

            var failure = Assert.Throws<ConductorSelfRelaunchPreparationException>(() =>
                ConductorSelfRelaunch.PrepareSuccessor(options));

            Assert.Equal("build", failure.Phase);
            if (state is "clean" or "null-branch")
            {
                Assert.Equal(1, calls);
                Assert.Contains("build merged conductor failed", failure.Message);
            }
            else
            {
                Assert.Equal(0, calls);
                Assert.Contains("main", failure.Message);
                Assert.Contains(state == "feature" ? "refs/heads/feature" : state == "detached" ? "detached HEAD" : "refs/heads/main", failure.Message);
                if (state == "dirty") Assert.Contains("first changed path: README.md", failure.Message);
                Assert.False(Directory.Exists(Path.Combine(root, "store")));
            }
        }
        finally { ScriptSandboxCleanup.DeleteDirectoryTree(root); }
    }

    private static void Git(string repo, params string[] args)
    {
        var result = InfrastructureTestSupport.RunGitProbe(repo, ["-C", repo, .. args]);
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        Assert.True(result.Succeeded, $"git probe failed: {result}");
    }
}
