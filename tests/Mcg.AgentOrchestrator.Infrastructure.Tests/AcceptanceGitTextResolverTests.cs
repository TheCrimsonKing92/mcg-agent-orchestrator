using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceGitTextResolverTests : IDisposable
{
    private const string ManifestJson =
        """
        {
          "engine": {
            "enforceStructuralCoverage": true,
            "partitionVerdictFullRerunEveryN": 5,
            "mtpInvocations": []
          }
        }
        """;

    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Fact]
    public void Resolve_SuccessReturnsRawUntrimmedStdout()
    {
        var result = AcceptanceGitTextResolver.Resolve(_root, "--version");

        Assert.NotNull(result);
        Assert.Contains("git version", result, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(result.Trim(), result);
    }

    [Fact]
    public void Resolve_NonZeroExitReturnsNull()
    {
        var result = AcceptanceGitTextResolver.Resolve(_root, "rev-parse", "--verify", "HEAD");

        Assert.Null(result);
    }

    [Fact]
    public void GoalAcceptanceVerifierResolveGitText_DelegatesWithoutChangingTheResult()
    {
        var expected = AcceptanceGitTextResolver.Resolve(_root, "--version");

        var actual = GoalAcceptanceVerifier.ResolveGitText(_root, "--version");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GitCallSites_PassExactArgumentVectorsToFake()
    {
        var calls = new List<(string WorktreePath, string[] Arguments)>();
        string? Resolve(string worktreePath, string[] arguments)
        {
            calls.Add((worktreePath, [.. arguments]));
            return arguments[0] switch
            {
                "worktree" => $"worktree {_root}\nHEAD abcdef\nbranch refs/heads/main\n",
                "diff" => "D\ttests/Sample/RemovedTests.cs\n",
                "show" => null,
                _ => throw new InvalidOperationException($"Unexpected git command '{arguments[0]}'.")
            };
        }

        var mainWorktree = GoalAcceptanceVerifier.ResolveMainWorktreePathWithGitForTests(_root, Resolve);
        var deletedFiles = GoalAcceptanceVerifier.ResolveDeletedTestFilesWithGitForTests(
            _root,
            "tests/Sample/Sample.csproj",
            Resolve);
        var manifestTrust = GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            _root,
            ["config/acceptance-manifest.json"],
            Resolve);

        Assert.Equal(_root, mainWorktree);
        Assert.Equal(["tests/Sample/RemovedTests.cs"], deletedFiles);
        Assert.NotNull(manifestTrust);
        Assert.Equal("trusted manifest comparison unavailable", manifestTrust.ResultSummary);
        Assert.Collection(
            calls,
            call =>
            {
                Assert.Equal(_root, call.WorktreePath);
                Assert.Equal(["worktree", "list", "--porcelain"], call.Arguments);
            },
            call =>
            {
                Assert.Equal(_root, call.WorktreePath);
                Assert.Equal(["diff", "--name-status", "main...HEAD", "--"], call.Arguments);
            },
            call =>
            {
                Assert.Equal(_root, call.WorktreePath);
                Assert.Equal(["show", "main:config/acceptance-manifest.json"], call.Arguments);
            });
    }

    [Fact]
    public void GitCallSites_TreatBlankFakeOutputAsFailure()
    {
        static string? Resolve(string worktreePath, string[] arguments) => "   ";

        Assert.Null(GoalAcceptanceVerifier.ResolveMainWorktreePathWithGitForTests(_root, Resolve));
        Assert.Empty(GoalAcceptanceVerifier.ResolveDeletedTestFilesWithGitForTests(
            _root,
            "tests/Sample/Sample.csproj",
            Resolve));
        var manifestTrust = GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            _root,
            ["config/acceptance-manifest.json"],
            Resolve);
        Assert.NotNull(manifestTrust);
        Assert.Equal("trusted manifest comparison unavailable", manifestTrust.ResultSummary);
    }

    [Fact]
    public void ManifestTrustCallSite_CanUseMatchingFakeGitOutput()
    {
        var configDirectory = Path.Combine(_root, "config");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, "acceptance-manifest.json"), ManifestJson);

        var result = GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            _root,
            ["config/acceptance-manifest.json"],
            (worktreePath, arguments) => ManifestJson);

        Assert.Null(result);
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
