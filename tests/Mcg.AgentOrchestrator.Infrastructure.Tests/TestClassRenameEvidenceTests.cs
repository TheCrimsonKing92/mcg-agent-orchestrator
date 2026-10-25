using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns its repository; no shared GitRunner or process state is changed.
public sealed class TestClassRenameEvidenceTests
{
    [Fact]
    public void GoalOnlyFile_UsesRenameCommitEvidence()
    {
        using var repo = new Repository();
        Rename(repo.Root, OldPath, NewPath, "NewNameTests");
        var candidate = Git(repo.Root, "rev-parse", "HEAD");

        var rename = Assert.Single(TestClassRenameEvidence.Read(repo.Root, "main", candidate));

        Assert.Equal("OldNameTests", rename.OldClass);
        Assert.Equal("NewNameTests", rename.NewClass);
        Assert.Equal(OldPath, rename.OldPath);
        Assert.Equal(NewPath, rename.NewPath);
        Assert.InRange(rename.Similarity, 50, 100);
        Assert.Equal("commit " + candidate, rename.Evidence);
    }

    [Fact]
    public void MainFile_PrefersTreeEvidenceWithoutDuplicates()
    {
        using var repo = new Repository(oldOnMain: true);
        var mergeBase = Git(repo.Root, "rev-parse", "main");
        Rename(repo.Root, OldPath, NewPath, "NewNameTests");
        var candidate = Git(repo.Root, "rev-parse", "HEAD");

        var rename = Assert.Single(TestClassRenameEvidence.Read(repo.Root, "main", candidate));

        Assert.Equal("OldNameTests", rename.OldClass);
        Assert.Equal("NewNameTests", rename.NewClass);
        Assert.Equal($"tree {mergeBase}..{candidate}", rename.Evidence);
    }

    [Fact]
    public void Chain_FollowsEveryFileLinkToCandidateClass()
    {
        using var repo = new Repository();
        const string middlePath = "tests/Fixture/MiddleNameTests.cs";
        Rename(repo.Root, OldPath, middlePath, "MiddleNameTests");
        var firstRename = Git(repo.Root, "rev-parse", "HEAD");
        Rename(repo.Root, middlePath, NewPath, "NewNameTests");

        var renames = TestClassRenameEvidence.Read(repo.Root, "main", Git(repo.Root, "rev-parse", "HEAD"));

        var rename = Assert.Single(renames.Where(item => item.OldClass == "OldNameTests"));
        Assert.Equal("NewNameTests", rename.NewClass);
        Assert.Equal(OldPath, rename.OldPath);
        Assert.Equal(NewPath, rename.NewPath);
        Assert.Equal("commit " + firstRename, rename.Evidence);
    }

    [Fact]
    public void ReusedIntermediatePath_CannotFollowAnEarlierRename()
    {
        using var repo = new Repository();
        const string middlePath = "tests/Fixture/MiddleNameTests.cs";
        Write(repo.Root, middlePath, Source("MiddleNameTests"));
        Commit(repo.Root, "Add independent middle class", middlePath);
        Rename(repo.Root, middlePath, NewPath, "NewNameTests");
        Rename(repo.Root, OldPath, middlePath, "MiddleNameTests");
        File.Delete(Path.Combine(repo.Root, middlePath));
        Commit(repo.Root, "Remove reused path without a rename", middlePath);

        var renames = TestClassRenameEvidence.Read(repo.Root, "main", Git(repo.Root, "rev-parse", "HEAD"));

        Assert.DoesNotContain(renames, rename => rename.OldClass == "OldNameTests");
        Assert.Contains(renames, rename => rename.OldClass == "MiddleNameTests" && rename.NewClass == "NewNameTests");
    }

    [Fact]
    public void NonRepository_ReturnsNoEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-rename-nonrepo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Assert.Empty(TestClassRenameEvidence.Read(root, "main", new string('a', 40))); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("unrelated")]
    [InlineData("unchanged-class")]
    [InlineData("two-new-classes")]
    [InlineData("old-path-restored")]
    public void UnprovenClassIdentity_ReturnsNoEvidence(string scenario)
    {
        using var repo = new Repository();
        if (scenario == "unrelated") ReplaceWithUnrelated(repo.Root);
        else
        {
            Rename(repo.Root, OldPath, NewPath, scenario == "unchanged-class" ? "OldNameTests" : "NewNameTests",
                scenario == "two-new-classes" ? "\npublic sealed class ExtraHelper { }\n" : "");
            if (scenario == "old-path-restored")
            {
                Write(repo.Root, OldPath, Source("OldNameTests"));
                Commit(repo.Root, "Restore old path", OldPath);
            }
            // These controls must reach class/presence validation, rather than miss git's threshold.
            Assert.Contains("R", Git(repo.Root, "log", "--find-renames", "--diff-filter=R", "--name-status", "--format=", "main..HEAD"));
        }

        Assert.Empty(TestClassRenameEvidence.Read(repo.Root, "main", Git(repo.Root, "rev-parse", "HEAD")));
    }

    [Fact]
    public void SyntaxError_InvalidatesAllEvidence()
    {
        using var repo = new Repository();
        Rename(repo.Root, OldPath, NewPath, "NewNameTests", "\nclass Broken {\n");
        Assert.Contains("R", Git(repo.Root, "log", "--find-renames", "--diff-filter=R", "--name-status", "--format=", "main..HEAD"));

        Assert.Empty(TestClassRenameEvidence.Read(repo.Root, "main", Git(repo.Root, "rev-parse", "HEAD")));
    }

    [Fact]
    public void GitFailure_DiscardsPreviouslyReadEvidence()
    {
        using var repo = new Repository();
        Rename(repo.Root, OldPath, NewPath, "NewNameTests");
        var showReached = false;
        var renames = TestClassRenameEvidence.Read(repo.Root, "main", Git(repo.Root, "rev-parse", "HEAD"),
            (directory, args) =>
            {
                if (args[0] != "show") return GitCli.Run(directory, args);
                showReached = true;
                return new GitCli.GitResult(1, "", "unavailable blob");
            });

        Assert.True(showReached);
        Assert.Empty(renames);
    }

    internal const string OldPath = "tests/Fixture/OldNameTests.cs";
    internal const string NewPath = "tests/Fixture/NewNameTests.cs";
    internal const string OtherPath = "tests/Fixture/OtherWorkTests.cs";

    internal static string Source(string className) => $$"""
        public sealed class {{className}}
        {
            [Fact] public void Check() { }
            [Fact] public void Second() { }
        }
        """ + "\n";

    internal static void Write(string root, string path, string content)
    {
        var fullPath = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    internal static void Commit(string root, string message, params string[] paths)
    {
        Git(root, ["add", "--", .. paths]);
        Git(root, "commit", "-m", message);
    }

    internal static void AddOld(string root)
    {
        Write(root, OldPath, Source("OldNameTests"));
        Commit(root, "Add original tests", OldPath);
    }

    internal static void Rename(string root, string oldPath, string newPath, string className, string extra = "")
    {
        Git(root, "mv", oldPath, newPath);
        Write(root, newPath, Source(className) + extra);
        Commit(root, "Rename tests", newPath);
    }

    internal static void ReplaceWithUnrelated(string root)
    {
        File.Delete(Path.Combine(root, OldPath));
        Write(root, OtherPath, """
            namespace UnrelatedFeature;
            public sealed class OtherWorkTests {
                private readonly int[] values = [4, 8, 16, 32];
                [Fact] public void Check() => Assert.Equal(60, values.Sum());
                [Fact] public void ReadsFirstValue() => Assert.Equal(4, values[0]);
                [Fact] public void ReadsLastValue() => Assert.Equal(32, values[^1]);
                [Fact] public void CountsEntries() => Assert.Equal(4, values.Length);
                [Fact] public void RejectsZero() => Assert.DoesNotContain(0, values);
            }
            """);
        Commit(root, "Replace with unrelated work", OldPath, OtherPath);
    }

    internal static string Git(string root, params string[] args)
    {
        var result = GitCli.Run(root, args);
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result.Error}");
        return result.Output.Trim();
    }

    internal sealed class Repository : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-rename-evidence", Guid.NewGuid().ToString("N"));

        internal Repository(bool oldOnMain = false)
        {
            Directory.CreateDirectory(Root);
            Git(Root, "init", "-b", "main");
            Git(Root, "config", "user.name", "Rename Evidence Test");
            Git(Root, "config", "user.email", "rename@example.invalid");
            Write(Root, "README.md", "Rename fixture\n");
            if (oldOnMain) Write(Root, OldPath, Source("OldNameTests"));
            Commit(Root, "Main baseline", oldOnMain ? ["README.md", OldPath] : ["README.md"]);
            Git(Root, "checkout", "-b", "goal");
            if (!oldOnMain) AddOld(Root);
        }

        public void Dispose() => LandingExecutorTests.TryDeleteDirectory(Root);
    }
}
