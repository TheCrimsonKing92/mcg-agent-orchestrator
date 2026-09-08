using Mcg.AgentOrchestrator.Core;

public sealed class AcceptanceCriteriaParserTests
{
    [Xunit.Fact]
    public void Parse_CodeTokensInProse_EmitsNoPatternCheck()
    {
        var text = """
            ## Some Goal

            ## Acceptance
            - grep confirms no path still calls `new JsonSerializerOptions`
            - No test identity is lost beyond `[Xunit.Fact]` or `[Xunit.Theory]` declarations removed by the diff.
            - dotnet build clean and passes
            """;

        var criteria = AcceptanceCriteriaParser.Parse(text);

        Assert.Empty(criteria);
    }

    [Xunit.Fact(DisplayName = "AcceptanceCriteriaParser_returns_empty_for_brief_with_no_acceptance_section")]
    public void AcceptanceCriteriaParserReturnsEmptyForBriefWithNoAcceptanceSection()
    {
        var text = """
            ## Why
            Some reason.

            ## Design
            Some design.
            """;

        var criteria = AcceptanceCriteriaParser.Parse(text);

        Assert.Empty(criteria);
    }

    [Xunit.Fact(DisplayName = "AcceptanceCriteriaParser_skips_subjective_bullets")]
    public void AcceptanceCriteriaParserSkipsSubjectiveBullets()
    {
        var text = """
            ## Acceptance
            - Both suites green via scripts/Invoke-TestSummary.ps1
            - No new warnings introduced
            - Code is clean and readable
            """;

        var criteria = AcceptanceCriteriaParser.Parse(text);

        Assert.Empty(criteria);
    }

    [Xunit.Fact(DisplayName = "AcceptanceCriteriaParser_emits_command_exit_from_backtick_command_bullet")]
    public void AcceptanceCriteriaParserEmitsCommandExitFromBacktickCommandBullet()
    {
        var text = """
            ## Acceptance
            - `dotnet build Mcg.AgentOrchestrator.sln -c Release` clean, no new warnings.
            - Both suites green (Core.Tests + Infrastructure.Tests) via scripts/Invoke-TestSummary.ps1.
            """;

        var criteria = AcceptanceCriteriaParser.Parse(text);

        var commandExit = Xunit.Assert.Single(criteria);
        Assert.Equal("command-exit", commandExit.Type);
        Assert.Equal("dotnet build Mcg.AgentOrchestrator.sln -c Release", commandExit.Command);
        Xunit.Assert.Null(commandExit.Pattern);
        Xunit.Assert.Null(commandExit.Path);
    }

    [Xunit.Fact(DisplayName = "AcceptanceCriteriaParser_emits_file_exists_from_path_is_produced_bullet")]
    public void AcceptanceCriteriaParserEmitsFileExistsFromPathIsProducedBullet()
    {
        var text = """
            ## Acceptance
            - `.orchestrator/goal-acceptance-criteria.json` is produced for a dispatched goal whose brief has a `## Acceptance` section.
            - Both suites green.
            """;

        var criteria = AcceptanceCriteriaParser.Parse(text);

        var fileExists = Xunit.Assert.Single(criteria);
        Assert.Equal("file-exists", fileExists.Type);
        Assert.Equal(".orchestrator/goal-acceptance-criteria.json", fileExists.Path);
        Xunit.Assert.Null(fileExists.Command);
        Xunit.Assert.Null(fileExists.Pattern);
    }

    [Xunit.Fact(DisplayName = "AcceptanceCriteriaParser_parses_this_goals_acceptance_section")]
    public void AcceptanceCriteriaParserParsesThisGoalsAcceptanceSection()
    {
        var text = """
            ## Acceptance
            - `dotnet build Mcg.AgentOrchestrator.sln -c Release` clean, no new warnings.
            - Both suites green (Core.Tests + Infrastructure.Tests) via scripts/Invoke-TestSummary.ps1,
              including the new parser and verifier tests.
            - `.orchestrator/goal-acceptance-criteria.json` is produced for a dispatched goal whose brief has
              a `## Acceptance` section, and the verifier reports those criteria as advisory checks.
            """;

        var criteria = AcceptanceCriteriaParser.Parse(text);

        Assert.Equal(2, criteria.Count);
        Assert.Contains(criteria, c => c.Type == "command-exit" && c.Command == "dotnet build Mcg.AgentOrchestrator.sln -c Release");
        Assert.Contains(criteria, c => c.Type == "file-exists" && c.Path == ".orchestrator/goal-acceptance-criteria.json");
    }

    [Xunit.Fact(DisplayName = "AcceptanceCriteriaParser_stops_at_next_h2_heading")]
    public void AcceptanceCriteriaParserStopsAtNextH2Heading()
    {
        var text = """
            ## Acceptance
            - `dotnet test` passes.

            ## Goal id
            abc123
            """;

        var criteria = AcceptanceCriteriaParser.Parse(text);

        var single = Xunit.Assert.Single(criteria);
        Assert.Equal("command-exit", single.Type);
        Assert.Equal("dotnet test", single.Command);
    }

    [Xunit.Fact(DisplayName = "AcceptanceCriteriaParser_is_case_insensitive_for_acceptance_heading")]
    public void AcceptanceCriteriaParserIsCaseInsensitiveForAcceptanceHeading()
    {
        var text = """
            ## acceptance
            - `git diff --check` passes.
            """;

        var criteria = AcceptanceCriteriaParser.Parse(text);

        var single = Xunit.Assert.Single(criteria);
        Assert.Equal("command-exit", single.Type);
        Assert.Equal("git diff --check", single.Command);
    }

    [Xunit.Fact]
    public void Parse_ExplicitTestRemoval_EmitsStructuredIdentity()
    {
        var text = """
            ## Acceptance
            - test-removal: `Example.Tests.RemoteMirrorTests.PushesMirror`
            """;

        var criteria = AcceptanceCriteriaParser.Parse(text);

        var single = Xunit.Assert.Single(criteria);
        Assert.Equal("test-removal", single.Type);
        Assert.Equal("Example.Tests.RemoteMirrorTests.PushesMirror", single.TestIdentity);
        Xunit.Assert.Null(single.Pattern);
    }

    [Xunit.Fact(DisplayName = "AcceptanceCriteriaParser_preserves_numbered_declared_criteria_and_continuations")]
    public void AcceptanceCriteriaParserPreservesNumberedDeclaredCriteriaAndContinuations()
    {
        var text = """
            ## Acceptance
            1. A reviewer superset is accepted,
               and the goal proceeds.
            2) Extra attestations retain their evidence.
            3. The failure reason reaches conductor output.

            ## Scope
            Keep changes narrow.
            """;

        var criteria = AcceptanceCriteriaParser.ParseDeclared(text);

        Assert.Equal(
            [
                "A reviewer superset is accepted, and the goal proceeds.",
                "Extra attestations retain their evidence.",
                "The failure reason reaches conductor output."
            ],
            criteria);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Acceptance criteria:")]
    [Xunit.InlineData("  aCcEpTaNcE cRiTeRiA:  ")]
    public void ParseDeclared_StandaloneLabel_PreservesNumberedCriteria(string heading)
    {
        var text = $"""
            Brief preface.

            {heading}
            1. First declared criterion.
            2. Second declared criterion,
               with its indented continuation.
            3. Third declared criterion.
            4. Fourth declared criterion.
            """;

        var criteria = AcceptanceCriteriaParser.ParseDeclared(text);

        Assert.Equal(
            [
                "First declared criterion.",
                "Second declared criterion, with its indented continuation.",
                "Third declared criterion.",
                "Fourth declared criterion."
            ],
            criteria);
    }

    [Xunit.Fact]
    public void ParseDeclared_StandaloneLabel_StopsAtRecognizedSectionAndTrailer()
    {
        var text = """
            Acceptance criteria:
            1. First declared criterion.

            ## Scope
            2. Unrelated numbered prose.

            acceptance criteria:
            3. Later declared criterion.

            Verification:
            4. Trailer prose.
            """;

        var criteria = AcceptanceCriteriaParser.ParseDeclared(text);

        Assert.Equal(["First declared criterion."], criteria);
        Assert.Equal(
            "## Scope\n2. Unrelated numbered prose.\n\nacceptance criteria:\n3. Later declared criterion.\n\nVerification:\n4. Trailer prose.",
            AcceptanceCriteriaParser.RemoveDeclaredSection(text));
    }

    [Xunit.Fact]
    public void ParseDeclared_StandaloneLabel_StopsAtSubsequentStandaloneLabel()
    {
        var text = """
            Acceptance criteria:
            1. First declared criterion.

            acceptance criteria:
            2. Later declared criterion.
            """;

        Assert.Equal(["First declared criterion."], AcceptanceCriteriaParser.ParseDeclared(text));
        Assert.Equal(
            "acceptance criteria:\n2. Later declared criterion.",
            AcceptanceCriteriaParser.RemoveDeclaredSection(text));
    }

    [Xunit.Fact]
    public void ParseDeclared_OnlyRecognizesStandalonePlainLabel()
    {
        var text = """
            This sentence mentions acceptance criteria: but is not a section.
            1. Unrelated numbered prose.

            Acceptance criteria
            2. Still unrelated numbered prose.
            """;

        Assert.Empty(AcceptanceCriteriaParser.ParseDeclared(text));
        Assert.Equal(text, AcceptanceCriteriaParser.RemoveDeclaredSection(text));
    }

    [Xunit.Fact(DisplayName = "AcceptanceCriteriaParser_stops_at_plain_text_intake_trailer")]
    public void AcceptanceCriteriaParserStopsAtPlainTextIntakeTrailer()
    {
        var text = """
            ## Acceptance
            1. First declared outcome is preserved.
            2. Second declared outcome is preserved.
            3. Third declared outcome is preserved.
            4. Fourth declared outcome is preserved.

            Target files/scopes:
            Scope confidence: unknown
            Includes:
            - none
            Exclusions:
            - none

            Verification:
            - Run the focused refinement test.
            - Inspect the rendered Planner brief.
            """;

        var criteria = AcceptanceCriteriaParser.ParseDeclared(text);

        Assert.Equal(
            [
                "First declared outcome is preserved.",
                "Second declared outcome is preserved.",
                "Third declared outcome is preserved.",
                "Fourth declared outcome is preserved."
            ],
            criteria);
    }
}
