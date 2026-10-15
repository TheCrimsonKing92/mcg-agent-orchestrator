using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: unique roots and explicit registry; no ambient environment mutation.
public sealed class WorkspaceInvocationBindingTests
{
    [Fact]
    public void RootDiscoveryRecognizesGitFileAndExplicitRootWins()
    {
        var root = Directory.CreateTempSubdirectory("workspace-git-file-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: fixture-metadata");
            var child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
            Assert.Equal(root, OrchestratorWorkspace.ResolveRepoRoot(child, root, readEnvironment: _ => null));
            Assert.Equal(root, OrchestratorWorkspace.ResolveRepoRoot(child, child, root, _ => child));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FixtureRequiresBothAbsoluteExpectations()
    {
        Assert.Throws<InvalidOperationException>(() => WorkspaceInvocationBinding.Parse(["conductor", "setup", "--disposable-fixture"]));
        Assert.Throws<ArgumentException>(() => WorkspaceInvocationBinding.Parse(["--repository-root", "relative"]));
    }

    [Fact]
    public void ResolvedStateAndInheritedRootMustMatchBeforeWrites()
    {
        var root = Directory.CreateTempSubdirectory("workspace-binding-").FullName;
        try
        {
            var state = Path.Combine(root, ".orchestrator");
            var binding = WorkspaceInvocationBinding.Parse(["conductor", "setup", "--repository-root", root,
                "--expected-state-directory", state, "--disposable-fixture"]);
            var registry = new OrchestratorProjectRegistry(Path.Combine(root, "registry"), Path.Combine(root, "data"));
            var workspace = OrchestratorWorkspace.ForDirectory(root, null, null, registry);
            binding.Verify(workspace);
            Assert.Equal(new[] { "conductor", "setup" }, binding.CommandArgs);
            Assert.Throws<InvalidOperationException>(() => binding.Verify(workspace with { OrchestratorDirectory = root }));
            Assert.Throws<InvalidOperationException>(() => binding.ValidateInheritedSelectors(root + "-decoy", null, null,
                new(null, []), new("default", [])));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("project")]
    [InlineData("tenant")]
    public void FixtureRefusesShadowedInheritedSelectors(string selector)
    {
        var root = Directory.CreateTempSubdirectory("workspace-binding-").FullName;
        try
        {
            var binding = WorkspaceInvocationBinding.Parse(["--repository-root", root, "--expected-state-directory",
                Path.Combine(root, ".orchestrator"), "--disposable-fixture"]);
            Assert.Throws<InvalidOperationException>(() => binding.ValidateInheritedSelectors(null,
                selector == "project" ? "decoy" : null, selector == "tenant" ? "decoy" : null,
                new("default", []), new("default", [])));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
