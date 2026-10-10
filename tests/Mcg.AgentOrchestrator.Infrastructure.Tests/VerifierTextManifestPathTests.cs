using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its temporary directories and changes no process-wide state.
public sealed class VerifierTextManifestPathTests
{
    [Fact]
    public void StateDirectoryManifest_IsNamedByPreflightAndStructuralCoverage()
    {
        using var fixture = new ManifestFixture();
        ManifestFixture.WriteInvalidManifest(fixture.ProjectManifest);
        Assert.True(File.Exists(fixture.ProjectManifest));
        Assert.False(File.Exists(fixture.WorktreeManifest));
        Assert.False(Directory.Exists(Path.Combine(fixture.Worktree, ".orchestrator")));

        var finding = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Worktree, fixture.ProjectHome);
        var coverage = StructuralCoverageFailureMessage.Build(fixture.Worktree, fixture.ProjectHome,
            ["tests/Alpha.Tests/Alpha.Tests.csproj"]);
        var manifestPath = Path.GetFullPath(fixture.ProjectManifest);

        Assert.True(finding.HasViolation);
        Assert.Equal(manifestPath + ": Acceptance manifest engine maxConcurrentShards must be at least 1.", finding.Message);
        Assert.Contains(manifestPath, finding.Message);
        Assert.Contains(manifestPath, coverage);
        Assert.DoesNotContain("config/acceptance-manifest.json", finding.Message);
        Assert.DoesNotContain("config/acceptance-manifest.json", coverage);
    }

    [Fact]
    public void HomeManifest_PreservesExactPreflightAndStructuralCoverageText()
    {
        using var fixture = new ManifestFixture();
        ManifestFixture.WriteInvalidManifest(fixture.WorktreeManifest);
        Assert.False(Directory.Exists(fixture.ProjectHome));

        var finding = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Worktree);
        var coverage = StructuralCoverageFailureMessage.Build(fixture.Worktree, null,
            ["tests/Alpha.Tests/Alpha.Tests.csproj", "tests/Beta.Tests/Beta.Tests.csproj"]);

        Assert.True(finding.HasViolation);
        Assert.Equal("config/acceptance-manifest.json: Acceptance manifest engine maxConcurrentShards must be at least 1.", finding.Message);
        Assert.StartsWith("config/acceptance-manifest.json: ", finding.Message, StringComparison.Ordinal);
        Assert.Equal("Structural coverage requires every discovered test project to be declared by a dotnet-test check in config/acceptance-manifest.json:" +
            Environment.NewLine + "tests/Alpha.Tests/Alpha.Tests.csproj" +
            Environment.NewLine + "tests/Beta.Tests/Beta.Tests.csproj", coverage);
    }

    private sealed class ManifestFixture : IDisposable
    {
        private string Root { get; } = SharedTestSupport.CreateTempDirectory();
        public string Worktree => Path.Combine(Root, "worktree");
        public string ProjectHome => Path.Combine(Root, "project state");
        public string WorktreeManifest => Path.Combine(Worktree, "config", "acceptance-manifest.json");
        public string ProjectManifest => Path.Combine(ProjectHome, "acceptance-manifest.json");

        public ManifestFixture() => Directory.CreateDirectory(Worktree);

        public static void WriteInvalidManifest(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"engine\":{\"maxConcurrentShards\":0}}");
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
