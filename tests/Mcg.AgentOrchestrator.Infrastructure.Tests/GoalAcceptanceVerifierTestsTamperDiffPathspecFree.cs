using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTestsTamperDiffPathspecFree : GoalAcceptanceVerifierTestBase
{
    [Fact]
    public async Task TamperDiffCommandDoesNotGrowWithChangedTestPaths()
    {
        var changedFiles = Enumerable.Range(0, 150)
            .Select(index => $"tests/Project.Tests/{index:D3}/".PadRight(77, 'x') + ".cs").ToArray();
        Assert.Equal(150, changedFiles.Length);
        Assert.All(changedFiles, path =>
        {
            Assert.Equal(80, path.Length);
            Assert.Contains("Tests", path, StringComparison.OrdinalIgnoreCase);
        });
        var calls = new List<string[]>();
        var result = await RunGuardAsync(changedFiles, DiffSection(changedFiles[0], "+    Assert.True(value);"), calls);

        Assert.Equal(["git", "diff", "--unified=0", "main...HEAD"], Assert.Single(calls));
        Assert.True(result.Advisory);
        Assert.True(result.Passed);
        Assert.Equal("no test degradation detected", result.ResultSummary);
    }

    [Fact]
    public async Task OnlyTestSectionsContributeDegradationSignals()
    {
        const string testPath = "tests/Project.Tests/FooTests.cs";
        const string sourcePath = "src/Project/Foo.cs";
        var diff = DiffSection(testPath, "-    Assert.True(value);") +
            DiffSection(sourcePath, "-    Assert.Equal(1, value);");
        var result = await RunGuardAsync([testPath, sourcePath], diff, []);

        Assert.True(result.Advisory);
        Assert.False(result.Passed);
        Assert.Equal("1 test degradation signal(s)", result.ResultSummary);
        Assert.Contains(testPath, result.OutputTail!, StringComparison.Ordinal);
        Assert.Contains("net -1 assertion", result.OutputTail!, StringComparison.Ordinal);
        Assert.DoesNotContain(sourcePath, result.OutputTail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnavailableDiffRemainsPassedAdvisory()
    {
        var calls = new List<string[]>();
        var result = await RunGuardAsync(["tests/Project.Tests/FooTests.cs"], "git failed", calls, exitCode: 1);

        Assert.Equal(["git", "diff", "--unified=0", "main...HEAD"], Assert.Single(calls));
        Assert.True(result.Advisory);
        Assert.True(result.Passed);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("diff unavailable", result.ResultSummary);
    }

    [Theory]
    [InlineData("tests/Project.Tests/FooTests.cs", true)]
    [InlineData("src/Project/Foo.cs", false)]
    public void DeletedFileUsesOldPath(string path, bool keep)
    {
        var diff = $"diff --git a/{path} b/{path}\r\n--- a/{path}\r\n+++ /dev/null\r\n@@ -1 +0,0 @@\r\n-Assert.True(value);\r\n";
        Assert.Equal(keep ? diff : "", GoalAcceptanceVerifier.FilterTestFileDiffSections(diff));
    }

    [Theory]
    [InlineData("tests/Project.Tests/FooTests.cs", "src/Project/Foo.cs", true)]
    [InlineData("src/Project/Foo.cs", "tests/Project.Tests/FooTests.cs", true)]
    [InlineData("src/Project/Foo.cs", "src/Project/Bar.cs", false)]
    public void RenameConsidersBothSides(string before, string after, bool keep)
    {
        var diff = $"diff --git a/{before} b/{after}\nsimilarity index 100%\nrename from {before}\nrename to {after}\n";
        Assert.Equal(keep ? diff : "", GoalAcceptanceVerifier.FilterTestFileDiffSections(diff));
    }

    [Theory]
    [InlineData("tests/Project.Tests/Foo Tests.cs", true)]
    [InlineData("src/Project/Foo bar.cs", false)]
    public void HeaderOnlySectionHandlesSpaces(string path, bool keep)
    {
        var diff = $"diff --git a/{path} b/{path}\nold mode 100644\nnew mode 100755\n";
        Assert.Equal(keep ? diff : "", GoalAcceptanceVerifier.FilterTestFileDiffSections(diff));
    }

    [Theory]
    [InlineData("Project.\\124ests/Foo\\303\\244.cs", true)]
    [InlineData("Project/Foo\\303\\244.cs", false)]
    [InlineData("Project.\\124ests/Foo\\t\\\"bar\\\\baz.cs", true)]
    public void QuotedPathsAreDecodedBeforeClassification(string quotedPath, bool keep)
    {
        var diff = $"diff --git \"a/{quotedPath}\" \"b/{quotedPath}\"\n--- \"a/{quotedPath}\"\n+++ \"b/{quotedPath}\"\n@@ -1 +0,0 @@\n-Assert.True(value);\n";
        Assert.Equal(keep ? diff : "", GoalAcceptanceVerifier.FilterTestFileDiffSections(diff));
        var headerOnly = $"diff --git \"a/{quotedPath}\" \"b/{quotedPath}\"\nold mode 100644\nnew mode 100755\n";
        Assert.Equal(keep ? headerOnly : "", GoalAcceptanceVerifier.FilterTestFileDiffSections(headerOnly));
    }

    [Fact]
    public void HunkContentCannotOverrideSectionPaths()
    {
        var testDiff = DiffSection("tests/Project.Tests/FooTests.cs", "+++ b/src/Project/Foo.cs");
        var sourceDiff = DiffSection("src/Project/Foo.cs", "--- a/tests/Project.Tests/FooTests.cs");
        Assert.Equal(testDiff, GoalAcceptanceVerifier.FilterTestFileDiffSections(testDiff + sourceDiff));
    }

    [Fact]
    public void FilteringPreservesSectionsAndLineEndings()
    {
        var testDiff = DiffSection("tests/Project.Tests/FooTests.cs", "-Assert.True(value);").Replace("\n", "\r\n");
        var sourceDiff = DiffSection("src/Project/Foo.cs", "-Assert.True(value);");
        Assert.Equal(testDiff, GoalAcceptanceVerifier.FilterTestFileDiffSections(sourceDiff + testDiff + sourceDiff));
        Assert.Equal("", GoalAcceptanceVerifier.FilterTestFileDiffSections(""));
    }

    private async Task<AcceptanceCheckResult> RunGuardAsync(
        string[] changedFiles, string diff, List<string[]> calls, int exitCode = 0)
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                if (args.Contains("--unified=0"))
                {
                    calls.Add(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(exitCode, diff));
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            });
            var result = await verifier.RunAsync(root, changedFiles: changedFiles);
            Assert.True(result.Passed);
            return Assert.Single(result.Checks!, check => check.Name == "test tamper guard");
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    private static string DiffSection(string path, string change) =>
        $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -1 +1 @@\n{change}\n";
}
