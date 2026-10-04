using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class ReviewerChangedExistingTestReaderTests : WorkerDispatchTestSupport
{
    private const string PathInRepo = "tests/Feature.Tests/T.cs";
    private static readonly DateTimeOffset CommitTime = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadsEditedAndRemovedMethodsButIgnoresTriviaAndNewMethods()
    {
        var root = CreateSeededDispatchRepository();
        var file = Path.Combine(root, PathInRepo);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """
            using Xunit;
            public class T
            {
                [Fact] public void Edited() { Assert.Equal(1, 1); }
                [Fact] public void Whitespace() { Assert.True(true); }
                [Fact] public void Removed() { Assert.True(true); }
            }
            """);
        Commit(root);
        var baseline = Head(root);
        File.WriteAllText(file, """
            using Xunit;
            public class T
            {
                [Fact]
                public void Edited()
                {
                    Assert.Equal(2, 2);
                }
                [Fact] public void Whitespace() { /* comment */ Assert.True( true ); }
                [Fact] public void Added() { Assert.True(true); }
            }
            """);
        Commit(root);
        var head = Head(root);
        var read = ReviewerChangedExistingTestReader.Read(root, baseline, head, [PathInRepo]);
        Assert.Null(read.Diagnostic);
        Assert.Equal(2, read.Entries.Count);
        var changed = Assert.Single(read.Entries.Where(entry => !entry.Removed));
        Assert.Equal("T", changed.TypeName);
        Assert.Equal("Edited", changed.MethodName);
        Assert.Equal(PathInRepo, changed.File);
        Assert.Equal(4, changed.StartLine);
        Assert.Equal(8, changed.EndLine);
        var removed = Assert.Single(read.Entries.Where(entry => entry.Removed));
        Assert.Equal("Removed", removed.MethodName);
        Assert.Equal(6, removed.StartLine);
        Assert.Equal(6, removed.EndLine);

        foreach (var missing in new string?[] { null, "missing-merge-base" })
        {
            var degraded = ReviewerChangedExistingTestReader.Read(root, missing, head, [PathInRepo]);
            Assert.Empty(degraded.Entries);
            Assert.Contains(missing is null ? "merge base is missing" : "missing-merge-base", degraded.Diagnostic!);
        }
        var missingHead = ReviewerChangedExistingTestReader.Read(root, baseline, null, [PathInRepo]);
        Assert.Empty(missingHead.Entries);
        Assert.Contains("candidate head is missing", missingHead.Diagnostic!);
    }

    [Fact]
    public void EntireDeletedFileReportsRemovedMethodsAndNewFileReportsNone()
    {
        var root = CreateSeededDispatchRepository();
        var file = Path.Combine(root, PathInRepo);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "class T { public void Gone() {} }");
        Commit(root);
        var baseline = Head(root);
        File.Delete(file);
        File.WriteAllText(Path.Combine(root, "tests/Feature.Tests/New.cs"), "class New { public void Added() {} }");
        Commit(root);
        var result = ReviewerChangedExistingTestReader.Read(root, baseline, Head(root), [PathInRepo, "tests/Feature.Tests/New.cs"]);
        Assert.Null(result.Diagnostic);
        var removed = Assert.Single(result.Entries);
        Assert.True(removed.Removed);
        Assert.Equal("Gone", removed.MethodName);
    }

    [Fact]
    public void OverloadsAreComparedByParameterTypesAndAttributeChangesCount()
    {
        var root = CreateSeededDispatchRepository();
        var file = Path.Combine(root, PathInRepo);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "class T { [Theory] public void M(int x) {} [Theory] public void M(string x) {} }");
        Commit(root);
        var baseline = Head(root);
        File.WriteAllText(file, "class T { [Theory(Skip = \"reason\")] public void M(int x) {} [Theory] public void M(string x) {} }");
        Commit(root);
        var read = ReviewerChangedExistingTestReader.Read(root, baseline, Head(root), [PathInRepo]);
        Assert.Null(read.Diagnostic);
        Assert.Equal("M", Assert.Single(read.Entries).MethodName);
        Assert.False(read.Entries[0].Removed);
    }

    [Theory]
    [InlineData("rev-parse")]
    [InlineData("ls-tree")]
    [InlineData("show")]
    public void GitFailureDiscardsAllEntriesAndNamesTheFailedCommand(string failedCommand)
    {
        var read = ReviewerChangedExistingTestReader.Read("fixture", "base", "head", [PathInRepo], (_, args) =>
            args[0] == failedCommand ? new(2, "", "fixture error") : new(0, "fixture output", ""));
        Assert.Empty(read.Entries);
        Assert.Contains("git " + failedCommand, read.Diagnostic!);
        Assert.Contains("fixture error", read.Diagnostic!);
    }

    [Fact]
    public void ThrowingReadAndIncompleteOutputDegradeWithoutThrowing()
    {
        var read = ReviewerChangedExistingTestReader.Read("fixture", "base", "head", [PathInRepo],
            (_, _) => throw new IOException("reading denied"));
        Assert.Empty(read.Entries);
        Assert.Contains("reading denied", read.Diagnostic!);
        foreach (var result in new[]
                 {
                     new GitCli.GitResult(0, "partial", "", DrainTimedOut: true),
                     new GitCli.GitResult(0, "", "", ProcessStarted: false),
                     new GitCli.GitResult(0, "", "")
                 })
        {
            var incomplete = ReviewerChangedExistingTestReader.Read("fixture", "base", "head", [PathInRepo], (_, _) => result);
            Assert.Empty(incomplete.Entries);
            Assert.Contains("git rev-parse", incomplete.Diagnostic!);
        }
    }

    [Fact]
    public void LaterReadingFailureDiscardsPreviouslyFoundChanges()
    {
        var read = ReviewerChangedExistingTestReader.Read("fixture", "base", "head", ["tests/A.cs", "tests/Z.cs"], (_, args) =>
        {
            if (args[0] == "rev-parse") return new(0, args[2].StartsWith("base", StringComparison.Ordinal) ? "base" : "head", "");
            if (args[0] == "ls-tree") return new(0, "present\0", "");
            if (args[1].EndsWith("tests/Z.cs", StringComparison.Ordinal)) return new(7, "", "later read denied");
            return new(0, args[1].StartsWith("base:", StringComparison.Ordinal)
                ? "class A { void M() { X(1); } }" : "class A { void M() { X(2); } }", "");
        });
        Assert.Empty(read.Entries);
        Assert.Contains("git show base:tests/Z.cs", read.Diagnostic!);
        Assert.Contains("later read denied", read.Diagnostic!);
    }

    [Fact]
    public void NoChangedTestPathsNeedsNoGitOrBaseline()
    {
        var read = ReviewerChangedExistingTestReader.Read("fixture", null, null, ["src/Feature.cs", "tests/readme.md"],
            (_, _) => throw new InvalidOperationException("git must not run"));
        Assert.Empty(read.Entries);
        Assert.Null(read.Diagnostic);
    }

    private static void Commit(string root)
    {
        RunGit(root, ["add", "tests"], CommitTime);
        RunGit(root, ["commit", "-m", "Test method fixture"], CommitTime);
    }

    private static string Head(string root)
    {
        var result = GitCli.Run(root, "rev-parse", "HEAD");
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }
}
