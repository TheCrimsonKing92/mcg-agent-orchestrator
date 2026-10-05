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
    public void PlannerContract_ArchivedE5c18520NewStoreMarker_Passes()
    {
        var plan = ReadCanonicalArchivedFixture(
            E5c18520FixtureName,
            "86A0C693C22296BC7E6517EB7A36384B99057288E057237278F364B3B65844F1");

        var succeeded = PlannerOutputContract.TryValidate(
            plan,
            out _,
            out var diagnostic,
            acceptanceCriteria: E5c18520AcceptanceCriteria);

        Xunit.Assert.True(succeeded, diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_LiveNewBehaviorWithoutArtifactKind_IsRejected()
    {
        var workingDirectory = CreateTempDirectory();
        var targetBody =
            "- Extend `src/AcceptanceOwnershipStore.cs` — new behavior for cross-process claims.";
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains(
            "target citation 'src/AcceptanceOwnershipStore.cs' does not exist",
            result.Diagnostic,
            StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("- Extend `src/NewStore.cs` — new durable ownership store.")]
    [Xunit.InlineData("- Extend `src/NewFixture.cs` (new file) with focused contract coverage.")]
    [Xunit.InlineData("- Create `src/CreatedDocument.md` with the verification receipt.")]
    [Xunit.InlineData("- Add new file `src/AddedScript.ps1` for the verification workflow.")]
    public void PlannerContract_NewFileMarkerVariantsPermitMissingTargets(string targetBody)
    {
        var workingDirectory = CreateTempDirectory();
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_WholeFileMoveWithCommaNote_Passes()
    {
        var workingDirectory = CreateTempDirectory();
        var sourcePath = Path.Combine(
            workingDirectory, "src", "Mcg.AgentOrchestrator.Infrastructure", "Workspaces",
            "RepositorySourceInventory.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllText(sourcePath, "// Existing move source.");
        var targetBody =
            "- **Whole-file move:** `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RepositorySourceInventory.cs` → `src/Mcg.AgentOrchestrator.Infrastructure/Workers/RepositorySourceInventory.cs` (new file, byte-identical).";
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_NewPolicyClassWithCommaClause_Passes()
    {
        var workingDirectory = CreateTempDirectory();
        var targetBody =
            "- `src/Mcg.AgentOrchestrator.Core/Application/DispatchAdmissionPolicy.cs` — new policy class, in namespace `Mcg.AgentOrchestrator.Core`. Contents:";
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData(
        "- **`src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/SourceSizeRatchet.cs`**: the row at line 136 and a new row placed after the `ConductEvents.cs` row. A rough estimate is about 2690 lines for `ConductorBatchLoop.cs` and about 750 for the new file. The Developer must use the measured values.",
        "ConductEvents.cs",
        new string[] { "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/SourceSizeRatchet.cs", "ConductorBatchLoop.cs" })]
    [Xunit.InlineData(
        "- **Fact.** `FindFocusedEvidenceClassFiles` (line 370) walks every `*.cs` under the project directory, skipping `bin` and `obj`, and parses each file with Roslyn (`ParseCSharpRoot`, line 401).",
        "*.cs",
        new string[] { })]
    public void PlannerContract_UnmarkedLiveCitations_AreRejected(
        string targetBody, string offendingCitation, string[] existingPaths)
    {
        var workingDirectory = CreateTempDirectory();
        foreach (var relativePath in existingPaths)
        {
            var path = Path.Combine(workingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "// Existing cited file.");
        }
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains(offendingCitation, result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("- Extend `src/Missing.cs` (new filter, see below).", "src/Missing.cs")]
    [Xunit.InlineData("- Extend `src/Missing.cs` — new test coverage, for the parser.", "src/Missing.cs")]
    [Xunit.InlineData("- Extend `src/Missing.cs` (new file behavior) for parsing.", "src/Missing.cs")]
    [Xunit.InlineData(
        "- Extend `src/MissingUnmarked.cs` and `src/MissingMarked.cs` (new file, empty).",
        "src/MissingUnmarked.cs")]
    public void PlannerContract_NearMarkersDoNotExcuseMissingPaths(
        string targetBody, string offendingCitation)
    {
        var workingDirectory = CreateTempDirectory();
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains(offendingCitation, result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("(new file, byte-identical)")]
    [Xunit.InlineData("(new file; generated from the existing source)")]
    [Xunit.InlineData("(new file: focused parser coverage)")]
    public void PlannerContract_ParenthesizedNotesPermitMissingTargets(string marker)
    {
        var workingDirectory = CreateTempDirectory();
        var targetBody = $"- Extend `src/Missing.cs` {marker}.";
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("(new file,)")]
    [Xunit.InlineData("(new file;   )")]
    [Xunit.InlineData("(new file:\t)")]
    [Xunit.InlineData("(file, byte-identical)")]
    [Xunit.InlineData("— policy class, in the existing namespace")]
    public void PlannerContract_EmptyNotesOrMissingNew_AreRejected(string marker)
    {
        var workingDirectory = CreateTempDirectory();
        var targetBody = $"- Extend `src/Missing.cs` {marker}.";
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("src/Missing.cs", result.Diagnostic, StringComparison.Ordinal);
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
    public void PlannerContract_Archived485363d4OrderedList_PassesWithoutSequenceKeywords()
    {
        var plan = ReadCanonicalArchivedFixture(
            "485363d4-ba8e416a-20260805022806.out.txt",
            "DF5A804E3066F2C7895D6CC0961CCD3E28A44DBDA2751FBB5D19C38910FF4A99");

        var succeeded = PlannerOutputContract.TryValidate(plan, out _, out var diagnostic);

        Xunit.Assert.True(succeeded, diagnostic);
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
        Xunit.Assert.Contains("sequential numbered list", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_NumberedIntegrationLineWithSubstantiveProse_Passes()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Integration seams",
            "1. Persist the shared substance predicate.\nThe premise, ownership, external, and stop sections then route through it before dispatch conversion runs.");

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
            "1. Persist receipt evidence here.\n3. Persist receipt evidence there.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("sequential numbered list", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_IntegrationPlaceholderWithinSubstantiveSentence_Passes()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Integration seams",
            "1. Persist the shared substance predicate so every section routes through it before dispatch conversion runs.\n2. Confirm none newly flags or newly clears a claude-cli profile now that the default carries the placeholder.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_NumberedIntegrationPlaceholderOnlyItems_Fail()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Integration seams",
            "1. placeholder\n2. placeholder");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("integration seams", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("placeholder")]
    [Xunit.InlineData("placeholder.")]
    [Xunit.InlineData("1. Placeholder.")]
    [Xunit.InlineData("1) PLACEHOLDER:;,!?")]
    [Xunit.InlineData("- placeholder")]
    [Xunit.InlineData("* placeholder.")]
    [Xunit.InlineData("\\u2022 placeholder:")]
    [Xunit.InlineData(" \t1. placeholder. \t")]
    public void PlannerContract_IntegrationWholeLinePlaceholderWithSubstantiveCompanion_Fails(string markerLine)
    {
        // Expand the bullet during execution so discovery keeps its JSON display name ASCII.
        markerLine = markerLine.Replace("\\u2022", "\u2022", StringComparison.Ordinal);
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Integration seams",
            markerLine + "\n2. Persist the shared substance predicate so every section routes through it before dispatch conversion runs.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("integration seams", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("sequential numbered list", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("TODO")]
    [Xunit.InlineData("TBD")]
    public void PlannerContract_IntegrationAcronymWithinSubstantiveProse_Fails(string marker)
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Integration seams",
            $"Persist the shared substance predicate with {marker} before every section routes through dispatch conversion and receipt validation.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("integration seams", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("sequential numbered list", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("## Premise validity", "valid valid valid valid valid valid valid valid valid valid")]
    [Xunit.InlineData("## Ownership and lifecycle", "owns owns owns owns owns owns owns owns owns owns")]
    [Xunit.InlineData("## External and edge contracts", "timeout timeout timeout timeout timeout timeout timeout timeout")]
    [Xunit.InlineData("## Integration seams", "then then then then then then then then then then")]
    [Xunit.InlineData("## Risks and stop conditions", "stop stop stop stop stop stop stop stop stop stop")]
    [Xunit.InlineData("## Premise validity", "1 22 333 4444 55555 666666 7777777 88888888")]
    public void PlannerContract_MarkerOnlySectionBodies_Fail(string heading, string body)
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), heading, body);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            workingDirectory,
            acceptanceCriteria: ["Implement the behavior."]);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("at least 8 distinct words", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_Archived485363d4EmDashNewFileMarkers_Pass()
    {
        var plan = ReadCanonicalArchivedFixture(
            "485363d4-ba8e416a-20260805011642.out.txt",
            "D34219B3D84C1DB41A03F69A4A6056A32744C4B459201E52B04D8D1BD5279210");

        var succeeded = PlannerOutputContract.TryValidate(plan, out _, out var diagnostic);

        Xunit.Assert.True(succeeded, diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_Archived485363d4ContextualSiblings_Pass()
    {
        var plan = ReadCanonicalArchivedFixture(
            "485363d4-ba8e416a-20260805010032.out.txt",
            "6DBB456263999A69D0A6CDA8216A413A8DE27C3C2236D4D8BE211218E2B7A4BA");

        var succeeded = PlannerOutputContract.TryValidate(plan, out _, out var diagnostic);

        Xunit.Assert.True(succeeded, diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_Archived658501ceParenthesizedNewFileMarker_Passes()
    {
        var plan = ReadCanonicalArchivedFixture(
            "658501ce-f6708f44-20260805012800.out.txt",
            "55A29354C46758800777C6332EE4E7D8F760DF216F29ED620C6A4DC0FFB3F6D9");

        var succeeded = PlannerOutputContract.TryValidate(plan, out _, out var diagnostic);

        Xunit.Assert.True(succeeded, diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_ContextualSameLineExistingSibling_Passes()
    {
        var targetBody =
            "- Extend `tests\\Mcg.AgentOrchestrator.Infrastructure.Tests\\CliCommandTestsPersistentRunnerCommands.cs` and `CliHelpTests.cs` with focused contract coverage.";
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
            - Extend `tests\Mcg.AgentOrchestrator.Infrastructure.Tests\CliCommandTestsPersistentRunnerCommands.cs` with focused contract coverage.
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
        Directory.CreateDirectory(Path.Combine(workingDirectory, "active"));
        // Sibling.cs stays absent: inheriting the historical new-file marker under active context would incorrectly accept it.
        File.WriteAllText(Path.Combine(workingDirectory, "active", "Anchor.cs"), "anchor");
        var plan = PlannerContractPlanFixture().Replace(
            PlannerContractAcceptanceMappingBody,
            "1. Preserve the earlier explicit citation `prior/Sibling.cs` (new file) as historical evidence for the negative control.",
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
    public void PlannerContract_ExtensionlessMissingCitationPassesThroughAsProse()
    {
        var workingDirectory = CreateTempDirectory();
        var citation = "src/GenuinelyMissingTarget";
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            $"- Extend `{citation}` with focused negative-control coverage for nonexistent targets.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains($"`{citation}`", result.Plan, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Planner contract note", result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_UnresolvableProseSpansPassThroughUntouched()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            "- Keep `seed.txt` as the concrete seam, preserve `try/finally` behavior, and retain the `archive/` directory reference.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains("`try/finally`", result.Plan, StringComparison.Ordinal);
        Xunit.Assert.Contains("`archive/`", result.Plan, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Planner contract note", result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_BareUniqueRepositoryFilenameResolvesWithoutPrecedingFullPath()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        const string filename = "GoalOperationJournal.cs";
        var fixtureMatches = FindNonExcludedRepositoryFixtureFiles(repositoryRoot, filename);
        Xunit.Assert.True(
            fixtureMatches.Length == 1,
            $"Expected exactly one non-excluded '{filename}' fixture, found {fixtureMatches.Length}; re-pin this test to a real unique source filename.");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            $"- Extend `{filename}` with focused citation-resolution coverage.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, repositoryRoot);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains($"`{filename}`", result.Plan, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Planner contract note", result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_BareUniqueFilenameResolvesInIsolatedRepository()
    {
        var workingDirectory = CreateTempDirectory();
        var nestedDirectory = Path.Combine(workingDirectory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(Path.Combine(nestedDirectory, "UniqueTarget.cs"), "// fixture");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            "- Extend `UniqueTarget.cs` with focused citation-resolution coverage.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_ExistingDirectoryProvidesSameLineContextForBareFilename()
    {
        var workingDirectory = CreateTempDirectory();
        var contextualDirectory = Path.Combine(workingDirectory, "src", "feature");
        Directory.CreateDirectory(contextualDirectory);
        File.WriteAllText(Path.Combine(contextualDirectory, "Sibling.cs"), "// fixture");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            "- Inspect `src/feature` and extend same-line `Sibling.cs` with contextual resolution coverage.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_ContextualMissFallsBackToUniqueRepositoryFile()
    {
        var workingDirectory = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "active"));
        Directory.CreateDirectory(Path.Combine(workingDirectory, "elsewhere"));
        File.WriteAllText(Path.Combine(workingDirectory, "active", "Anchor.cs"), "// anchor");
        File.WriteAllText(Path.Combine(workingDirectory, "elsewhere", "ContextMissTarget.cs"), "// target");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            "- Extend `active/Anchor.cs` and `ContextMissTarget.cs` with focused fallback coverage.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains("`ContextMissTarget.cs`", result.Plan, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Planner contract note", result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_ContextualMissWithAmbiguousRepositoryFilenameFails()
    {
        var workingDirectory = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "active"));
        Directory.CreateDirectory(Path.Combine(workingDirectory, "first"));
        Directory.CreateDirectory(Path.Combine(workingDirectory, "second"));
        File.WriteAllText(Path.Combine(workingDirectory, "active", "Anchor.cs"), "// anchor");
        File.WriteAllText(Path.Combine(workingDirectory, "first", "ContextDuplicate.cs"), "// first");
        File.WriteAllText(Path.Combine(workingDirectory, "second", "ContextDuplicate.cs"), "// second");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            "- Extend `active/Anchor.cs` and `ContextDuplicate.cs` with focused ambiguity coverage.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("target citation 'ContextDuplicate.cs' is ambiguous", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("'first/ContextDuplicate.cs'", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("'second/ContextDuplicate.cs'", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("Offending citation: 'ContextDuplicate.cs'", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_ContextualMissWithNoRepositoryMatchStillFails()
    {
        var workingDirectory = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "active"));
        File.WriteAllText(Path.Combine(workingDirectory, "active", "Anchor.cs"), "// anchor");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            "- Extend `active/Anchor.cs` and `AbsentContextTarget.cs` with focused rejection coverage.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains(
            "target citation 'AbsentContextTarget.cs' does not exist",
            result.Diagnostic,
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_ContextualMatchWinsOverRepositoryAmbiguity()
    {
        var workingDirectory = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "active"));
        Directory.CreateDirectory(Path.Combine(workingDirectory, "elsewhere"));
        File.WriteAllText(Path.Combine(workingDirectory, "active", "Anchor.cs"), "// anchor");
        File.WriteAllText(Path.Combine(workingDirectory, "active", "Preferred.cs"), "// contextual");
        File.WriteAllText(Path.Combine(workingDirectory, "elsewhere", "Preferred.cs"), "// repository match");
        var contextualPlan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            "- Extend `active/Anchor.cs` and `Preferred.cs` with focused precedence coverage.");
        var repositoryPlan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            """
            - Extend `active/Anchor.cs` with focused precedence coverage.
            - Extend `Preferred.cs` with repository ambiguity coverage.
            """);

        var contextualResult = PlannerOutputContract.Resolve(contextualPlan, string.Empty, workingDirectory);
        var repositoryResult = PlannerOutputContract.Resolve(repositoryPlan, string.Empty, workingDirectory);

        Xunit.Assert.True(contextualResult.Succeeded, contextualResult.Diagnostic);
        Xunit.Assert.False(repositoryResult.Succeeded);
        Xunit.Assert.Contains("target citation 'Preferred.cs' is ambiguous", repositoryResult.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_AmbiguousBareFilenameFailsWithCandidateDiagnostic()
    {
        var workingDirectory = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "first"));
        Directory.CreateDirectory(Path.Combine(workingDirectory, "second"));
        File.WriteAllText(Path.Combine(workingDirectory, "first", "Duplicate.cs"), "// first");
        File.WriteAllText(Path.Combine(workingDirectory, "second", "Duplicate.cs"), "// second");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            "- Extend `Duplicate.cs` with focused ambiguity coverage.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("target citation 'Duplicate.cs' is ambiguous", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("'first/Duplicate.cs'", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("'second/Duplicate.cs'", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("Offending citation: 'Duplicate.cs'", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_ContextualMissPrefersRepositoryRootFile()
    {
        var workingDirectory = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "docs", "incidents"));
        Directory.CreateDirectory(Path.Combine(workingDirectory, "tests", "Sample"));
        File.WriteAllText(Path.Combine(workingDirectory, "README.md"), "root readme");
        File.WriteAllText(Path.Combine(workingDirectory, "docs", "cli-reference.md"), "cli reference");
        File.WriteAllText(Path.Combine(workingDirectory, "docs", "incidents", "README.md"), "incident readme");
        File.WriteAllText(Path.Combine(workingDirectory, "tests", "Sample", "README.md"), "test readme");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            "- Inspect `docs/cli-reference.md` and `README.md` for the documented contract.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains("`README.md`", result.Plan, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Planner contract note", result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_CommandLinesAndAngleBracketPatternsAreProse()
    {
        var workingDirectory = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "src"));
        File.WriteAllText(Path.Combine(workingDirectory, "src", "Present.cs"), "// fixture");
        var targetBody = """
            - Inspect `src/Present.cs` for the concrete implementation seam.
            - Verify with `.\scripts\Invoke-TestSummary.ps1 -Target tests\...\*.Tests.csproj`.
            - Restore permissions with `icacls /reset /T /C /Q`.
            - Observe the runtime artifact `waiters/gate-<pid>-<guid>.json`.
            """.ReplaceLineEndings("\n");
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains(targetBody, result.Plan, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Planner contract note", result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("src/Absent/Missing.cs")]
    [Xunit.InlineData("*.cs")]
    [Xunit.InlineData("tests/**/*.csproj")]
    [Xunit.InlineData("src/Absent/Missing.cs::Some Method")]
    [Xunit.InlineData("src/Absent/Missing.cs::Method<T>")]
    public void PlannerContract_WildcardAndAbsentPathsStillFailAsMissing(string citation)
    {
        var workingDirectory = CreateTempDirectory();
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            $"- Inspect `{citation}` for the concrete implementation seam.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains($"target citation '{citation}' does not exist", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Offending citation: '{citation}'", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("src/Some Missing.cs")]
    [Xunit.InlineData("src/Some\tMissing.cs")]
    [Xunit.InlineData("src/Some\u00a0Missing.cs")]
    [Xunit.InlineData("src/<Missing.cs")]
    [Xunit.InlineData("src/Missing>.cs")]
    public void PlannerContract_WhitespaceOrAngleBracketInExtractedPathIsProse(string citation)
    {
        var workingDirectory = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "src"));
        File.WriteAllText(Path.Combine(workingDirectory, "src", "Present.cs"), "// fixture");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            $"- Inspect `src/Present.cs` and describe `{citation}` as prose.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains($"`{citation}`", result.Plan, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Planner contract note", result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("src/Present.cs::Some Method")]
    [Xunit.InlineData("src/Present.cs::Method<T>")]
    [Xunit.InlineData(" src/Present.cs ")]
    public void PlannerContract_SymbolSuffixOrSurroundingSpacePreservesFileCitation(string citation)
    {
        var workingDirectory = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "src"));
        File.WriteAllText(Path.Combine(workingDirectory, "src", "Present.cs"), "// fixture");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            $"- Inspect `{citation}` for the concrete implementation seam.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains($"`{citation}`", result.Plan, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Planner contract note", result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void PlannerContract_ContextualSuffixPrecedesRootFallback(bool hasContextualSuffix)
    {
        var workingDirectory = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "active"));
        File.WriteAllText(Path.Combine(workingDirectory, "active", "Anchor.cs"), "// anchor");
        var rootFilename = hasContextualSuffix ? "RootTarget.md" : "RootTarget.md.cs";
        File.WriteAllText(Path.Combine(workingDirectory, rootFilename), "// root target");
        if (hasContextualSuffix)
        {
            File.WriteAllText(Path.Combine(workingDirectory, "active", "RootTarget.md.cs"), "// contextual target");
        }

        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            "- Inspect `active/Anchor.cs` and `RootTarget.md` for the concrete implementation seam.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains("Inspect `active/Anchor.cs` and `RootTarget.md.cs`", result.Plan, StringComparison.Ordinal);
        Xunit.Assert.Contains("raw citation `RootTarget.md` resolved to `RootTarget.md.cs`", result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_LineRangeSuffixResolvesAgainstExistingFile()
    {
        var workingDirectory = CreateTempDirectory();
        var sourceDirectory = Path.Combine(workingDirectory, "src");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "RangeTarget.cs"), "// fixture");
        const string citation = "src/RangeTarget.cs:10-20";
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            $"- Extend `{citation}` with focused line-range coverage.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains($"`{citation}`", result.Plan, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Planner contract note", result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_ExcludedTreeDoesNotCreateFalseAmbiguity()
    {
        var workingDirectory = CreateTempDirectory();
        foreach (var directory in new[] { "source", "bin", ".state" })
        {
            Directory.CreateDirectory(Path.Combine(workingDirectory, directory));
            File.WriteAllText(Path.Combine(workingDirectory, directory, "VisibleTarget.cs"), "// fixture");
        }

        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            "- Extend `VisibleTarget.cs` with focused exclusion coverage.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
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
        var citation = "tests/Mcg.AgentOrchestrator.Core.Tests/" + new string('Q', 600) + ".cs";
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
            Mcg.AgentOrchestrator.App.Application.OutputTextPreview
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
    [Xunit.InlineData("**Criterion 1** - mapping text", 1, "mapping text")]
    [Xunit.InlineData("**Criterion 1 (current objective) - mapping text**", 1, "mapping text")]
    [Xunit.InlineData("**Criterion 1** - second collection sharing the fixture", 1, "second collection sharing the fixture")]
    [Xunit.InlineData("**Criterion 1 (current objective) - repository-wide audit", 1, "repository-wide audit")]
    [Xunit.InlineData("**Criterion 1 [current objective] - mapping text**", 1, "mapping text")]
    [Xunit.InlineData("1. mapping", 1, "mapping")]
    [Xunit.InlineData("- 1. mapping", 1, "mapping")]
    [Xunit.InlineData("1 maps to mapping", 1, "mapping")]
    [Xunit.InlineData("1 - mapping", 1, "mapping")]
    [Xunit.InlineData("1 covers mapping", 1, "mapping")]
    [Xunit.InlineData("Criterion 1 covers disposition=planned; plan=Implement the mapped behavior.", 1, "disposition=planned; plan=Implement the mapped behavior.")]
    [Xunit.InlineData("1 - maps to disposition=planned; plan=Implement the mapped behavior.", 1, "disposition=planned; plan=Implement the mapped behavior.")]
    [Xunit.InlineData("- Criterion 1 : maps disposition=planned; plan=Implement the mapped behavior.", 1, "disposition=planned; plan=Implement the mapped behavior.")]
    public void PlannerContract_MappingShapes_ParseAndResolve(
        string mappingLine,
        int expectedCriterion,
        string expectedMapping)
    {
        var parsed = PlannerOutputContract.TryParseCriterionMappingLine(
            mappingLine,
            out var criterion,
            out var mapping);

        Xunit.Assert.True(parsed);
        Xunit.Assert.Equal(expectedCriterion, criterion);
        Xunit.Assert.Equal(expectedMapping, mapping);

        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Acceptance criteria mapping",
            mappingLine + "\nSupporting detail keeps this mapping section substantive for contract validation.");

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            workingDirectory,
            acceptanceCriteria: ["Implement the mapped behavior."]);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_MalformedMapping_ReportsOffendingLine()
    {
        const string offendingLine = "**Criterion 1 current objective without a separator**";
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Acceptance criteria mapping",
            offendingLine + "\nSupporting detail keeps this mapping section substantive for contract validation.");

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            workingDirectory,
            acceptanceCriteria: ["Implement the mapped behavior."]);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("criterion 1 is unmapped", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("was found but was not parsed as a mapping", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains(offendingLine, result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_HeadingMapping_ReportsHeadingAndCanonicalRepair()
    {
        const string heading = "### Criterion 1";
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Acceptance criteria mapping",
            $$"""
            {{heading}}
            `disposition=planned; plan=Implement the mapped behavior.`
            """);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            workingDirectory,
            acceptanceCriteria: ["Implement the mapped behavior."]);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("criterion 1 is unmapped", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("was found but was not parsed as a mapping", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains(heading, result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("1. disposition=planned; plan=<mapping>", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_HeadingMapping_PreservesCriterionNumberInRepair()
    {
        const string heading = "### Criterion 2";
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Acceptance criteria mapping",
            $$"""
            1. disposition=planned; plan=Implement the first mapped behavior.
            {{heading}}
            `disposition=planned; plan=Implement the second mapped behavior.`
            """);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            workingDirectory,
            acceptanceCriteria: ["Implement the first mapped behavior.", "Implement the second mapped behavior."]);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("criterion 2 is unmapped", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains(heading, result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("2. disposition=planned; plan=<mapping>", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("1. disposition=planned; plan=<mapping>", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_CanonicalDispositionMapping_Passes()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Acceptance criteria mapping",
            "1. disposition=planned; plan=Implement the mapped behavior.");

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            workingDirectory,
            acceptanceCriteria: ["Implement the mapped behavior."]);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_HeadingMapping_BoundsOffendingLine()
    {
        var heading = "### Criterion 1 " + new string('x', 250);
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Acceptance criteria mapping",
            heading + "\n`disposition=planned; plan=Implement the mapped behavior.`");

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            workingDirectory,
            acceptanceCriteria: ["Implement the mapped behavior."]);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("…", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(heading, result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_AbsentMapping_ReportsNoLineFound()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Acceptance criteria mapping",
            "This section discusses verification ownership but contains no numbered mapping line.");

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            workingDirectory,
            acceptanceCriteria: ["Implement the mapped behavior."]);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("criterion 1 is unmapped", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("no line was found for criterion 1", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("keep your existing plan and re-emit", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_DuplicateCriterionMappingsRemainFirstWins()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Acceptance criteria mapping",
            """
            Criterion 1 covers disposition=planned
            1 - maps to disposition=planned; plan=This duplicate must not replace the first mapping.
            """);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            workingDirectory,
            acceptanceCriteria: ["Implement the mapped behavior."]);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("must include a non-empty plan", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_CriterionNumberUsesCompleteIntegerBoundary()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Acceptance criteria mapping",
            "Criterion 10 covers disposition=planned; plan=This maps criterion ten, not criterion one.");

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            workingDirectory,
            acceptanceCriteria: ["Implement criterion one."]);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("criterion 1 is unmapped", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_MappingOutsideAcceptanceSectionDoesNotSatisfyMissingCriterion()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Target seams and symbols",
            "Criterion 2 covers the second criterion through `seed.txt`.");

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            workingDirectory,
            acceptanceCriteria: ["Implement criterion one.", "Implement criterion two."]);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("criterion 2 is unmapped", result.Diagnostic, StringComparison.Ordinal);
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
        if (nextHeading < 0)
        {
            nextHeading = normalized.Length;
        }

        return normalized[..bodyStart] + "\n\n" + replacement.Trim() + "\n" + normalized[nextHeading..];
    }

    private static string PlannerFixturePath(string fileName) =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "PlannerOutputContract",
            fileName);

    private static bool IsExcludedRepositoryFixtureDirectory(string name) =>
        name.StartsWith(".", StringComparison.Ordinal) ||
        name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TestResults", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("playwright-report", StringComparison.OrdinalIgnoreCase);

    private static string[] FindNonExcludedRepositoryFixtureFiles(string repositoryRoot, string filename)
    {
        var matches = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(repositoryRoot);
        while (pendingDirectories.TryPop(out var directory))
        {
            matches.AddRange(Directory.EnumerateFiles(directory, filename));
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                if (IsExcludedRepositoryFixtureDirectory(Path.GetFileName(childDirectory)) ||
                    (File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                pendingDirectories.Push(childDirectory);
            }
        }

        return matches.ToArray();
    }

    private static string ReadCanonicalArchivedFixture(string fileName, string expectedSha256)
    {
        var fixtureBytes = System.Text.Encoding.UTF8.GetBytes(
            File.ReadAllText(PlannerFixturePath(fileName)).ReplaceLineEndings("\n"));
        Xunit.Assert.Equal(
            expectedSha256,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fixtureBytes)));
        return System.Text.Encoding.UTF8.GetString(fixtureBytes);
    }
}
