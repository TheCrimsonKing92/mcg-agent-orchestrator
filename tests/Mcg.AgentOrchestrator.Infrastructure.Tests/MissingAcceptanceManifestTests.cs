using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns its tree; check execution is injected and no ambient state is changed.
public sealed class MissingAcceptanceManifestTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MissingManifest_BothLoadForms_ThrowNamedFailure(bool knownChanges, bool projectHome)
    {
        using var fixture = new ForeignFixture();
        var home = projectHome ? fixture.ProjectHome : null;
        var failure = Assert.Throws<MissingAcceptanceManifestException>(() =>
            GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(
                fixture.Worktree, knownChanges ? [ForeignFixture.SourcePath] : null, home));

        Assert.Equal(fixture.ExpectedLocations(home), failure.SearchedLocations);
        AssertFailureMessage(fixture, home, failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingManifest_StartupContract_ReportsNamedFailure(bool projectHome)
    {
        using var fixture = new ForeignFixture();
        var home = projectHome ? fixture.ProjectHome : null;
        var failure = Assert.Throws<MissingAcceptanceManifestException>(() =>
            GoalAcceptanceVerifier.ValidateStartupContract(fixture.Worktree, home));

        AssertFailureMessage(fixture, home, failure.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MissingManifest_Gate_FailsWithoutExecutingChecks(bool knownChanges, bool projectHome)
    {
        using var fixture = new ForeignFixture();
        var home = projectHome ? fixture.ProjectHome : null;
        var calls = new ConcurrentQueue<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Enqueue(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "unexpected check"));
        });

        var result = await verifier.RunAsync(fixture.Worktree,
            changedFiles: knownChanges ? [ForeignFixture.SourcePath] : null, projectHomeDirectory: home);

        Assert.False(result.Passed);
        Assert.False(result.Skipped);
        Assert.Equal(1, result.ExitCode);
        AssertFailureMessage(fixture, home, result.OutputTail!);
        var check = Assert.Single(result.Checks!);
        Assert.Equal("acceptance manifest", check.Name);
        Assert.False(check.Passed);
        Assert.Equal(result.OutputTail, check.OutputTail);
        Assert.Empty(calls);
    }

    private static void AssertFailureMessage(ForeignFixture fixture, string? home, string message)
    {
        Assert.Contains("Acceptance manifest is missing", message, StringComparison.Ordinal);
        foreach (var location in fixture.ExpectedLocations(home))
            Assert.Contains(location, message, StringComparison.Ordinal);
        Assert.Contains(@"%LOCALAPPDATA%\Mcg.AgentOrchestrator\projects\<project>\", message, StringComparison.Ordinal);
        Assert.Contains("run project onboarding", message, StringComparison.Ordinal);
    }

    private sealed class ForeignFixture : IDisposable
    {
        public const string SourcePath = "src/Foreign/Widget.cs";
        private string Root { get; } = SharedTestSupport.CreateTempDirectory();
        public string Worktree => Path.Combine(Root, "worktree");
        public string ProjectHome => Path.Combine(Root, "project-home");

        public ForeignFixture()
        {
            Directory.CreateDirectory(Path.Combine(Worktree, "src", "Foreign"));
            Directory.CreateDirectory(ProjectHome);
            File.WriteAllText(Path.Combine(Worktree, ".git"), "gitdir: isolated-fixture");
            File.WriteAllText(Path.Combine(Worktree, "Foreign.sln"), "");
            File.WriteAllText(Path.Combine(Worktree, SourcePath), "namespace Foreign; public class Widget { }");
        }

        public IReadOnlyList<string> ExpectedLocations(string? home) => home is null
            ? [Path.Combine(Worktree, "config", "acceptance-manifest.json"),
               Path.Combine(Worktree, ".orchestrator", "acceptance-manifest.json")]
            : [Path.Combine(home, "acceptance-manifest.json"),
               Path.Combine(Worktree, "config", "acceptance-manifest.json"),
               Path.Combine(Worktree, ".orchestrator", "acceptance-manifest.json")];

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
