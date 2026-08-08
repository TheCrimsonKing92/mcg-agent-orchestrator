using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PlannerOutputContractTests : WorkerDispatchTestSupport
{
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

    [Xunit.Fact]
    public void PlannerContract_AcceptsEveryNewFileFormPublishedToThePlanner()
    {
        // Couples the grammar published in AgentOutputDirectives.WorkerResultTemplateLinesForRole(Planner)
        // to the grammar this contract enforces. Goal e5c18520 lost three paid Planner rounds writing
        // "— new SQLite-backed cross-process store", which declares newness but is not an accepted form.
        // If the regexes change, the directive must change with them, and this test is what says so.
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var targetBody = string.Join(
            "\n",
            "- Add `src/MarkedByPrefixKeyword.cs` to own the durable claim records.",
            "- Introduce `src/MarkedByParenSuffix.cs` (new file) for the ownership seam.",
            "- Introduce `src/MarkedByDashSuffix.cs` — new file for the reconciliation seam.");
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_PublishedDirectiveStatesTheAcceptedNewFileForms()
    {
        var directive = string.Join(
            "\n",
            Mcg.AgentOrchestrator.Core.AgentOutputDirectives.WorkerResultTemplateLinesForRole(
                Mcg.AgentOrchestrator.Core.AgentRole.Planner));

        // The enforced suffix regex accepts only these two tokens, and the em dash is U+2014.
        Xunit.Assert.Contains("(new file)", directive, StringComparison.Ordinal);
        Xunit.Assert.Contains("— new file", directive, StringComparison.Ordinal);
        // A single colon before a symbol is not recognised by CitedFilePath; the double colon is.
        Xunit.Assert.Contains("::", directive, StringComparison.Ordinal);
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
