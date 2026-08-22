public sealed class GoalAcceptanceVerifierSizeRatchetTests
{
    [Fact]
    public void RealTable_EveryEntryWithinCeiling()
    {
        var repositoryRoot = FindRepositoryRoot();
        var paths = SourceSizeRatchet.SeededCeilings.Select(entry => entry.RelativePath).ToArray();
        var documentationPath = Path.Combine(
            repositoryRoot,
            SourceSizeRatchet.DocumentationPath.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(11, SourceSizeRatchet.SeededCeilings.Count);
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
        Assert.True(
            File.Exists(documentationPath),
            $"Size-ratchet guidance document '{SourceSizeRatchet.DocumentationPath}' does not exist.");
        Assert.Equal(
            SourceSizeRatchet.SeededCeilings.ToArray(),
            ReadDocumentedCeilings(documentationPath));
        AssertNoViolations(SourceSizeRatchet.Evaluate(repositoryRoot, SourceSizeRatchet.SeededCeilings));
    }

    [Fact]
    public void SyntheticTable_OneCompliantAndOneExceeding_FailsOnlyExceeding()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteLines(root, "compliant.txt", 5, "line");
            WriteLines(root, "exceeding.txt", 12, "line");
            var table = new[]
            {
                new SourceSizeCeiling("compliant.txt", 8),
                new SourceSizeCeiling("exceeding.txt", 4),
            };

            var violations = SourceSizeRatchet.Evaluate(root, table);
            var violation = Assert.Single(violations);
            var exception = Record.Exception(() => AssertNoViolations(violations));

            Assert.Equal("exceeding.txt", violation.RelativePath);
            Assert.Equal(12, violation.ActualLineCount);
            Assert.Equal(4, violation.MaximumLineCount);
            Assert.NotNull(exception);
            Assert.Contains("exceeding.txt", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("compliant.txt", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SyntheticTable_AtBelowAndZero_ProducesNoViolations()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteLines(root, "at-ceiling.txt", 4, "line");
            WriteLines(root, "below-ceiling.txt", 3, "line");
            WriteLines(root, "empty.txt", 0, "line");
            var table = new[]
            {
                new SourceSizeCeiling("at-ceiling.txt", 4),
                new SourceSizeCeiling("below-ceiling.txt", 4),
                new SourceSizeCeiling("empty.txt", 4),
            };

            Assert.Empty(SourceSizeRatchet.Evaluate(root, table));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OverCeilingMessage_NamesFileActualCeilingAndBothRemedies()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteLines(root, "exceeding.txt", 12, "line");

            var violation = Assert.Single(SourceSizeRatchet.Evaluate(
                root,
                new[] { new SourceSizeCeiling("exceeding.txt", 4) }));

            Assert.Contains("exceeding.txt", violation.Message, StringComparison.Ordinal);
            Assert.Contains("12", violation.Message, StringComparison.Ordinal);
            Assert.Contains("recorded ceiling of 4", violation.Message, StringComparison.Ordinal);
            Assert.Contains(
                "Extract behavior to a collaborator and lower the ceiling",
                violation.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                "raise the recorded ceiling for this entry deliberately",
                violation.Message,
                StringComparison.Ordinal);
            Assert.Contains("justification in the same change", violation.Message, StringComparison.Ordinal);
            Assert.Contains("SourceSizeRatchet.SeededCeilings", violation.Message, StringComparison.Ordinal);
            Assert.Contains(SourceSizeRatchet.DocumentationPath, violation.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SyntheticTable_MissingFile_ProducesLoudViolation()
    {
        var root = CreateTempDirectory();
        try
        {
            var violation = Assert.Single(SourceSizeRatchet.Evaluate(
                root,
                new[] { new SourceSizeCeiling("renamed-or-deleted.cs", 10) }));

            Assert.Equal("renamed-or-deleted.cs", violation.RelativePath);
            Assert.Null(violation.ActualLineCount);
            Assert.Contains("does not exist", violation.Message, StringComparison.Ordinal);
            Assert.Contains("rename or delete", violation.Message, StringComparison.Ordinal);
            Assert.Contains("update SourceSizeRatchet.SeededCeilings", violation.Message, StringComparison.Ordinal);
            Assert.Contains("in the same change", violation.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SyntheticTable_UnreadableFile_ProducesLoudViolation()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteLines(root, "unreadable.cs", 1, "line");

            var violation = Assert.Single(SourceSizeRatchet.Evaluate(
                root,
                new[] { new SourceSizeCeiling("unreadable.cs", 10) },
                _ => throw new IOException("simulated read failure")));

            Assert.Equal("unreadable.cs", violation.RelativePath);
            Assert.Null(violation.ActualLineCount);
            Assert.Contains("could not be read", violation.Message, StringComparison.Ordinal);
            Assert.Contains("IOException: simulated read failure", violation.Message, StringComparison.Ordinal);
            Assert.Contains(SourceSizeRatchet.DocumentationPath, violation.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EqualSizeDifferentContent_ProducesSameVerdict()
    {
        var root = CreateTempDirectory();
        try
        {
            var atCeilingA = WriteLines(root, "at-ceiling-a.txt", 4, "alpha");
            var atCeilingB = WriteLines(root, "at-ceiling-b.txt", 4, "unrelated edit");
            var overCeilingA = WriteLines(root, "over-ceiling-a.txt", 5, "alpha");
            var overCeilingB = WriteLines(root, "over-ceiling-b.txt", 5, "unrelated edit");

            Assert.NotEqual(File.ReadAllText(atCeilingA), File.ReadAllText(atCeilingB));
            var table = new[]
            {
                new SourceSizeCeiling(Path.GetFileName(atCeilingA), 4),
                new SourceSizeCeiling(Path.GetFileName(atCeilingB), 4),
                new SourceSizeCeiling(Path.GetFileName(overCeilingA), 4),
                new SourceSizeCeiling(Path.GetFileName(overCeilingB), 4),
            };

            var violations = SourceSizeRatchet.Evaluate(root, table);

            Assert.Equal(
                new[] { "over-ceiling-a.txt", "over-ceiling-b.txt" },
                violations.Select(violation => violation.RelativePath));
            Assert.All(violations, violation => Assert.Equal(5, violation.ActualLineCount));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string WriteLines(string root, string fileName, int lineCount, string content)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllLines(path, Enumerable.Repeat(content, lineCount));
        return path;
    }

    private static SourceSizeCeiling[] ReadDocumentedCeilings(string documentationPath)
    {
        const string tableHeader = "| Guarded file | Seeded ceiling |";
        var lines = File.ReadLines(documentationPath).ToArray();
        var headerIndex = Array.FindIndex(lines, line => string.Equals(line, tableHeader, StringComparison.Ordinal));
        Assert.True(headerIndex >= 0, $"{SourceSizeRatchet.DocumentationPath} does not contain the ratchet table.");

        return lines
            .Skip(headerIndex + 2)
            .TakeWhile(line => line.StartsWith("| `", StringComparison.Ordinal))
            .Select(ParseDocumentedCeiling)
            .ToArray();
    }

    private static SourceSizeCeiling ParseDocumentedCeiling(string row)
    {
        var cells = row.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, cells.Length);
        Assert.StartsWith("`", cells[0], StringComparison.Ordinal);
        Assert.EndsWith("`", cells[0], StringComparison.Ordinal);
        Assert.True(
            int.TryParse(cells[1], out var maximumLineCount),
            $"Ratchet table ceiling '{cells[1]}' is not an integer.");
        return new SourceSizeCeiling(cells[0][1..^1], maximumLineCount);
    }

    private static void AssertNoViolations(IReadOnlyList<SourceSizeViolation> violations)
    {
        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations.Select(violation => violation.Message)));
    }
}
