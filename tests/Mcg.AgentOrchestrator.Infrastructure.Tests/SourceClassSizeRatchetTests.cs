using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SourceClassSizeRatchetTests
{
    [Fact]
    public void RealTable_EveryClassWithinCeilings()
    {
        var violations = SourceSizeRatchet.EvaluateClasses(FindRepositoryRoot(), SourceSizeRatchet.SeededClassCeilings);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations.Select(item => item.Message)));
    }

    [Fact]
    public void RealTable_SeedsEqualMeasuredTotalsAndCounts()
    {
        var root = FindRepositoryRoot();
        Assert.Equal(
            new[] { "ConductorDriver", "CliCommandHandlers", "GoalAcceptanceVerifier", "AgentOrchestratorKernel",
                "ConductorBatchLoop", "CliPersistentStateRunner" },
            SourceSizeRatchet.SeededClassCeilings.Select(row => row.ClassName));
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
                .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .Select(path => File.ReadAllLines(path)).ToArray();

        foreach (var row in SourceSizeRatchet.SeededClassCeilings)
        {
            var partials = files.Where(lines => Regex.IsMatch(string.Join("\n", lines),
                $@"\bpartial\s+class\s+{Regex.Escape(row.ClassName)}\b", RegexOptions.CultureInvariant)).ToArray();
            Assert.Equal(partials.Sum(lines => lines.Length), row.MaximumTotalLineCount);
            Assert.Equal(partials.Length, row.MaximumPartialFileCount);
        }
    }

    [Fact]
    public void ParsedAuthority_MatchesCompiledClassCeilings()
    {
        Assert.Equal(SourceSizeRatchet.SeededClassCeilings,
            SourceSizeRatchetPreflight.TryReadClassAuthority(FindRepositoryRoot()));
    }

    [Fact]
    public void TotalOneLineOver_ReportsTotalLimitAndExtraction()
    {
        var violation = Assert.Single(Evaluate(
            new SourceClassCeiling("Widget", 4, 2),
            new Dictionary<string, string[]>
            {
                ["src/Widget.cs"] = Partial(2),
                ["src/Widget.Other.cs"] = Partial(3),
            }));

        Assert.Equal("class:Widget", violation.RelativePath);
        Assert.Equal(5, violation.ActualLineCount);
        Assert.Equal(4, violation.MaximumLineCount);
        Assert.Contains("class total-line ceiling", violation.Message, StringComparison.Ordinal);
        AssertRemedies(violation);
    }

    [Fact]
    public void OneExtraPartial_ReportsFileCountLimitAndExtraction()
    {
        var violation = Assert.Single(Evaluate(
            new SourceClassCeiling("Widget", 10, 1),
            new Dictionary<string, string[]>
            {
                ["src/Widget.cs"] = Partial(2),
                ["src/Widget.Other.cs"] = Partial(2),
            }));

        Assert.Equal("class:Widget", violation.RelativePath);
        Assert.Equal(2, violation.ActualLineCount);
        Assert.Equal(1, violation.MaximumLineCount);
        Assert.Contains("partial-file-count ceiling", violation.Message, StringComparison.Ordinal);
        AssertRemedies(violation);
    }

    [Fact]
    public void BothLimitsExceeded_ReportsTotalThenFileCount()
    {
        var violations = Evaluate(new SourceClassCeiling("Widget", 3, 1),
            new Dictionary<string, string[]>
            {
                ["src/Widget.cs"] = Partial(2),
                ["src/Widget.Other.cs"] = Partial(2),
            });

        Assert.Equal(2, violations.Count);
        Assert.All(violations, item => Assert.Equal("class:Widget", item.RelativePath));
        Assert.Contains("class total-line ceiling", violations[0].Message, StringComparison.Ordinal);
        Assert.Contains("partial-file-count ceiling", violations[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MovingLinesBetweenExistingPartials_PreservesCompliantTotal()
    {
        var ceiling = new SourceClassCeiling("Widget", 6, 2);
        var files = new Dictionary<string, string[]>
        {
            ["src/Widget.cs"] = Partial(3),
            ["src/Widget.Other.cs"] = Partial(3),
        };
        Assert.Empty(Evaluate(ceiling, files));

        files["src/Widget.cs"] = Partial(2);
        files["src/Widget.Other.cs"] = Partial(4);

        Assert.Empty(Evaluate(ceiling, files));
    }

    [Fact]
    public void SourceFiltering_ExcludesGeneratedTreesOtherRootsAndNamePrefixes()
    {
        var files = new Dictionary<string, string[]>
        {
            ["src/Widget.cs"] = ["internal sealed partial", "class Widget { }"],
            ["src/Widget.Other.cs"] = ["partial class WidgetSuffix { }"],
            ["src/bin/Widget.cs"] = Partial(3),
            ["src/project/OBJ/Widget.cs"] = Partial(3),
            ["tests/Widget.cs"] = Partial(3),
            ["src/Widget.txt"] = Partial(3),
            ["src/../Widget.cs"] = Partial(3),
            ["src\\project\\bin\\Widget.cs"] = Partial(3),
        };
        var reads = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "class-ratchet-in-memory");

        Assert.Empty(SourceSizeRatchet.EvaluateClasses(root, [new SourceClassCeiling("Widget", 2, 1)],
            path => { reads.Add(path); return files[path]; }, _ => files.Keys));
        Assert.Equal(new[] { "src/Widget.cs", "src/Widget.Other.cs" }, reads);

        var absolutePath = Path.Combine(root, "src", "Widget.cs");
        Assert.Empty(SourceSizeRatchet.EvaluateClasses(root, [new SourceClassCeiling("Widget", 2, 1)],
            path => { Assert.Equal(absolutePath, path); return Partial(2); }, _ => [absolutePath]));
    }

    [Fact]
    public void EmptyClassTable_DoesNotEnumerateOrRead()
    {
        Assert.Empty(SourceSizeRatchet.EvaluateClasses("unused", [],
            _ => throw new InvalidOperationException("must not read"),
            _ => throw new InvalidOperationException("must not enumerate")));
    }

    [Theory]
    [InlineData("Widget total 1234")]
    [InlineData("Widget total 1,234")]
    [InlineData("Widget partial files 2")]
    public void Documentation_DuplicatedClassValue_ProducesLoudViolation(string duplicate)
    {
        var violation = Assert.Single(SourceSizeRatchet.EvaluateDocumentation(
            [SourceSizeRatchet.DocumentationSectionHeading,
                $"{SourceSizeRatchet.SeededCeilingsSymbol} {SourceSizeRatchet.SourcePath}",
                SourceSizeRatchet.SeededClassCeilingsSymbol, duplicate],
            [], [new SourceClassCeiling("Widget", 1234, 2)]));

        Assert.Equal("duplicated-ceiling-record", violation.Rule);
        Assert.Equal(4, violation.LineNumber);
        Assert.Contains(SourceSizeRatchet.SeededClassCeilingsSymbol, violation.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<SourceSizeViolation> Evaluate(
        SourceClassCeiling ceiling, Dictionary<string, string[]> files)
    {
        return SourceSizeRatchet.EvaluateClasses("in-memory", [ceiling], path => files[path], _ => files.Keys);
    }

    private static string[] Partial(int count) =>
        new[] { "internal sealed partial class Widget { }" }.Concat(Enumerable.Repeat("// member", count - 1)).ToArray();

    private static void AssertRemedies(SourceSizeViolation violation)
    {
        Assert.Contains("Extract members into a separately owned type", violation.Message, StringComparison.Ordinal);
        Assert.Contains("raise the row", violation.Message, StringComparison.Ordinal);
        Assert.Contains("justification directly above", violation.Message, StringComparison.Ordinal);
        Assert.Contains(SourceSizeRatchet.SeededClassCeilingsSymbol, violation.Message, StringComparison.Ordinal);
    }
}
