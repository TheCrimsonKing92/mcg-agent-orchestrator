using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: all facts and fixture copies belong to the individual test.
public sealed class IntegrationBranchComparerTests
{
    [Theory]
    [InlineData(FactConfidence.High)]
    [InlineData(FactConfidence.Medium)]
    public void Compare_EqualBranch_MatchesAndReportsConfidence(FactConfidence confidence)
    {
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        var learned = Learn(source, "trunk", confidence);

        var comparison = IntegrationBranchComparer.Compare(learned, "trunk");

        Assert.Equal(IntegrationBranchResultKind.Match, comparison.Kind);
        Assert.Equal("trunk", comparison.LearnedBranch);
        Assert.Equal("trunk", comparison.RegistryBranch);
        Assert.Contains(confidence.ToString(), comparison.Reason);
    }

    [Theory]
    [InlineData("Trunk")]
    [InlineData("master")]
    [InlineData("")]
    [InlineData(null)]
    public void Compare_DifferentRegistryBranch_CarriesBothValues(string? registryBranch)
    {
        using var source = new ProjectOnboardingFixture("answer-key-repository");

        var comparison = IntegrationBranchComparer.Compare(Learn(source, "trunk", FactConfidence.High), registryBranch);

        Assert.Equal(IntegrationBranchResultKind.BranchDiffers, comparison.Kind);
        Assert.Equal("trunk", comparison.LearnedBranch);
        Assert.Equal(registryBranch, comparison.RegistryBranch);
        Assert.Contains("trunk", comparison.Reason);
        if (!string.IsNullOrEmpty(registryBranch)) Assert.Contains(registryBranch, comparison.Reason);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData(null)]
    [InlineData("")]
    public void Compare_UnlearnedBranch_RemainsUnresolved(string? registryBranch)
    {
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        var learned = IntegrationBranchReader.Read(source.Root);

        var comparison = IntegrationBranchComparer.Compare(learned, registryBranch);

        Assert.Equal(IntegrationBranchResultKind.Unresolved, comparison.Kind);
        Assert.Null(comparison.LearnedBranch);
        Assert.Equal(registryBranch, comparison.RegistryBranch);
        Assert.Contains("integration-branch", comparison.Reason);
        Assert.Contains(learned.Reason, comparison.Reason);
    }

    private static LearnedIntegrationBranch Learn(ProjectOnboardingFixture source, string branch, FactConfidence confidence)
    {
        var remote = confidence == FactConfidence.High;
        var path = Path.Combine(source.Root, "." + "git", remote ? "refs/remotes/origin/HEAD" : "HEAD");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, (remote ? "ref: refs/remotes/origin/" : "ref: refs/heads/") + branch);
        return IntegrationBranchReader.Read(source.Root);
    }
}
