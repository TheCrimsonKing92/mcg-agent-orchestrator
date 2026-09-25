using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OwnerProtectedConfigurationGateTests : IDisposable
{
    private const string Trusted = """{"engine":{"enforceStructuralCoverage":true,"partitionVerdictFullRerunEveryN":5,"mtpInvocations":[]},"checks":[{"command":"old"}]}""";
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Xunit.Fact]
    public void ManifestChecksEditRequiresOperatorReviewWithPath()
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), Trusted.Replace("old", "new"));

        var result = GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            _root, ["config/acceptance-manifest.json"], (_, _) => Trusted);

        Assert.NotNull(result);
        Assert.Equal("operator review required", result.ResultSummary);
        Assert.Contains("config/acceptance-manifest.json", result.OutputTail, StringComparison.Ordinal);
        Assert.Contains("checks[0].command", result.OutputTail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void AddedTrackedConductorPolicyRequiresOperatorReviewWithPath()
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "conductor-policy.json"), """{"autonomy":{"maxRetries":3}}""");

        var result = GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            _root, ["config/conductor-policy.json"], (_, _) => null);

        Assert.NotNull(result);
        Assert.Equal("operator review required", result.ResultSummary);
        Assert.Contains("config/conductor-policy.json", result.OutputTail, StringComparison.Ordinal);
        Assert.Contains("autonomy.maxRetries", result.OutputTail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RemovedManifestRequiresOperatorReview()
    {
        var result = GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            _root, ["config/acceptance-manifest.json"], (_, _) => Trusted);

        Assert.NotNull(result);
        Assert.Equal("operator review required", result.ResultSummary);
        Assert.Contains("config/acceptance-manifest.json: changed field(s) $ (removed or renamed)",
            result.OutputTail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void EngineFieldEditKeepsExistingResultAndMessage()
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"),
            Trusted.Replace("\"enforceStructuralCoverage\":true", "\"enforceStructuralCoverage\":false"));

        var result = GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            _root, ["config/acceptance-manifest.json"], (_, _) => Trusted);

        Assert.NotNull(result);
        Assert.Equal("acceptance manifest trusted dimensions", result.Name);
        Assert.Equal("operator review required", result.ResultSummary);
        Assert.Equal("trusted review required for changed field(s): engine.enforceStructuralCoverage", result.OutputTail);
    }

    [Xunit.Fact]
    public void MixedEngineChecksAndPolicyEditNamesEveryChangedPath()
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"),
            Trusted.Replace("\"enforceStructuralCoverage\":true", "\"enforceStructuralCoverage\":false")
                .Replace("old", "new"));
        File.WriteAllText(Path.Combine(config, "conductor-policy.json"), """{"autonomy":{"maxRetries":4}}""");

        var result = GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            _root, ["config/acceptance-manifest.json", "config/conductor-policy.json"],
            (_, args) => args[1] == "main:config/acceptance-manifest.json"
                ? Trusted : """{"autonomy":{"maxRetries":3}}""");

        Assert.Equal("operator review required", Assert.IsType<AcceptanceCheckResult>(result).ResultSummary);
        Assert.Contains("config/acceptance-manifest.json", result.OutputTail, StringComparison.Ordinal);
        Assert.Contains("engine.enforceStructuralCoverage", result.OutputTail, StringComparison.Ordinal);
        Assert.Contains("checks[0].command", result.OutputTail, StringComparison.Ordinal);
        Assert.Contains("config/conductor-policy.json", result.OutputTail, StringComparison.Ordinal);
        Assert.Contains("autonomy.maxRetries", result.OutputTail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PolicyEditAndUnavailableTrustedManifestNameBothFiles()
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), Trusted);
        File.WriteAllText(Path.Combine(config, "conductor-policy.json"), """{"autonomy":{"maxRetries":4}}""");

        var result = GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            _root, ["config/acceptance-manifest.json", "config/conductor-policy.json"],
            (_, _) => null);

        Assert.Equal("operator review required", Assert.IsType<AcceptanceCheckResult>(result).ResultSummary);
        Assert.Contains("config/acceptance-manifest.json", result.OutputTail, StringComparison.Ordinal);
        Assert.Contains("config/conductor-policy.json", result.OutputTail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DeletedConductorPolicyRequiresOperatorReview()
    {
        var result = GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            _root, ["config/conductor-policy.json"],
            (_, _) => """{"autonomy":{"maxRetries":3}}""");

        Assert.Equal("operator review required", Assert.IsType<AcceptanceCheckResult>(result).ResultSummary);
        Assert.Contains("config/conductor-policy.json", result.OutputTail, StringComparison.Ordinal);
        Assert.Contains("autonomy.maxRetries", result.OutputTail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RenamedAwayConductorPolicyRequiresOperatorReview()
    {
        var result = GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            _root, ["config/new-policy.json"], (_, args) => args[0] switch
            {
                "diff" => "config/conductor-policy.json\nconfig/new-policy.json",
                "show" when args[1] == "main:config/conductor-policy.json" =>
                    """{"autonomy":{"maxRetries":3}}""",
                _ => null
            });

        Assert.Equal("operator review required", Assert.IsType<AcceptanceCheckResult>(result).ResultSummary);
        Assert.Contains("config/conductor-policy.json", result.OutputTail, StringComparison.Ordinal);
        Assert.Contains("autonomy.maxRetries", result.OutputTail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void AddedManifestWithoutTrustedMainRequiresOperatorReview()
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), Trusted);

        var result = GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            _root, ["config/acceptance-manifest.json"], (_, _) => null);

        Assert.Equal("operator review required", Assert.IsType<AcceptanceCheckResult>(result).ResultSummary);
        Assert.Contains("config/acceptance-manifest.json", result.OutputTail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void UnparseableCandidateManifestRequiresOperatorReview()
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), "{invalid");

        var result = GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            _root, ["config/acceptance-manifest.json"], (_, _) => Trusted);

        Assert.Equal("operator review required", Assert.IsType<AcceptanceCheckResult>(result).ResultSummary);
        Assert.Contains("config/acceptance-manifest.json", result.OutputTail, StringComparison.Ordinal);
        Assert.Contains("unparseable JSON", result.OutputTail, StringComparison.Ordinal);
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
