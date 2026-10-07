using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test uses its own temporary filesystem root from the support fixture.
public sealed class PlannerOutputContractTestsDescriptiveFileMarker : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("- `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.FailureCauseAdjudication.cs` — new owner file:")]
    [Xunit.InlineData("- `src/Missing.cs` — new owner file.")]
    [Xunit.InlineData("- `src/Missing.cs` — new test file")]
    [Xunit.InlineData("- `src/Missing.cs` — new policy class: contents follow")]
    public void DescriptiveFileMarkersPermitMissingTargets(string targetLine)
    {
        var workingDirectory = CreateTempDirectory();
        var plan = PlanWithTargetLine(targetLine);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("- `src/Missing.cs` — new owner file coverage for the parser.")]
    [Xunit.InlineData("- `src/Missing.cs` — owner file:")]
    [Xunit.InlineData("- `src/Missing.cs` — new behavior: see below")]
    public void NearMarkersDoNotExcuseMissingTargets(string targetLine)
    {
        var workingDirectory = CreateTempDirectory();
        var plan = PlanWithTargetLine(targetLine);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("src/Missing.cs", result.Diagnostic, StringComparison.Ordinal);
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
