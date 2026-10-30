using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: repositories and injected git runners belong to each test.
public sealed class GoTestPackageDiffEvidenceTests
{
    [Fact]
    public void CommittedRange_IncludesBothMoveEndpointsAndDeletedTests()
    {
        using var repo = new TestClassRenameEvidenceTests.Repository();
        Write(repo.Root, "old/run_test.go");
        Write(repo.Root, "deleted/run_test.go");
        TestClassRenameEvidenceTests.Commit(repo.Root, "Baseline Go tests", "old", "deleted");
        TestClassRenameEvidenceTests.Git(repo.Root, "branch", "-f", "main", "HEAD");
        TestClassRenameEvidenceTests.Git(repo.Root, "mv", "old", "moved");
        File.Delete(Path.Combine(repo.Root, "deleted/run_test.go"));
        Write(repo.Root, "added/nested/run_test.go");
        Write(repo.Root, "root_test.go");
        Write(repo.Root, "ignored/source.go");
        // git mv already staged the old path's deletion; it is no longer a valid add pathspec.
        TestClassRenameEvidenceTests.Commit(repo.Root, "Change Go tests", "moved", "deleted", "added", "root_test.go", "ignored");
        var candidate = TestClassRenameEvidenceTests.Git(repo.Root, "rev-parse", "HEAD");
        Write(repo.Root, "uncommitted/extra_test.go");

        var directories = GoTestPackageDiffEvidence.Read(repo.Root, "main", candidate);

        Assert.Equal(new[] { "", "added/nested", "deleted", "moved", "old" }, directories.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("merge-base")]
    [InlineData("diff")]
    [InlineData("drain")]
    [InlineData("exception")]
    public void UnavailableGit_ReturnsEmptyEvidence(string failure)
    {
        var reached = false;
        var directories = GoTestPackageDiffEvidence.Read(Path.GetTempPath(), "main", "candidate", (_, args) =>
        {
            if (args[0] == "merge-base" && failure != "merge-base") return new(0, "base\n", "");
            reached = true;
            if (failure == "exception") throw new IOException("unavailable");
            return new(failure == "drain" ? 0 : 1, "a/run_test.go\0", "unavailable", DrainTimedOut: failure == "drain");
        });

        Assert.True(reached);
        Assert.Empty(directories);
    }

    [Fact]
    public void NonRepository_ReturnsEmptyEvidence()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        Assert.Empty(GoTestPackageDiffEvidence.Read(fixture.Root, "main", new string('a', 40)));
    }

    [Fact]
    public void Cancellation_IsPropagated()
    {
        Assert.Throws<OperationCanceledException>(() => GoTestPackageDiffEvidence.Read(
            Path.GetTempPath(), "main", "candidate", (_, _) => throw new OperationCanceledException()));
    }

    [Theory]
    [InlineData("a\\nested\\..\\", "a")]
    [InlineData("./a//./", "a")]
    [InlineData(".", "")]
    [InlineData("A", "A")]
    [InlineData("../outside", null)]
    [InlineData("", null)]
    public void DirectoryIdentity_NormalizesSegmentsAndPreservesCase(string path, string? expected)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        Assert.Equal(expected, GoTestPackageDiffEvidence.NormalizeDirectory(fixture.Root, path));
        if (expected is not null)
            Assert.Equal(expected, GoTestPackageDiffEvidence.NormalizeDirectory(fixture.Root, Path.Combine(fixture.Root, path)));
    }

    private static void Write(string root, string path) => TestClassRenameEvidenceTests.Write(root, path, "package fixture\n");
}
