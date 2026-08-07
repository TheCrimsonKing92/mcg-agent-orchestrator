using Mcg.AgentOrchestrator.Core;

public sealed class AcceptanceCriteriaParserTests
{
    [Xunit.Fact(DisplayName = "AcceptanceCriteriaParser_emits_grep_absent_from_grep_confirms_no_bullet")]
    public void AcceptanceCriteriaParserEmitsGrepAbsentFromGrepConfirmsNoBullet()
    {
        var text = """
            ## Some Goal

            ## Acceptance
            - grep confirms no path still calls `new JsonSerializerOptions`
            - dotnet build clean and passes
            """;

        var criteria = AcceptanceCriteriaParser.Parse(text);

        var grepAbsent = Xunit.Assert.Single(criteria);
        Assert.Equal("grep-absent", grepAbsent.Type);
        Assert.Equal("new JsonSerializerOptions", grepAbsent.Pattern);
        Xunit.Assert.Null(grepAbsent.Command);
        Xunit.Assert.Null(grepAbsent.Path);
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

    [Xunit.Fact(DisplayName = "AcceptanceCriteriaParser_emits_grep_present_from_still_references_bullet")]
    public void AcceptanceCriteriaParserEmitsGrepPresentFromStillReferencesBullet()
    {
        var text = """
            ## Acceptance
            - code still references `ILegacyService` for backward compat.
            """;

        var criteria = AcceptanceCriteriaParser.Parse(text);

        var single = Xunit.Assert.Single(criteria);
        Assert.Equal("grep-present", single.Type);
        Assert.Equal("ILegacyService", single.Pattern);
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
}
