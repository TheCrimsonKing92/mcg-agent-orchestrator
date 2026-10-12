using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns a temporary filesystem root through the support fixture.
public sealed class PlannerOutputContractTestsMarkedWildcard : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("- Inspect `*.cs` — new class.", "*.cs")]
    [Xunit.InlineData("- Inspect `Missing?.cs` — new class.", "Missing?.cs")]
    [Xunit.InlineData("- Inspect `src/*.cs` (new file).", "src/*.cs")]
    [Xunit.InlineData("- Inspect `src/Missing?.cs` (new file).", "src/Missing?.cs")]
    [Xunit.InlineData("- Add new file `src/*/Missing.cs` for coverage.", "src/*/Missing.cs")]
    [Xunit.InlineData("- Add new file `src/?/Missing.cs` for coverage.", "src/?/Missing.cs")]
    [Xunit.InlineData("- Create `tests/**/*.csproj` for coverage.", "tests/**/*.csproj")]
    [Xunit.InlineData("- Create `tests/?/Missing.csproj` for coverage.", "tests/?/Missing.csproj")]
    public void MarkedWildcardTargets_AreRejected(string targetLine, string citation)
    {
        var workingDirectory = CreateTempDirectory();
        var plan = PlanWithTargetLine(targetLine);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        AssertWildcardRejection(result, citation, targetLine);

        var concreteCitation = ConcretePath(citation);
        Xunit.Assert.False(File.Exists(Path.Combine(workingDirectory, concreteCitation)));
        var concretePlan = PlanWithTargetLine(targetLine.Replace(citation, concreteCitation, StringComparison.Ordinal));
        var concreteResult = PlannerOutputContract.Resolve(concretePlan, string.Empty, workingDirectory);

        Xunit.Assert.True(concreteResult.Succeeded, concreteResult.Diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("src/*/Missing.cs", "Missing.cs")]
    [Xunit.InlineData("src/?/Missing.cs", "Missing.cs")]
    [Xunit.InlineData("src/*.cs", "*.cs")]
    [Xunit.InlineData("src/Missing?.cs", "Missing?.cs")]
    public void InheritedMarkedWildcardTargets_AreRejected(string precedingPath, string citation)
    {
        var workingDirectory = CreateTempDirectory();
        var targetLine = $"- Inspect `{citation}` for the implementation seam.";
        var plan = PlanWithTargetLine(targetLine, precedingPath);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        AssertWildcardRejection(result, citation, targetLine);

        var concretePath = ConcretePath(precedingPath);
        Xunit.Assert.False(File.Exists(Path.Combine(workingDirectory, concretePath)));
        var concretePlan = PlanWithTargetLine(ConcretePath(targetLine), concretePath);
        var concreteResult = PlannerOutputContract.Resolve(concretePlan, string.Empty, workingDirectory);

        Xunit.Assert.True(concreteResult.Succeeded, concreteResult.Diagnostic);
    }

    private static void AssertWildcardRejection(PlannerOutputContractResult result, string citation, string targetLine)
    {
        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("path contains a wildcard", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Offending citation: '{citation}'", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("source span [", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("Offending plan line ", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains(targetLine, result.Diagnostic, StringComparison.Ordinal);
    }

    private static string ConcretePath(string value) => value.Replace('*', 'N').Replace('?', 'N');

    private static string PlanWithTargetLine(string line, string? precedingPath = null)
    {
        var plan = PlannerContractPlanFixture().ReplaceLineEndings("\n");
        const string heading = "## Target seams and symbols";
        if (precedingPath is not null)
        {
            plan = plan.Replace(heading, $"- Create `{precedingPath}` for the implementation seam.\n\n" + heading, StringComparison.Ordinal);
        }

        var bodyStart = plan.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var bodyEnd = plan.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        return plan[..bodyStart] + "\n\nValidate new file targets against the cited implementation path.\n" + line + "\n" + plan[bodyEnd..];
    }
}
