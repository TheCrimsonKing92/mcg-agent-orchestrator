using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SourceSizeRatchetRetightenerTests : GoalWorktreeTestBase
{
    [Fact]
    public void RewriteChangesOnlyEligibleNumbersAndPreservesCommentsAndBom()
    {
        var text = "\uFEFF// new SourceSizeCeiling(\"src/A.cs\", 9)\r\n" +
            "/*\r\nnew SourceSizeCeiling(\"src/A.cs\", 9)\r\n*/\r\n" +
            "    new SourceSizeCeiling(\"src/A.cs\", 9), // keep this\r\n" +
            "    new SourceSizeCeiling(\"src/B.cs\", 5),\r\n" +
            "    new SourceSizeCeiling(\"src/C.cs\", 8),\r\n";
        var main = "new SourceSizeCeiling(\"src/A.cs\", 20)\nnew SourceSizeCeiling(\"src/B.cs\", 20)\n";
        var rewritten = SourceSizeRatchetRetightener.Rewrite(text, main, _ => 12, _ => 0, out var rows);
        Assert.Equal(text.Replace("9), // keep", "12), // keep").Replace("\"src/B.cs\", 5)", "\"src/B.cs\", 12)"), rewritten);
        Assert.Equal(new[] { new SourceSizeRetightenedRow("src/A.cs", 9, 12), new("src/B.cs", 5, 12) }, rows);
    }

    [Theory]
    [InlineData(15, 20, 18, 1)] // The goal added a line.
    [InlineData(20, 20, 21, 0)] // The goal did not lower the row.
    [InlineData(21, 20, 22, 0)] // The goal raised the row.
    [InlineData(15, 20, 15, 0)] // No overflow.
    [InlineData(15, 20, null, 0)] // Missing or unreadable file.
    [InlineData(15, 20, 18, null)] // Unavailable or binary numstat.
    public void IneligibleRowsAreByteIdentical(int oldValue, int mainValue, int? count, int? added)
    {
        var text = Row(oldValue);
        Assert.Equal(text, SourceSizeRatchetRetightener.Rewrite(text, Row(mainValue), _ => count, _ => added, out var rows));
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData(18)]
    [InlineData(21)]
    public void PureMoveOverflowRetightensToMeasuredLengthEvenAboveMainCeiling(int count)
    {
        Assert.Equal(Row(count), SourceSizeRatchetRetightener.Rewrite(Row(15), Row(20),
            _ => count, _ => 0, out var rows));
        Assert.Equal(new SourceSizeRetightenedRow("src/A.cs", 15, count), Assert.Single(rows));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicatePathsAreIneligible(bool duplicateIntegrated)
    {
        var text = duplicateIntegrated ? Row(15) + Row(15) : Row(15);
        var main = duplicateIntegrated ? Row(20) : Row(20) + Row(20);
        Assert.Equal(text, SourceSizeRatchetRetightener.Rewrite(text, main, _ => 18, _ => 0, out var rows));
        Assert.Empty(rows);
    }

    [Fact]
    public void RewriteRecognizesEverySeededRow()
    {
        var authority = File.ReadAllText(Path.Combine(FindCurrentSourceRoot(), SourceSizeRatchet.SourcePath));
        var main = authority;
        foreach (var row in SourceSizeRatchet.SeededCeilings)
            main = main.Replace($"new SourceSizeCeiling(\"{row.RelativePath}\", {row.MaximumLineCount})",
                $"new SourceSizeCeiling(\"{row.RelativePath}\", {row.MaximumLineCount + 1})", StringComparison.Ordinal);
        var byPath = SourceSizeRatchet.SeededCeilings.ToDictionary(row => row.RelativePath);
        Assert.Equal(main, SourceSizeRatchetRetightener.Rewrite(authority, main,
            path => byPath[path].MaximumLineCount + 1, _ => 0, out var rewritten));
        Assert.Equal(SourceSizeRatchet.SeededCeilings.Count, rewritten.Count);
        foreach (var row in rewritten) Assert.Equal(byPath[row.RelativePath].MaximumLineCount, row.OldCeiling);
    }

    [Theory]
    [InlineData("new SourceSizeCeiling(\"src/A.cs\", 18)\n", true)]
    [InlineData("new SourceSizeCeiling(\"src/A.cs\", 14)\n", false)]
    [InlineData("new SourceSizeCeiling(\"src/A.cs\", 18) // extra\n", false)]
    [InlineData("new SourceSizeCeiling(\"src/B.cs\", 18)\n", false)]
    public void CarryForwardPeelRequiresOnlyNumericIncreases(string after, bool expected) =>
        Assert.Equal(expected, SourceSizeRatchetRetightener.IsNumericRetightenOnly(Row(15), after));

    private static string Row(int ceiling) => $"new SourceSizeCeiling(\"src/A.cs\", {ceiling})\n";
}
