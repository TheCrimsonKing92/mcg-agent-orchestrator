public sealed class VerifiedRepositoryRootTests
{
    [Xunit.Fact]
    public void EnvironmentRootWinsForGitDirectoryAndFileAndInvalidValuesUseCallerPath()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "verified-root-tests", Guid.NewGuid().ToString("N"));
        var publishingRoot = Path.Combine(sandbox, "publishing");
        var candidateRoot = Path.Combine(sandbox, "candidate");
        var sourcePath = Path.Combine(publishingRoot, "tests", "Fixture.cs");
        try
        {
            Directory.CreateDirectory(Path.Combine(publishingRoot, ".git"));
            Directory.CreateDirectory(Path.Combine(publishingRoot, "tests"));
            Directory.CreateDirectory(Path.Combine(candidateRoot, "tests"));
            File.WriteAllText(Path.Combine(candidateRoot, "Mcg.AgentOrchestrator.sln"), "");

            string Resolve(string? value) => VerifiedRepositoryRoot.Resolve(
                sourcePath,
                name => name == VerifiedRepositoryRoot.VariableName ? value : null);

            Assert.Equal(publishingRoot, Resolve(null));
            Assert.Equal(publishingRoot, Resolve(""));
            Assert.Equal(publishingRoot, Resolve(candidateRoot));
            Assert.Equal(publishingRoot, Resolve(Path.Combine(sandbox, "missing")));

            Directory.CreateDirectory(Path.Combine(candidateRoot, ".git"));
            Assert.Equal(candidateRoot, Resolve(candidateRoot));
            Directory.Delete(Path.Combine(candidateRoot, ".git"));
            File.WriteAllText(Path.Combine(candidateRoot, ".git"), "gitdir: elsewhere");
            Assert.Equal(candidateRoot, Resolve(candidateRoot));
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }
}
