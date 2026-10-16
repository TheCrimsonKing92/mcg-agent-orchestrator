using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: rewrite inputs are local, and the repository authority is only read.
public sealed class SourceSizeRatchetRetightenerTestsLowering : GoalWorktreeTestBase
{
    [Fact]
    public void FileLoweringPreservesEveryOtherByte()
    {
        var text = "\uFEFF// justification stays\r\n" +
            "new SourceSizeCeiling(\"src/A.cs\", 20), // trailing stays\r\n" +
            "new SourceSizeCeiling(\"src/B.cs\", 10),\r\n" +
            "// new SourceSizeCeiling(\"src/A.cs\", 20)\r\n" +
            "/*\r\nnew SourceSizeCeiling(\"src/A.cs\", 20)\r\n*/\r\n";
        var expected = text.Replace("20), // trailing", "17), // trailing", StringComparison.Ordinal);
        var actual = SourceSizeRatchetRetightener.Rewrite(text, "", path => path == "src/A.cs" ? 17 : 10,
            _ => throw new InvalidOperationException("Lowering must not depend on added lines."), out var rows);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
        Assert.Equal(new[] { new SourceSizeRetightenedRow("src/A.cs", 20, 17) }, rows);
    }

    [Theory]
    [InlineData(true, 17)]
    [InlineData(false, null)]
    [InlineData(false, 25)]
    [InlineData(false, 20)]
    public void FileNegativeControlsAreByteIdentical(bool duplicate, int? count)
    {
        const string row = "new SourceSizeCeiling(\"src/A.cs\", 20)\r\n";
        var text = "\uFEFF" + row + (duplicate ? row : "");
        var actual = SourceSizeRatchetRetightener.Rewrite(text, "", _ => count, _ => 0, out var rows);
        Assert.Equal(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes(actual));
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData(9, 2, 9, 2)]
    [InlineData(10, 3, 10, 3)]
    [InlineData(12, 4, 12, 3)]
    [InlineData(0, 0, 12, 3)]
    public void ClassLoweringClampsEachNumberAndRequiresDeclaringFiles(int lines, int files, int expectedLines, int expectedFiles)
    {
        var text = ClassRow(12, 3);
        var actual = SourceSizeRatchetRetightener.Rewrite(text, "", _ => null, _ => null, out var rows,
            new Dictionary<string, (int, int)> { ["Widget"] = (lines, files) });
        Assert.Equal(Encoding.UTF8.GetBytes(ClassRow(expectedLines, expectedFiles)), Encoding.UTF8.GetBytes(actual));
        if (expectedLines == 12 && expectedFiles == 3) Assert.Empty(rows);
        else Assert.Equal(new SourceSizeRetightenedRow("class:Widget", 12, expectedLines, 3, expectedFiles), Assert.Single(rows));
    }

    [Fact]
    public void ExistingCallShapeLeavesClassRowsByteIdentical()
    {
        var text = ClassRow(12, 3);
        var actual = SourceSizeRatchetRetightener.Rewrite(text, "", _ => 1, _ => 0, out var rows);
        Assert.Equal(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes(actual));
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClassDuplicatesAndUnknownClassesAreUnchanged(bool duplicate)
    {
        var text = ClassRow(12, 3) + (duplicate ? ClassRow(12, 3) : "");
        var measurements = new Dictionary<string, (int, int)> { [duplicate ? "Widget" : "Other"] = (9, 2) };
        Assert.Equal(text, SourceSizeRatchetRetightener.Rewrite(text, "", _ => null, _ => null, out var rows, measurements));
        Assert.Empty(rows);
    }

    [Fact]
    public void ClassCommentAndStringLookalikesStayUnchanged()
    {
        var row = ClassRow(12, 3);
        var lookalikes = "// " + row + "/* " + row + " */\r\n" +
            "var text = @\"" + row.Replace("\"", "\"\"") + "\";\r\n" +
            "var raw = \"\"\"\r\n\"\r\n" + row + "\"\"\";\r\n";
        var text = lookalikes + row;
        var actual = SourceSizeRatchetRetightener.Rewrite(text, "", _ => null, _ => null, out var rows,
            new Dictionary<string, (int, int)> { ["Widget"] = (9, 2) });
        Assert.Equal(lookalikes + ClassRow(9, 2), actual);
        Assert.Equal(new SourceSizeRetightenedRow("class:Widget", 12, 9, 3, 2), Assert.Single(rows));
    }

    [Fact]
    public void RecognizesEveryRealClassRowAndReportsItsOldPair()
    {
        var authority = File.ReadAllText(Path.Combine(FindCurrentSourceRoot(), SourceSizeRatchet.SourcePath));
        var measurements = SourceSizeRatchet.SeededClassCeilings.ToDictionary(row => row.ClassName,
            row => (row.MaximumTotalLineCount - 1, row.MaximumPartialFileCount - 1));
        var expected = authority;
        foreach (var row in SourceSizeRatchet.SeededClassCeilings)
            expected = expected.Replace($"new SourceClassCeiling(\"{row.ClassName}\", {row.MaximumTotalLineCount}, {row.MaximumPartialFileCount})",
                $"new SourceClassCeiling(\"{row.ClassName}\", {row.MaximumTotalLineCount - 1}, {row.MaximumPartialFileCount - 1})", StringComparison.Ordinal);
        Assert.Equal(expected, SourceSizeRatchetRetightener.Rewrite(authority, authority, _ => null, _ => null,
            out var rows, measurements));
        Assert.Equal(7, rows.Count);
        Assert.Equal(SourceSizeRatchet.SeededClassCeilings.Select(row => new SourceSizeRetightenedRow(
            $"class:{row.ClassName}", row.MaximumTotalLineCount, row.MaximumTotalLineCount - 1,
            row.MaximumPartialFileCount, row.MaximumPartialFileCount - 1)), rows);
    }

    [Theory]
    [InlineData("new SourceSizeCeiling(\"src/A.cs\", 15)", "new SourceSizeCeiling(\"src/A.cs\", 14)", true)]
    [InlineData("new SourceClassCeiling(\"Widget\", 12, 3)", "new SourceClassCeiling(\"Widget\", 9, 3)", true)]
    [InlineData("new SourceClassCeiling(\"Widget\", 12, 3)", "new SourceClassCeiling(\"Widget\", 12, 2)", true)]
    [InlineData("new SourceClassCeiling(\"Widget\", 12, 3)", "new SourceClassCeiling(\"Other\", 9, 2)", false)]
    [InlineData("new SourceClassCeiling(\"Widget\", 12, 3)", "new SourceClassCeiling(\"Widget\", 9, 2) // extra", false)]
    [InlineData("new SourceClassCeiling(\"Widget\", 12, 3)", "new SourceClassCeiling(\"Widget\", 12, 3)", false)]
    [InlineData("new SourceSizeCeiling(\"src/A.cs\", 15)", "new SourceSizeCeiling(\"src/A.cs\", 14)\nnew SourceClassCeiling(\"Widget\", 9, 2)", false)]
    [InlineData("new SourceSizeCeiling(\"src/A.cs\", 15)\nnew SourceClassCeiling(\"Widget\", 12, 3)",
        "new SourceSizeCeiling(\"src/A.cs\", 16)\nnew SourceClassCeiling(\"Widget\", 9, 2)", true)]
    [InlineData("new SourceSizeCeiling(\"src/A.cs\", 15)\nnew SourceClassCeiling(\"Widget\", 12, 3)",
        "new SourceClassCeiling(\"Widget\", 9, 2)\nnew SourceSizeCeiling(\"src/A.cs\", 16)", false)]
    [InlineData("new SourceSizeCeiling(\"src/A.cs\", 15)\nnew SourceClassCeiling(\"Widget\", 12, 3)",
        "new SourceSizeCeiling(\"src/A.cs\", 14)", false)]
    public void PeelAcceptsOnlyNumericChangesWithTheSameRowSequence(string before, string after, bool expected) =>
        Assert.Equal(expected, SourceSizeRatchetRetightener.IsNumericRetightenOnly(before, after));

    private static string ClassRow(int lines, int files) => $"new SourceClassCeiling(\"Widget\", {lines}, {files})\r\n";
}
