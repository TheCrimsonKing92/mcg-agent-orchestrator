using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: lint operates only on the supplied text.
public sealed class BriefLintTests
{
    public static IEnumerable<object[]> PreChangeFailureCriteria()
    {
        foreach (var anchor in new[] { "pre-change", "prechange", "before the change", "prior code",
                     "prior implementation", "old code", "unchanged code" })
        foreach (var failure in new[] { "fail", "fails", "failing", "red" })
        foreach (var executor in new[] { "Acceptance executes", "Acceptance runs", "assigned to Acceptance" })
            yield return [$"The test must be {failure} against {anchor}. Developer owns; {executor}. TEST-VERIFIABLE.", anchor];
    }

    public static IEnumerable<object[]> AllowedPreChangeBriefs()
    {
        const string demand = "The test fails against pre-change code. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
        yield return [$"## Acceptance criteria\n1. {demand.Replace("Acceptance executes", "Reviewer executes")}"];
        yield return [$"## What to build\n1. {demand}\n## Acceptance criteria\n1. The receipt exists."];
        yield return [$"## Acceptance criteria\n1. The receipt exists.\n## Scope\n{demand}"];
        yield return [$"## Acceptance criteria\n{demand}\n1. The receipt exists."];
        yield return [$"## Acceptance criteria\n- {demand}"];
        yield return [$"## Acceptance criteria\n* {demand}"];
        yield return ["## Acceptance criteria\n1. A committed negative-control test runs on the candidate and fails against old code. Developer owns; Acceptance executes. TEST-VERIFIABLE."];
        yield return ["## Acceptance criteria\n1. On the candidate, a committed negative control test is red for prior code. Developer owns; Acceptance runs. TEST-VERIFIABLE."];
        yield return ["## Acceptance criteria\n1. Returns a failing pre-change-failure-criterion check. Developer owns; Acceptance executes. TEST-VERIFIABLE."];
        yield return ["## Acceptance criteria\n1. A criterion that needs a test to fail against the pre-change code is never assigned to Acceptance, because focused evidence runs only on the candidate; the pre-change half belongs to a Reviewer reading or to a committed negative-control test that runs on the candidate."];
        yield return ["## Acceptance criteria\n1. The test fails against pre-change code; Acceptance reads the receipt, Reviewer executes."];
        yield return ["## Acceptance criteria\n1. The test passes against pre-change code. Acceptance executes."];
        yield return ["## Acceptance criteria\n1. The test fails against candidate code. Acceptance executes."];
    }

    [Theory]
    [MemberData(nameof(PreChangeFailureCriteria))]
    public void Numbered_pre_change_failure_Blocks_dispatch(string criterion, string anchor)
    {
        var finding = Assert.Single(BriefLint.Lint($"## Acceptance criteria\n1. {criterion}"));
        Assert.Equal("pre-change-failure-criterion", finding.Kind);
        Assert.Equal("blocks-dispatch", finding.SeverityToken);
        Assert.Contains($"\"{anchor}\"", finding.Message, StringComparison.Ordinal);
        Assert.Equal("Make the pre-change half a Reviewer reading, or a committed negative-control test that runs on the candidate.", finding.Remedy);
    }

    [Theory]
    [MemberData(nameof(AllowedPreChangeBriefs))]
    public void Allowed_pre_change_wording_Does_not_block_dispatch(string brief) =>
        Assert.DoesNotContain(BriefLint.Lint(brief), finding => finding.Kind == "pre-change-failure-criterion");

    [Theory]
    [InlineData("\n", ".")]
    [InlineData("\r\n", ")")]
    public void Repeated_pre_change_demands_Report_each_numbered_line(string newline, string ordinal)
    {
        const string criterion = "The test is RED for OLD CODE. ACCEPTANCE RUNS.";
        var findings = BriefLint.Lint(string.Join(newline, "## ACCEPTANCE CRITERIA",
            $"1{ordinal} {criterion}", $"2{ordinal} {criterion}", "## Scope", $"3{ordinal} {criterion}"));
        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding =>
        {
            Assert.Equal("pre-change-failure-criterion", finding.Kind);
            Assert.Contains("\"OLD CODE\"", finding.Message, StringComparison.Ordinal);
        });
    }

    public static IEnumerable<object[]> PostLandingPhrases()
    {
        yield return ["After this goal lands"];
        yield return ["after the goal lands"];
        yield return ["after landing"];
        yield return ["POST-LANDING"];
        yield return ["once this goal has landed"];
        yield return ["once the goal has landed"];
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(PostLandingPhrases))]
    public void NumberedAcceptanceCriterionBlocksDispatchForEachPostLandingPhrase(string phrase)
    {
        var finding = Xunit.Assert.Single(BriefLint.Lint($"## Acceptance criteria\n1. {phrase}, record the result."));
        Xunit.Assert.Equal("post-landing-criterion", finding.Kind);
        Xunit.Assert.Equal("blocks-dispatch", finding.SeverityToken);
        Xunit.Assert.Contains($"\"{phrase}\"", finding.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("prose outside the numbered acceptance criteria", finding.Remedy, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain('\n', finding.Message);
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(PostLandingPhrases))]
    public void PostLandingPhraseOutsideNumberedAcceptanceLineDoesNotBlockDispatch(string phrase)
    {
        string[] briefs =
        [
            $"## What to build\n1. {phrase}, record the result.\n## Acceptance criteria\n1. The receipt exists.",
            $"## Acceptance criteria\n1. The receipt exists.\n## Scope\n{phrase}, record the result.",
            $"## Acceptance criteria\n{phrase}, record the result.\n1. The receipt exists.",
            $"## Acceptance criteria\n1. The receipt exists.\n   {phrase}, record the result."
        ];
        foreach (var brief in briefs)
            Xunit.Assert.DoesNotContain(BriefLint.Lint(brief), finding => finding.Kind == "post-landing-criterion");
    }

    [Xunit.Theory]
    [Xunit.InlineData("\n", ".")]
    [Xunit.InlineData("\r\n", ")")]
    public void RepeatedCriteriaProduceOneFindingPerLineAndStopAtNextSection(string newline, string ordinal)
    {
        var brief = string.Join(newline,
            "## ACCEPTANCE CRITERIA", $"  1{ordinal} After landing, record the result.",
            $"  2{ordinal} After landing, then once the goal has landed, record another result.",
            "## What to build", $"3{ordinal} After landing, record a follow-up.");
        var findings = BriefLint.Lint(brief);
        Xunit.Assert.Equal(2, findings.Count);
        Xunit.Assert.All(findings, finding =>
        {
            Xunit.Assert.Equal("post-landing-criterion", finding.Kind);
            Xunit.Assert.Equal("blocks-dispatch", finding.SeverityToken);
            Xunit.Assert.StartsWith("Matched \"After landing\"", finding.Message, StringComparison.Ordinal);
        });
    }

    public static IEnumerable<object[]> SingleTrapBriefs()
    {
        yield return ["Create the .git-keep marker.", "git-directory-reference", "blocks-dispatch"];
        yield return ["Update SKILL.md with the workflow.", "skill-definition-file", "blocks-dispatch"];
        yield return [$"Plan the {GoalReadinessPreflight.HighRiskSignalWords[0]} change.", "readiness-high-risk-word", "blocks-cli-start"];
        yield return ["Summary ends here## Next steps", "inline-heading-split", "advisory"];
        yield return ["This slice removes three facts from the suite.", "missing-test-removal-bullet", "advisory"];
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(SingleTrapBriefs))]
    public void ReportsEachSingleTrap(string text, string kind, string severity)
    {
        var finding = Xunit.Assert.Single(BriefLint.Lint(text));
        Xunit.Assert.Equal(kind, finding.Kind);
        Xunit.Assert.Equal(severity, finding.SeverityToken);
        Xunit.Assert.NotEmpty(finding.Remedy);
        Xunit.Assert.DoesNotContain('\n', finding.Message);
        Xunit.Assert.DoesNotContain('\r', finding.Message);
    }

    [Xunit.Fact]
    public void CleanBriefAndSingleHashesRemainSilent()
    {
        Xunit.Assert.Empty(BriefLint.Lint("Update .gitignore and .agents/skills/research-evidence/SKILL.md."));
        Xunit.Assert.Empty(BriefLint.Lint("Use C# for this slice.\r\n## Next steps\r\nRead the code."));
        Xunit.Assert.Empty(BriefLint.Lint(""));
    }

    [Xunit.Fact]
    public void RemovalDeclarationSuppressesAdvisory()
    {
        Xunit.Assert.Empty(BriefLint.Lint("This removes three facts.\n  - test-removal: Example.OldFact"));
        Xunit.Assert.Single(BriefLint.Lint("This drops tests.\nMention test-removal: Example.OldFact in prose."));
    }

    [Xunit.Fact]
    public void ReusesEveryReadinessWordAndWholeWordTokenization()
    {
        var text = string.Join("; ", GoalReadinessPreflight.HighRiskSignalWords);
        var findings = BriefLint.Lint(text);
        Xunit.Assert.Equal(GoalReadinessPreflight.HighRiskSignalWords.Count, findings.Count);
        Xunit.Assert.All(findings, finding => Xunit.Assert.Equal("blocks-cli-start", finding.SeverityToken));
        Xunit.Assert.Empty(BriefLint.Lint(string.Join(" ", GoalReadinessPreflight.HighRiskSignalWords.Select(word => $"prefix{word}suffix"))));
    }

    [Xunit.Fact]
    public void ContextIsQuotedAndLineEndingsDoNotCreateFindings()
    {
        const string text = "Long neutral introductory sentence\nThen .git-keep followed by neutral closing text.";
        var findings = BriefLint.Lint(text);
        Xunit.Assert.Equal(findings, BriefLint.Lint(text.Replace("\n", "\r\n")));
        var finding = Xunit.Assert.Single(findings);
        Xunit.Assert.Contains(".git", finding.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Then .git-keep followed", finding.Message, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain('\n', finding.Message);
        Xunit.Assert.DoesNotContain('\n', Xunit.Assert.Single(BriefLint.Lint("removes\nthree facts")).Message);
    }

    [Xunit.Fact]
    public void FindingsAreDeduplicatedAndOrderedBySeverityThenOffset()
    {
        var word = GoalReadinessPreflight.HighRiskSignalWords[0];
        var findings = BriefLint.Lint($"removes three facts; {word} {word.ToUpperInvariant()}; SKILL.md SKILL.md; .git-keep .GIT-keep; A## Heading; B### Other");
        Xunit.Assert.Equal(new[] { "skill-definition-file", "git-directory-reference", "readiness-high-risk-word", "missing-test-removal-bullet", "inline-heading-split", "inline-heading-split" }, findings.Select(finding => finding.Kind));
        Xunit.Assert.All(findings, finding => Xunit.Assert.DoesNotContain('\n', finding.Message));
    }
}
