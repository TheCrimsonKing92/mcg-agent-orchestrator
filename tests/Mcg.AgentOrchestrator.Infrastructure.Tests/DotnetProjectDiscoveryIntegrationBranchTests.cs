using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns an isolated fixture copy; no tools are executed.
public sealed class DotnetProjectDiscoveryIntegrationBranchTests
{
    [Fact]
    public void Discover_RemoteDefault_RecordsTrunkInsteadOfCheckedOutBranch()
    {
        using var fixture = new ProjectOnboardingFixture("answer-key-repository");
        WriteReference(fixture, "HEAD", "ref: refs/heads/feature/x");
        WriteReference(fixture, "refs/remotes/origin/HEAD", "ref: refs/remotes/origin/trunk");
        var learned = IntegrationBranchReader.Read(fixture.Root);

        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.NotNull(model.IntegrationBranch);
        Assert.Equal("trunk", model.IntegrationBranch.Value);
        Assert.Equal(FactConfidence.High, model.IntegrationBranch.Confidence);
        Assert.Equal(learned.Branch!.Source, model.IntegrationBranch.Source);
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey == LearnedIntegrationBranch.OwnerQuestionKey);
    }

    [Fact]
    public void Discover_OnlyLocalHead_RecordsDevelopWithMediumConfidence()
    {
        using var fixture = new ProjectOnboardingFixture("answer-key-repository");
        WriteReference(fixture, "HEAD", "ref: refs/heads/develop");

        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.NotNull(model.IntegrationBranch);
        Assert.Equal("develop", model.IntegrationBranch.Value);
        Assert.Equal(FactConfidence.Medium, model.IntegrationBranch.Confidence);
        Assert.Equal(new FactSource(".git/HEAD", 1), model.IntegrationBranch.Source);
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey == LearnedIntegrationBranch.OwnerQuestionKey);
    }

    [Theory]
    [InlineData("detached")]
    [InlineData("missing")]
    [InlineData("pointer")]
    public void Discover_UnlearnedBranch_RemainsNullWithoutOwnerQuestion(string scenario)
    {
        using var fixture = new ProjectOnboardingFixture("answer-key-repository");
        switch (scenario)
        {
            case "detached": WriteReference(fixture, "HEAD", new string('a', 40)); break;
            case "missing": break;
            case "pointer":
                File.WriteAllText(Path.Combine(fixture.Root, "." + "git"), "gitdir: elsewhere");
                break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.Null(model.IntegrationBranch);
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey == LearnedIntegrationBranch.OwnerQuestionKey);
    }

    private static void WriteReference(ProjectOnboardingFixture fixture, string relativePath, string text)
    {
        var path = Path.Combine(fixture.Root, "." + "git", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
