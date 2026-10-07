using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test uses its own temporary filesystem root from the support fixture.
public sealed class PlannerOutputContractTestsCitationLine : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("\n")]
    [Xunit.InlineData("\r\n")]
    public void MissingCitationReportsExactPlanLine(string newline)
    {
        const string citation = "src/Missing.cs";
        const string line = "  - Extend `src/Missing.cs` with focused contract rejection coverage.  ";
        var root = CreateTempDirectory();
        var plan = PlanWithTargetLine(line).ReplaceLineEndings(newline);

        Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(plan, root, out var validated, out var diagnostic));
        var spanStart = validated.IndexOf(citation, StringComparison.Ordinal);
        var lineNumber = validated[..spanStart].Count(character => character == '\n') + 1;
        var expectedPrefix = $"target citation '{citation}' does not exist and is not marked as a new file; source span [{spanStart}..{spanStart + citation.Length})." +
            $"{Environment.NewLine}Offending citation: '{citation}'";
        Xunit.Assert.StartsWith(expectedPrefix, diagnostic, StringComparison.Ordinal);
        Xunit.Assert.EndsWith($"Offending plan line {lineNumber}: '{line.Trim()}'", diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Equal(expectedPrefix + $" Offending plan line {lineNumber}: '{line.Trim()}'", diagnostic);
    }

    [Xunit.Fact]
    public void AmbiguousCitationReportsExactPlanLine()
    {
        const string citation = "Duplicate.cs";
        const string line = "  - Extend `Duplicate.cs` with focused ambiguity coverage.  ";
        var root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "first"));
        Directory.CreateDirectory(Path.Combine(root, "second"));
        File.WriteAllText(Path.Combine(root, "first", citation), "// first");
        File.WriteAllText(Path.Combine(root, "second", citation), "// second");
        var plan = PlanWithTargetLine(line);

        Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(plan, root, out var validated, out var diagnostic));
        var spanStart = validated.IndexOf(citation, StringComparison.Ordinal);
        var lineNumber = validated[..spanStart].Count(character => character == '\n') + 1;
        Xunit.Assert.Contains($"target citation '{citation}' is ambiguous;", diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains($"source span [{spanStart}..{spanStart + citation.Length}).", diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains($"{Environment.NewLine}Offending citation: '{citation}'", diagnostic, StringComparison.Ordinal);
        Xunit.Assert.EndsWith($"Offending plan line {lineNumber}: '{line.Trim()}'", diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void LongCitingLineIsBoundedToTwoHundredCharacters()
    {
        const string start = "- Extend `src/Missing.cs` with focused coverage ";
        var line = start + new string('x', 300 - start.Length);
        var plan = PlanWithTargetLine("  " + line + "  ");
        var root = CreateTempDirectory();

        Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(plan, root, out var validated, out var diagnostic));
        var spanStart = validated.IndexOf("src/Missing.cs", StringComparison.Ordinal);
        var lineNumber = validated[..spanStart].Count(character => character == '\n') + 1;
        var bounded = line[..199] + "…";
        Xunit.Assert.Equal(200, bounded.Length);
        Xunit.Assert.EndsWith($"Offending plan line {lineNumber}: '{bounded}'", diagnostic, StringComparison.Ordinal);
    }

    private static string PlanWithTargetLine(string line)
    {
        var plan = PlannerContractPlanFixture().ReplaceLineEndings("\n");
        const string heading = "## Target seams and symbols";
        var bodyStart = plan.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var bodyEnd = plan.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        return plan[..bodyStart] + "\n\n" + line + "\n" + plan[bodyEnd..];
    }
}
