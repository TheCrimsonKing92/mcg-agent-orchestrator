using Mcg.AgentOrchestrator.Core;

public sealed class AcceptanceCriteriaParserNestedListTests
{
    [Xunit.Fact]
    public void ParseDeclared_IndentedSubBullets_ContinueOpenCriterion()
    {
        var brief = """
            ## Acceptance
            1. The shape is:
               - first sub-point;
               - second sub-point;
               - third sub-point.
            2. Second criterion.
            ## Scope
            """;

        Assert.Equal(
            ["The shape is: first sub-point; second sub-point; third sub-point.", "Second criterion."],
            AcceptanceCriteriaParser.ParseDeclared(brief));
    }

    [Xunit.Fact]
    public void ParseDeclared_FlatBullets_YieldOneCriterionPerBullet()
    {
        var brief = """
            ## Acceptance
            - First.
            - Second.
            - Third.
            ## Scope
            """;

        Assert.Equal(["First.", "Second.", "Third."], AcceptanceCriteriaParser.ParseDeclared(brief));
    }

    [Xunit.Fact]
    public void ParseDeclared_UniformlyIndentedItems_YieldOneCriterionPerItem()
    {
        var brief = """
            ## Acceptance
              - First.
              * Second.
              3) Third.
            ## Scope
            """;

        Assert.Equal(["First.", "Second.", "Third."], AcceptanceCriteriaParser.ParseDeclared(brief));
    }

    [Xunit.Fact]
    public void ParseDeclared_ShallowerLaterItem_LowersBaseline()
    {
        var shallower = """
            ## Acceptance
              - First.
              - Second.
            - Third.
            ## Scope
            """;
        var nested = """
            ## Acceptance
            - First.
               - Detail one.
               2) Detail two.
            - Second.
            ## Scope
            """;

        Assert.Equal(["First.", "Second.", "Third."], AcceptanceCriteriaParser.ParseDeclared(shallower));
        Assert.Equal(["First. Detail one. Detail two.", "Second."], AcceptanceCriteriaParser.ParseDeclared(nested));
    }

    [Xunit.Fact]
    public void ParseDeclared_TabsExpandToFourColumns()
    {
        var brief = "## Acceptance\n  - First.\n\t- Nested.\n  - Second.\n## Scope";

        Assert.Equal(["First. Nested.", "Second."], AcceptanceCriteriaParser.ParseDeclared(brief));
    }

    [Xunit.Fact]
    public void Parse_ClassifiesMergedCriterionText()
    {
        var brief = """
            ## Acceptance
            - `dotnet build example.sln` passes.
               - The nested check reports success.
            ## Scope
            """;

        var criterion = Assert.Single(AcceptanceCriteriaParser.Parse(brief));
        Assert.Equal("command-exit", criterion.Type);
        Assert.Equal("`dotnet build example.sln` passes. The nested check reports success.", criterion.Name);
    }

    [Xunit.Fact]
    public void ParseDeclared_Goal4d693f8cAcceptanceSection_DeclaresFiveCriteria()
    {
        var brief = """
            ## Acceptance criteria

            1. A conductor-driver test drives the ba07bd29 shape through the conductor's acceptance-failure path, with injected apparatus-gate seams in the style of ConductorDriverTestsWithinAttemptRerunApparatus. The shape is:
               - a `missing-trx` partition with passing within-attempt rerun evidence and no identities;
               - a partition whose one failing identity lies outside the changed paths, with origin UnconfirmedIntroduced and a candidate rerun that passed with a receipt;
               - an aggregate covered by both.

               The test asserts that no worker task is retried, that the goal is held Verified for a re-gate whose reason names both evidence kinds, and that the re-gate is recorded against the per-goal bound. It fails against today's code. Developer owns; Acceptance executes. TEST-VERIFIABLE.
            2. The same test class asserts that today's Developer retry still happens for each of these variants:
               - the identity's candidate rerun failed;
               - the identity lies inside the changed paths;
               - the partition has no within-attempt rerun evidence;
               - the partition's rerun executed zero tests.

               Developer owns; Acceptance executes. TEST-VERIFIABLE.
            3. WithinAttemptRerunApparatusClassifierTests (including IncompleteOrMixedEvidenceKeepsExistingClassification), ConductorDriverTestsWithinAttemptRerunApparatus, ApparatusRedClassifierCandidateRerunTests, ConductorDriverTestsApparatusRedRegate and ApparatusRedGateMessageFingerprintTests pass unmodified. Developer owns; Acceptance executes. TEST-VERIFIABLE.
            4. The Developer reports tests: deferred naming every test class it touched; the Tester's evidence_request runs them. Developer owns; Acceptance executes. TEST-VERIFIABLE.
            5. The Reviewer confirms by reading the diff:
               - that only identity-less checks with passing in-attempt rerun evidence, and aggregates covered by such checks, are newly excused;
               - that every other Genuine path in `ApparatusRedClassifier` is unchanged and the per-goal regate bound is shared;
               - that every touched guarded file stays within its SourceSizeRatchet ceiling. `ConductorDriver.cs` has a ceiling of 6434 lines and 6317 today, so new logic goes in `ApparatusRedGate.cs`, the classifier files or new partial files.

               TEST-VERIFIABLE by reading.

            ## Scope
            """;

        var criteria = AcceptanceCriteriaParser.ParseDeclared(brief);
        Assert.Equal(5, criteria.Count);
        Assert.Contains("The shape is:", criteria[0]);
        Assert.Contains("a `missing-trx` partition", criteria[0]);
        Assert.Contains("that every touched guarded file stays within its SourceSizeRatchet ceiling", criteria[4]);
    }
}
