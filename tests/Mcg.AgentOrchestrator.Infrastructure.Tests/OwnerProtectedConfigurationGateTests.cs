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

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
