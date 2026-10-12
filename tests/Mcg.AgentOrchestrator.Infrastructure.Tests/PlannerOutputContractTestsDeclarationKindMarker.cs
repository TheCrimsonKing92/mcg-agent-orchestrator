using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test uses its own temporary filesystem root from the support fixture.
public sealed class PlannerOutputContractTestsDeclarationKindMarker : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("- `src/Mcg.AgentOrchestrator.Infrastructure/Onboarding/ManifestFactResultKind.cs` — new public enum.", "src/Mcg.AgentOrchestrator.Infrastructure/Onboarding/ManifestFactResultKind.cs")]
    [Xunit.InlineData("- `src/Missing.cs` — new enum", "src/Missing.cs")]
    [Xunit.InlineData("- `src/Missing.cs` — new result struct;", "src/Missing.cs")]
    [Xunit.InlineData("- `src/Missing.cs` — new public struct: contents follow", "src/Missing.cs")]
    public void DeclarationKindMarkers_MissingTargets_AreAccepted(string targetLine, string targetPath)
    {
        var workingDirectory = CreateTempDirectory();
        Xunit.Assert.False(File.Exists(Path.Combine(workingDirectory, targetPath)));
        var plan = PlanWithTargetLine(targetLine);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("- `src/Missing.cs` — new enum coverage for the parser.")]
    [Xunit.InlineData("- `src/Missing.cs` — new public enums.")]
    [Xunit.InlineData("- `src/Missing.cs` — enum:")]
    public void MalformedDeclarationMarkers_MissingTargets_AreRejected(string targetLine)
    {
        var workingDirectory = CreateTempDirectory();
        var plan = PlanWithTargetLine(targetLine);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("target citation 'src/Missing.cs' does not exist and is not marked as a new file", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("- Inspect `src/Missing/Absent.cs`.")]
    [Xunit.InlineData("- Inspect `src/Missing/Absent.cs` — new widget.")]
    public void UnmarkedOrUnknownKind_MissingTargets_AreRejected(string targetLine)
    {
        var workingDirectory = CreateTempDirectory();
        Xunit.Assert.False(File.Exists(Path.Combine(workingDirectory, "src/Missing/Absent.cs")));
        var plan = PlanWithTargetLine(targetLine);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("target citation 'src/Missing/Absent.cs' does not exist", result.Diagnostic, StringComparison.Ordinal);
    }

    private static string PlanWithTargetLine(string line)
    {
        var plan = PlannerContractPlanFixture().ReplaceLineEndings("\n");
        const string heading = "## Target seams and symbols";
        var bodyStart = plan.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var bodyEnd = plan.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        // Short markers still need a substantive section body; keep their suffix unchanged.
        var sectionLine = line.Length < 40 ? "Target seam: " + line : line;
        return plan[..bodyStart] + "\n\n" + sectionLine + "\n" + plan[bodyEnd..];
    }
}
