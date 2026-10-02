using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class PromptRolloutPhraseStepTests
{
    [Xunit.Fact]
    public void RealLandingExtractsEscapedControlPhrasesAndExcludesExistingRequest()
    {
        using var repo = new PromptRolloutTestRepository();
        var phrases = PromptRolloutPhraseStep.FindNovelPhrases(repo.Root, repo.LandingSha,
            [PromptRolloutPhraseStep.RoleRequirementsPath]);
        Assert.Contains("negative_control", phrases);
        Assert.Contains("revert-src", phrases);
        Assert.DoesNotContain("evidence_request", phrases);
    }

    [Xunit.Fact]
    public void NonPromptLandingReturnsNoPhrases()
    {
        using var repo = new PromptRolloutTestRepository();
        repo.Write("src/Other.cs", "// negative_control:revert-src\n");
        var sha = repo.Commit("ordinary source");
        Assert.Empty(PromptRolloutPhraseStep.FindNovelPhrases(repo.Root, sha, ["src/Other.cs"]));
    }

    [Xunit.Fact]
    public void SkillsUseQuotedSpansAndIdentifiersWithUnionBaselineAndOrdinalComparison()
    {
        using var repo = new PromptRolloutTestRepository();
        repo.Write(PromptRolloutPhraseStep.OutputDirectivesPath, "// sibling_phrase\n");
        repo.Commit("sibling baseline");
        const string path = ".agents/skills/example/SKILL.md";
        repo.Write(path, "`backtick span` \"double span\" short-id colon:name sibling_phrase SIBLING_PHRASE\n");
        var sha = repo.Commit("skill landing");
        var phrases = PromptRolloutPhraseStep.FindNovelPhrases(repo.Root, sha, [path.Replace('/', '\\')]);
        Assert.Equal(new[] { "backtick span", "double span", "short-id", "colon:name", "SIBLING_PHRASE" }, phrases);
    }

    [Xunit.Fact]
    public void ExistingPromptPhrasesProduceNothingAndNovelPhrasesAreCappedInAppearanceOrder()
    {
        using var repo = new PromptRolloutTestRepository();
        const string path = ".agents/skills/example/SKILL.md";
        repo.Write(path, "evidence_request negative_control revert-src\n");
        var existing = repo.Commit("existing phrases");
        Assert.Empty(PromptRolloutPhraseStep.FindNovelPhrases(repo.Root, existing, [path]));
        repo.Write(path, string.Join(' ', Enumerable.Range(0, 40).Select(index => $"phrase_{index}")));
        var novel = repo.Commit("many phrases");
        Assert.Equal(Enumerable.Range(0, 32).Select(index => $"phrase_{index}"),
            PromptRolloutPhraseStep.FindNovelPhrases(repo.Root, novel, [path]));
    }
}
