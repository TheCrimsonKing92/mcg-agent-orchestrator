using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PlannerOutputContractTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void PlannerContract_ExactLive485363d4OrderedIntegrationList_PassesWithoutSequenceKeywords()
    {
        var plan = ReadExactLiveFixture("485363d4-ba8e416a-20260805010032.out.txt");

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            InfrastructureTestSupport.FindRepositoryRoot());

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_NumberedIntegrationPlaceholders_Fail()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Integration seams",
            "1. TBD filler that only pads the section body.\n2. TBD filler that still supplies no substantive integration seam.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("must describe an integration sequence", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_NumberedIntegrationListWithSubstantiveLaterText_Passes()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Integration seams",
            "1. Persist the validated receipt so later roles consume the complete Planner evidence.\n2. Build the downstream context from that durable receipt and verify its exact content.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_ExactLive485363d4EmDashNewFileMarkers_Pass()
    {
        var plan = ReadExactLiveFixture("485363d4-ba8e416a-20260805011642.out.txt");

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            InfrastructureTestSupport.FindRepositoryRoot());

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_ExactLive658501ceParenthesizedNewFileMarker_Passes()
    {
        var plan = ReadExactLiveFixture("658501ce-f6708f44-20260805012800.out.txt");

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            InfrastructureTestSupport.FindRepositoryRoot());

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_ContextualHallucinatedSibling_Fails()
    {
        var targetBody =
            """
            - Extend `tests\Mcg.AgentOrchestrator.Infrastructure.Tests\CliCommandTests.PersistentRunnerCommands.cs`, `HallucinatedSibling.cs`, and `CliHelpTests.cs` with focused contract coverage.
            """;
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            InfrastructureTestSupport.FindRepositoryRoot());

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("target citation 'HallucinatedSibling.cs' does not exist", result.Diagnostic, StringComparison.Ordinal);
    }

    private static string ReplaceSectionBody(string plan, string heading, string replacement)
    {
        var normalized = plan.ReplaceLineEndings("\n");
        var bodyStart = normalized.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var nextHeading = normalized.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        return normalized[..bodyStart] + "\n\n" + replacement.Trim() + "\n" + normalized[nextHeading..];
    }

    private static string ReadExactLiveFixture(string fileName) =>
        File.ReadAllText(Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "Fixtures",
            "PlannerOutputContract",
            fileName));
}
