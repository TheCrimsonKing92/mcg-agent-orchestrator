using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PlannerOutputContractTests : WorkerDispatchTestSupport
{
    private const string E5c18520FixtureName = "e5c18520-316cfc5a-20260805154436.out.txt";
    private static readonly string[] E5c18520AcceptanceCriteria =
    [
        "Add the real-process handoff regression.",
        "Rebuild occupied logical slots from durable claims.",
        "Persist and classify process identity.",
        "Emit adoption and wait events.",
        "Integrate adoption into the max-duration handoff path."
    ];

    [Xunit.Fact]
    public void PlannerContract_ExactRejectedE5c18520NewStoreMarker_Passes()
    {
        var fixtureBytes = ReadCanonicalLiveFixtureBytes(E5c18520FixtureName);
        Xunit.Assert.Equal(
            "86A0C693C22296BC7E6517EB7A36384B99057288E057237278F364B3B65844F1",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fixtureBytes)));
        var plan = System.Text.Encoding.UTF8.GetString(fixtureBytes);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            InfrastructureTestSupport.FindRepositoryRoot(),
            acceptanceCriteria: E5c18520AcceptanceCriteria);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_ExactRejectedE5c18520WithoutArtifactKind_ReproducesRejection()
    {
        var plan = ReadExactLiveFixture(E5c18520FixtureName).Replace(
            "`src/Mcg.AgentOrchestrator.Infrastructure/Persistence/AcceptanceOwnershipStore.cs` — new SQLite-backed cross-process store.",
            "`src/Mcg.AgentOrchestrator.Infrastructure/Persistence/AcceptanceOwnershipStore.cs` — new behavior for cross-process claims.",
            StringComparison.Ordinal);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            InfrastructureTestSupport.FindRepositoryRoot(),
            acceptanceCriteria: E5c18520AcceptanceCriteria);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("AcceptanceOwnershipStore.cs", result.Diagnostic, StringComparison.Ordinal);
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
    public void PlannerContract_ArtifactKindFollowedByDescriptionDoesNotMarkMissingPathAsNewFile()
    {
        var workingDirectory = CreateTempDirectory();
        var targetBody = "- Extend `src/Missing.cs` — new test coverage for the parser.";
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

    [Xunit.Fact]
    public void PlannerContract_ExtensionlessCsCitationResolvesAndRetainsAuditNote()
    {
        var workingDirectory = CreateTempDirectory();
        var sourceDirectory = Path.Combine(workingDirectory, "src");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "ExistingTarget.cs"), "// fixture");
        var rawCitation = "src/ExistingTarget";
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            $"- Extend `{rawCitation}` with focused resolver coverage and preserve its downstream file contract.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains("`src/ExistingTarget.cs`", result.Plan, StringComparison.Ordinal);
        Xunit.Assert.Contains(
            "raw citation `src/ExistingTarget` resolved to `src/ExistingTarget.cs`",
            result.Plan,
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_RelativeCitationIgnoresWorkingDirectoryPrefixCasing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var workingDirectory = CreateTempDirectory();
        var sourceDirectory = Path.Combine(workingDirectory, "src");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "ExistingTarget.cs"), "// fixture");
        var perturbedWorkingDirectory = new string(workingDirectory.Select(character =>
            char.IsLetter(character)
                ? char.IsUpper(character) ? char.ToLowerInvariant(character) : char.ToUpperInvariant(character)
                : character).ToArray());
        Xunit.Assert.False(string.Equals(workingDirectory, perturbedWorkingDirectory, StringComparison.Ordinal));
        Xunit.Assert.True(Directory.Exists(perturbedWorkingDirectory));
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            "- Extend `src/ExistingTarget.cs` with focused resolver coverage.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, perturbedWorkingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_LiveRoundFiveExtensionlessCitationResolves()
    {
        const string receiptCitation = "tests/Mcg.AgentOrchestrator.Core.Tests/DispatchOutcomeClassifyTests";
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        Xunit.Assert.True(
            File.Exists(Path.Combine(repositoryRoot, receiptCitation.Replace('/', Path.DirectorySeparatorChar) + ".cs")),
            "The live receipt file moved; update this receipt-pinned test independently of the resolver rule.");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            $"- Extend `{receiptCitation}` with focused classification coverage for the verified Planner rejection receipt.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, repositoryRoot);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains($"`{receiptCitation}.cs`", result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_ExtensionlessMissingCitationStillFails()
    {
        var workingDirectory = CreateTempDirectory();
        var citation = "src/GenuinelyMissingTarget";
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            $"- Extend `{citation}` with focused negative-control coverage for nonexistent targets.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains($"target citation '{citation}' does not exist", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Offending citation: '{citation}'", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_CaseMismatchedCsCandidateSuggestsButDoesNotResolve()
    {
        var workingDirectory = CreateTempDirectory();
        var sourceDirectory = Path.Combine(workingDirectory, "src");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "ExactCase.cs"), "// fixture");
        var citation = "src/exactcase";
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            $"- Extend `{citation}` with focused case-sensitive resolution coverage for cross-platform consistency.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("Did you mean `src/ExactCase.cs`?", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Offending citation: '{citation}'", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_LiteralPathWinsWhenCsCandidateAlsoExists()
    {
        var workingDirectory = CreateTempDirectory();
        var sourceDirectory = Path.Combine(workingDirectory, "src");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "LiteralTarget"), "literal fixture");
        File.WriteAllText(Path.Combine(sourceDirectory, "LiteralTarget.cs"), "suffixed fixture");
        const string citation = "src/LiteralTarget";
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            $"- Extend `{citation}` with focused literal-first resolution coverage.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains($"`{citation}`", result.Plan, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Planner contract note", result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_RejectionIsCompleteInRoundAndRecordedLog()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-09T03:07:55Z"));
        var citation = "tests/Mcg.AgentOrchestrator.Core.Tests/" + new string('Q', 600);
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            $"- Extend `{citation}` with focused rejection-recovery coverage for the stored Planner round.");
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root,
            Mcg.AgentOrchestrator.Core.AgentRole.Planner,
            plan,
            new string('x', Mcg.AgentOrchestrator.Core.VerificationTextBounds.BoundThreshold * 2),
            clock);

        var runner = new BackgroundDispatchRunner(clock, isStillRunning: _ => false);
        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);
        for (var reconciliation = 0; reconciliation < 5; reconciliation++)
        {
            runner.RefreshLatestProcess(kernel, goal.Id, task.Id);
        }

        var expected = $"Offending citation: '{citation}'";
        Xunit.Assert.Contains(expected, task.LastVerification!.StandardError, StringComparison.Ordinal);
        Xunit.Assert.Contains(
            expected,
            Mcg.AgentOrchestrator.App.Rendering.OutputTextPreview
                .CreateVerificationLog(task.LastVerification.StandardError, task.LastVerification.StandardErrorPath)
                .Text,
            StringComparison.Ordinal);
        var rejectionLog = File.ReadAllText(process.StandardErrorPath);
        Xunit.Assert.Contains(expected, rejectionLog, StringComparison.Ordinal);
        const string rejectionMarker = "[orchestrator Planner output contract rejection]";
        Xunit.Assert.Equal(1, rejectionLog.Split(rejectionMarker, StringSplitOptions.None).Length - 1);
    }

    [Xunit.Fact]
    public void PlannerContract_PublishedDirectiveStatesDescriptiveArtifactKindForm()
    {
        var directive = string.Join(
            "\n",
            Mcg.AgentOrchestrator.Core.AgentOutputDirectives.WorkerResultTemplateLinesForRole(
                Mcg.AgentOrchestrator.Core.AgentRole.Planner));

        Xunit.Assert.Contains("up to three descriptive words", directive, StringComparison.Ordinal);
        Xunit.Assert.Contains("artifact-kind noun", directive, StringComparison.Ordinal);
        Xunit.Assert.Contains("period, semicolon, or the end of the line", directive, StringComparison.Ordinal);
        Xunit.Assert.Contains("stdout is authoritative", directive, StringComparison.Ordinal);
        Xunit.Assert.Contains("writing a separate artifact is not required", directive, StringComparison.Ordinal);
        Xunit.Assert.Contains("disposition=undecidable", directive, StringComparison.Ordinal);
        Xunit.Assert.Contains("PLANNER_EVIDENCE_REQUEST:", directive, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_AcceptsMixedPlannedAndUndecidableCriterionMappings()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Acceptance criteria mapping",
            """
            1. disposition=undecidable; would-settle=the original wrapper diagnostic; required-source=the historical wrapper process; unavailable-because=the process exited without recording it
            2. disposition=planned; plan=Implement the remaining behavior through the existing durable receipt and verify its downstream projection.
            """);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            workingDirectory,
            acceptanceCriteria: ["Recover the historical cause.", "Implement the remaining behavior."]);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains("disposition=undecidable", result.Plan, StringComparison.Ordinal);
        Xunit.Assert.Contains("2. disposition=planned", result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("1. disposition=undecidable; required-source=wrapper; unavailable-because=never recorded", "would-settle")]
    [Xunit.InlineData("1. disposition=undecidable; would-settle=diagnostic; unavailable-because=never recorded", "required-source")]
    [Xunit.InlineData("1. disposition=planned", "plan")]
    public void PlannerContract_RejectsIncompleteDispositionMappings(string mapping, string missingField)
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Acceptance criteria mapping", mapping);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            workingDirectory,
            acceptanceCriteria: ["Map the criterion."]);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains(missingField, result.Diagnostic, StringComparison.Ordinal);
    }

    private static string ReplaceSectionBody(string plan, string heading, string replacement)
    {
        var normalized = plan.ReplaceLineEndings("\n");
        var bodyStart = normalized.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var nextHeading = normalized.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        return normalized[..bodyStart] + "\n\n" + replacement.Trim() + "\n" + normalized[nextHeading..];
    }

    private static string PlannerFixturePath(string fileName) =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "PlannerOutputContract",
            fileName);

    private static byte[] ReadCanonicalLiveFixtureBytes(string fileName) =>
        System.Text.Encoding.UTF8.GetBytes(
            File.ReadAllText(PlannerFixturePath(fileName)).ReplaceLineEndings("\n"));

    private static string ReadExactLiveFixture(string fileName) =>
        File.ReadAllText(PlannerFixturePath(fileName));
}
