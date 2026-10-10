using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CandidateTreeProbeProjectFileFallbackTests
{
    [Theory]
    [InlineData("Domain.csproj")]
    [InlineData("src/Domain.fsproj")]
    [InlineData("src/Domain/Domain.csproj")]
    [InlineData("src/Domain/Domain.vbproj")]
    [InlineData("src/Domain/Domain.CSPROJ")]
    public void GitRootWithProjectWithinTwoLevelsUsesFileSystemProbe(string projectPath)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: isolated-fixture");
            WriteProject(root, projectPath);

            var probe = CandidateTreeProbe.ForRepositoryRoot(root);

            Assert.NotSame(CandidateTreeProbe.AssumeAllPresent, probe);
            Assert.False(probe.Exists(AcceptancePolicyShardPlanner.CoreTestsProject));
            Assert.True(probe.Exists(projectPath));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData("obj/Domain.csproj")]
    [InlineData("src/obj/Domain.csproj")]
    [InlineData("src/OBJ/Domain.csproj")]
    [InlineData("bin/Domain.fsproj")]
    [InlineData("src/bin/Domain.vbproj")]
    [InlineData("src/nested/Domain/Domain.csproj")]
    public void GitRootWithOnlyExcludedOrDeepProjectsAssumesAllPresent(string projectPath)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: isolated-fixture");
            WriteProject(root, projectPath);

            Assert.Same(CandidateTreeProbe.AssumeAllPresent, CandidateTreeProbe.ForRepositoryRoot(root));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void ProjectRootWithoutGitMetadataAssumesAllPresent()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            WriteProject(root, "src/Domain/Domain.csproj");

            Assert.Same(CandidateTreeProbe.AssumeAllPresent, CandidateTreeProbe.ForRepositoryRoot(root));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static void WriteProject(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />");
    }
}
