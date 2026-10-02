using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class BriefLintTests
{
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
