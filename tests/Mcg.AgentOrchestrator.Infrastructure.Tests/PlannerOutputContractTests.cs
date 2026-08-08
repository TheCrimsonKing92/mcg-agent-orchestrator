using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PlannerOutputContractTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void PlannerContract_ExactRejectedE5c18520NewStoreMarker_Passes()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "PlannerOutputContract",
            "e5c18520-316cfc5a-20260805154436.out.txt");
        var fixtureBytes = File.ReadAllBytes(fixturePath);
        Xunit.Assert.Equal(
            "86A0C693C22296BC7E6517EB7A36384B99057288E057237278F364B3B65844F1",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fixtureBytes)));
        var plan = System.Text.Encoding.UTF8.GetString(fixtureBytes);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            InfrastructureTestSupport.FindRepositoryRoot());

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_NewBehaviorSuffixDoesNotMarkMissingPathAsNewFile()
    {
        var workingDirectory = CreateTempDirectory();
        var targetBody = "- Extend `src/Missing.cs` — new behavior for the existing validation path.";
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("target citation 'src/Missing.cs' does not exist", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_SlashDelimitedProvenanceTextIsNotAPath()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var targetBody =
            "- Keep the provenance triple `cli / operator / local-process` explicit while extending `seed.txt`.";
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_ExactLive485363d4OrderedList_PassesWithoutSequenceKeywords()
    {
        var plan = ReadExactLiveFixture("485363d4-ba8e416a-20260805022806.out.txt");

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
    public void PlannerContract_MalformedNumberedIntegrationList_Fails()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Integration seams",
            "1. Persist the validated receipt so later roles consume complete Planner evidence.\n3. Build downstream context using that durable receipt and verify its exact content.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("must describe an integration sequence", result.Diagnostic, StringComparison.Ordinal);
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
    public void PlannerContract_ExactLive485363d4ContextualSiblings_Pass()
    {
        var plan = ReadExactLiveFixture("485363d4-ba8e416a-20260805010032.out.txt");

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
    public void PlannerContract_ContextualSameLineExistingSibling_Passes()
    {
        var targetBody =
            "- Extend `tests\\Mcg.AgentOrchestrator.Infrastructure.Tests\\CliCommandTests.PersistentRunnerCommands.cs` and `CliHelpTests.cs` with focused contract coverage.";
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            InfrastructureTestSupport.FindRepositoryRoot());

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_ContextualDirectoryResetsOnNewlineAndHallucinatedSibling_Fails()
    {
        var targetBody =
            """
            - Extend `tests\Mcg.AgentOrchestrator.Infrastructure.Tests\CliCommandTests.PersistentRunnerCommands.cs` with focused contract coverage.
            - Extend `HallucinatedSibling.cs` with a later-bullet negative control.
            """;
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            InfrastructureTestSupport.FindRepositoryRoot());

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("target citation 'HallucinatedSibling.cs' does not exist", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_ActiveContextDoesNotUseHistoricalSameBasenameFallback()
    {
        var workingDirectory = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "prior"));
        Directory.CreateDirectory(Path.Combine(workingDirectory, "active"));
        File.WriteAllText(Path.Combine(workingDirectory, "prior", "Sibling.cs"), "prior");
        File.WriteAllText(Path.Combine(workingDirectory, "active", "Anchor.cs"), "anchor");
        var plan = PlannerContractPlanFixture().Replace(
            PlannerContractAcceptanceMappingBody,
            "1. Preserve the earlier explicit citation `prior/Sibling.cs` as historical evidence for the negative control.",
            StringComparison.Ordinal);
        plan = ReplaceSectionBody(
            plan,
            "## Target seams and symbols",
            "- Extend `active/Anchor.cs` and `Sibling.cs` with focused contract coverage.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("target citation 'Sibling.cs' does not exist", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_UnmarkedNonexistentPath_Fails()
    {
        var workingDirectory = CreateTempDirectory();
        var targetBody = "- Extend `src/MissingUnmarked.cs` with focused contract coverage.";
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("target citation 'src/MissingUnmarked.cs' does not exist", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_NewFileSuffixMarksOnlyAdjacentPath()
    {
        var workingDirectory = CreateTempDirectory();
        var targetBody =
            "- Extend `src/MissingUnmarked.cs` and `src/MissingMarked.cs` — new file with focused contract coverage.";
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("target citation 'src/MissingUnmarked.cs' does not exist", result.Diagnostic, StringComparison.Ordinal);
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
            AppContext.BaseDirectory,
            "Fixtures",
            "PlannerOutputContract",
            fileName));
}
