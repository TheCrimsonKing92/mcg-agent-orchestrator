using System.Globalization;

public sealed class GoalAcceptanceVerifierSizeRatchetTests
{
    // Seeded at fde5f80f62ae388f98eb13c712cfe3abb4782b44 using File.ReadLines(path).Count().
    // Raised from 8713 for goal 75b85ca1: effective lane selection and structural-coverage
    // threading belong to the gate-plan owner, so extracting them would split that invariant.
    // Raised again for goal 85f0b81d: 227 new behavior lines were extracted to
    // AcceptanceLaneDurationStore, so only call-site lines remained here.
    private const int MaximumLineCount = 8763;
    private const string SourceRelativePath =
        "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs";
    private const string DocumentationPath = "docs/god-class-decomposition-plan.md";

    [Fact]
    public void RealFile_CurrentLineCount_StaysWithinRecordedCeiling()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            repositoryRoot,
            SourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(sourcePath), $"Expected ratcheted source file at '{sourcePath}'.");

        AssertWithinCeiling(CountLines(sourcePath));
    }

    [Fact]
    public void SyntheticCountAboveCeiling_FailsAndNamesBothRemedies()
    {
        var actualLineCount = MaximumLineCount + 1;

        var exception = Record.Exception(() => AssertWithinCeiling(actualLineCount));

        Assert.NotNull(exception);
        Assert.Contains(
            MaximumLineCount.ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            actualLineCount.ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("Extract behavior to a collaborator", exception.Message, StringComparison.Ordinal);
        Assert.Contains("raise MaximumLineCount deliberately", exception.Message, StringComparison.Ordinal);
        Assert.Contains("justification", exception.Message, StringComparison.Ordinal);
        Assert.Contains(DocumentationPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SyntheticCountAtOrBelowCeiling_Passes()
    {
        Assert.Null(Record.Exception(() => AssertWithinCeiling(MaximumLineCount)));
        Assert.Null(Record.Exception(() => AssertWithinCeiling(MaximumLineCount - 1)));
        Assert.Null(Record.Exception(() => AssertWithinCeiling(0)));
    }

    [Fact]
    public void EqualSizeDifferentContent_ProducesSameVerdict()
    {
        var root = CreateTempDirectory();
        try
        {
            var atCeilingA = WriteLines(root, "at-ceiling-a.txt", MaximumLineCount, "alpha");
            var atCeilingB = WriteLines(root, "at-ceiling-b.txt", MaximumLineCount, "unrelated edit");
            var overCeilingA = WriteLines(root, "over-ceiling-a.txt", MaximumLineCount + 1, "alpha");
            var overCeilingB = WriteLines(root, "over-ceiling-b.txt", MaximumLineCount + 1, "unrelated edit");

            Assert.NotEqual(File.ReadAllText(atCeilingA), File.ReadAllText(atCeilingB));
            var firstAtCeilingCount = CountLines(atCeilingA);
            var secondAtCeilingCount = CountLines(atCeilingB);
            var firstOverCeilingCount = CountLines(overCeilingA);
            var secondOverCeilingCount = CountLines(overCeilingB);
            Assert.Equal(MaximumLineCount, firstAtCeilingCount);
            Assert.Equal(firstAtCeilingCount, secondAtCeilingCount);
            Assert.Equal(MaximumLineCount + 1, firstOverCeilingCount);
            Assert.Equal(firstOverCeilingCount, secondOverCeilingCount);
            Assert.Null(Record.Exception(() => AssertWithinCeiling(firstAtCeilingCount)));
            Assert.Null(Record.Exception(() => AssertWithinCeiling(secondAtCeilingCount)));

            var firstFailure = Record.Exception(() => AssertWithinCeiling(firstOverCeilingCount));
            var secondFailure = Record.Exception(() => AssertWithinCeiling(secondOverCeilingCount));
            Assert.NotNull(firstFailure);
            Assert.NotNull(secondFailure);
            Assert.Equal(firstFailure.Message, secondFailure.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int CountLines(string path)
        => File.ReadLines(path).Count();

    private static string WriteLines(string root, string fileName, int lineCount, string content)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllLines(path, Enumerable.Repeat(content, lineCount));
        return path;
    }

    private static void AssertWithinCeiling(int actualLineCount)
    {
        Assert.True(actualLineCount <= MaximumLineCount, BuildFailureMessage(actualLineCount));
    }

    private static string BuildFailureMessage(int actualLineCount)
    {
        return $"GoalAcceptanceVerifier.cs has {actualLineCount.ToString(CultureInfo.InvariantCulture)} lines, " +
            $"exceeding the recorded ceiling of {MaximumLineCount.ToString(CultureInfo.InvariantCulture)}. " +
            "Extract behavior to a collaborator and lower the ceiling, or raise MaximumLineCount deliberately " +
            $"with justification in the same change. See {DocumentationPath}.";
    }
}
