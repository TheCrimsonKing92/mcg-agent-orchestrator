using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns an isolated fixture copy and its metadata files.
public sealed class IntegrationBranchReaderTests
{
    [Fact]
    public void Read_RemoteDefault_TakesPrecedenceOverCheckedOutBranch()
    {
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        WriteReference(source, "HEAD", "ref: refs/heads/feature/x");
        WriteReference(source, "refs/remotes/origin/HEAD", "ref: refs/remotes/origin/trunk");

        var learned = IntegrationBranchReader.Read(source.Root);

        Assert.NotNull(learned.Branch);
        Assert.Equal("trunk", learned.Branch.Value);
        Assert.Equal(FactConfidence.High, learned.Branch.Confidence);
        Assert.Equal(".git/refs/remotes/origin/HEAD", learned.Branch.Source.Path);
        Assert.Equal(1, learned.Branch.Source.Line);
        Assert.Null(learned.OwnerQuestionFactKey);
        Assert.Equal(IntegrationBranchResultKind.Match,
            IntegrationBranchComparer.Compare(learned, "trunk").Kind);
        var differs = IntegrationBranchComparer.Compare(learned, "master");
        Assert.Equal(IntegrationBranchResultKind.BranchDiffers, differs.Kind);
        Assert.Equal("trunk", differs.LearnedBranch);
        Assert.Equal("master", differs.RegistryBranch);
    }

    [Fact]
    public void Read_OnlyLocalHead_LearnsMediumConfidenceCaseSensitiveHint()
    {
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        WriteReference(source, "HEAD", "ref: refs/heads/develop");

        var learned = IntegrationBranchReader.Read(source.Root);

        Assert.NotNull(learned.Branch);
        Assert.Equal("develop", learned.Branch.Value);
        Assert.Equal(FactConfidence.Medium, learned.Branch.Confidence);
        Assert.Equal(".git/HEAD", learned.Branch.Source.Path);
        Assert.Equal(1, learned.Branch.Source.Line);
        Assert.Equal(IntegrationBranchResultKind.BranchDiffers,
            IntegrationBranchComparer.Compare(learned, "Develop").Kind);
        var match = IntegrationBranchComparer.Compare(learned, "develop");
        Assert.Equal(IntegrationBranchResultKind.Match, match.Kind);
        Assert.Contains("Medium", match.Reason);
    }

    [Theory]
    [InlineData("detached", "detached")]
    [InlineData("missing", "directory is missing")]
    [InlineData("pointer", "pointer file")]
    [InlineData("malformed-remote", "remote default reference is malformed")]
    [InlineData("missing-head", "HEAD reference is missing")]
    [InlineData("malformed-head", "HEAD reference is malformed")]
    public void Read_NoUsableReference_LeavesOwnerQuestion(string scenario, string reason)
    {
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        switch (scenario)
        {
            case "detached": WriteReference(source, "HEAD", new string('a', 40)); break;
            case "pointer":
                File.WriteAllText(Path.Combine(source.Root, "." + "git"), "gitdir: elsewhere");
                break;
            case "malformed-remote":
                WriteReference(source, "HEAD", "ref: refs/heads/master");
                WriteReference(source, "refs/remotes/origin/HEAD", "garbage");
                break;
            case "missing-head": Directory.CreateDirectory(Path.Combine(source.Root, "." + "git")); break;
            case "malformed-head": WriteReference(source, "HEAD", "garbage"); break;
        }

        var learned = IntegrationBranchReader.Read(source.Root);

        Assert.Null(learned.Branch);
        Assert.Equal("integration-branch", learned.OwnerQuestionFactKey);
        Assert.Contains(reason, learned.Reason);
        foreach (var registryBranch in new[] { "master", "main" })
        {
            var comparison = IntegrationBranchComparer.Compare(learned, registryBranch);
            Assert.Equal(IntegrationBranchResultKind.Unresolved, comparison.Kind);
            Assert.Null(comparison.LearnedBranch);
            Assert.Contains("integration-branch", comparison.Reason);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_SymbolicReference_PreservesCaseAndSlashes(bool remote)
    {
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        var path = remote ? "refs/remotes/origin/HEAD" : "HEAD";
        var prefix = remote ? "ref: refs/remotes/origin/" : "ref: refs/heads/";
        WriteReference(source, path, "\uFEFF" + prefix + "Release/Team/X  \r\nignored");

        var learned = IntegrationBranchReader.Read(source.Root);

        Assert.NotNull(learned.Branch);
        Assert.Equal("Release/Team/X", learned.Branch.Value);
        Assert.Equal(remote ? FactConfidence.High : FactConfidence.Medium, learned.Branch.Confidence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ref: refs/remotes/origin/")]
    [InlineData("ref: refs/remotes/origin/feature x")]
    [InlineData("ref: refs/remotes/origin/feature/")]
    [InlineData("ref: refs/remotes/origin/feature..x")]
    [InlineData("ref: refs/remotes/origin/feature:x")]
    [InlineData("ref: refs/remotes/origin/feature/.hidden")]
    [InlineData("ref: refs/remotes/origin/feature.lock")]
    [InlineData("ref: refs/remotes/other/trunk")]
    public void Read_MalformedRemote_DoesNotFallThroughToValidHead(string reference)
    {
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        WriteReference(source, "HEAD", "ref: refs/heads/trunk");
        WriteReference(source, "refs/remotes/origin/HEAD", reference);

        var learned = IntegrationBranchReader.Read(source.Root);

        Assert.Null(learned.Branch);
        Assert.Contains("remote default reference is malformed", learned.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_LockedReference_ReportsUnreadableWithoutFallback(bool remote)
    {
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        WriteReference(source, "HEAD", "ref: refs/heads/trunk");
        var path = remote ? "refs/remotes/origin/HEAD" : "HEAD";
        if (remote) WriteReference(source, path, "ref: refs/remotes/origin/trunk");
        using var locked = new FileStream(Path.Combine(source.Root, "." + "git", path),
            FileMode.Open, FileAccess.Read, FileShare.None);

        var learned = IntegrationBranchReader.Read(source.Root);

        Assert.Null(learned.Branch);
        Assert.Equal("integration-branch", learned.OwnerQuestionFactKey);
        Assert.Contains("unreadable", learned.Reason);
    }

    private static void WriteReference(ProjectOnboardingFixture source, string relative, string text)
    {
        var path = Path.Combine(source.Root, "." + "git", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
