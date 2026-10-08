using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: checks use an instance-local repository fake and immutable marker rules.
public sealed class CliAuthorDraftCommandTestsOwnerSentences
{
    [Theory]
    [MemberData(nameof(BriefLintTests.PostLandingPhrases), MemberType = typeof(BriefLintTests))]
    public void Post_landing_declared_criterion_fails_with_index_phrase_and_prose_remedy(string phrase)
    {
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown.Replace("The receipt records the result.",
            $"{phrase}, record the result.");
        var checks = AuthorBriefDraftChecks.Run(markdown, CliAuthorDraftCommandTests.Fixture.MainSha,
            new CliAuthorDraftCommandTests.FakeRepository());
        Assert.All(checks.Where(check => check.Name != "post-landing-criterion"), check => Assert.True(check.Passed));
        var result = Assert.Single(checks, check => check.Name == "post-landing-criterion");
        Assert.False(result.Passed);
        Assert.Contains("criterion 1", result.Detail, StringComparison.Ordinal);
        Assert.Contains($"\"{phrase}\"", result.Detail, StringComparison.Ordinal);
        Assert.Contains("prose outside the numbered acceptance criteria", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Unchanged_valid_draft_passes_post_landing_check()
    {
        Assert.True(PostLandingCheck(CliAuthorDraftCommandTests.ValidMarkdown).Passed);
    }

    [Theory]
    [MemberData(nameof(BriefLintTests.PostLandingPhrases), MemberType = typeof(BriefLintTests))]
    public void Post_landing_phrase_only_in_scope_passes(string phrase)
    {
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown.Replace("Drafting only.",
            $"{phrase}, record the result.");
        Assert.True(PostLandingCheck(markdown).Passed);
    }

    [Fact]
    public void Post_landing_phrase_on_declared_criterion_continuation_fails()
    {
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown.Replace("The receipt records the result.",
            "The receipt records the result.\n   After landing, record another result.");
        var result = PostLandingCheck(markdown);
        Assert.False(result.Passed);
        Assert.Contains("criterion 1", result.Detail, StringComparison.Ordinal);
        Assert.Contains("\"After landing\"", result.Detail, StringComparison.Ordinal);
    }

    private static AuthorBriefDraftCheck PostLandingCheck(string markdown) =>
        Assert.Single(AuthorBriefDraftChecks.Run(markdown, CliAuthorDraftCommandTests.Fixture.MainSha,
            new CliAuthorDraftCommandTests.FakeRepository()), check => check.Name == "post-landing-criterion");

    [Theory]
    [InlineData("Developer owns; Conductor acceptance gate executes. TEST-VERIFIABLE")]
    [InlineData("Tester owns; Conductor acceptance gate executes. TEST-VERIFIABLE")]
    [InlineData("Reviewer owns; Conductor acceptance gate executes. TEST-VERIFIABLE")]
    [InlineData("Operator owns; Operator executes after landing. REAL-WORLD-DEPENDENT")]
    [InlineData("Operator owns; Operator executes after the dependent goals land. REAL-WORLD-DEPENDENT")]
    public void Invented_owner_sentences_fail_with_the_criterion_in_the_detail(string sentence)
    {
        var criterion = "The feature works. " + sentence;
        var result = Check(criterion);
        Assert.False(result.Passed);
        Assert.Contains(criterion, result.Detail);
    }

    [Theory]
    [InlineData("Developer owns; Acceptance executes. TEST-VERIFIABLE.", true, false)]
    [InlineData("Tester owns; Acceptance executes. TEST-VERIFIABLE.", true, false)]
    [InlineData("Reviewer owns; Reviewer executes. TEST-VERIFIABLE.", false, false)]
    [InlineData("Operator owns; Operator executes. REAL-WORLD-DEPENDENT.", false, true)]
    public void Exact_house_forms_pass_and_match_downstream_ownership(
        string sentence, bool acceptance, bool operatorOwned)
    {
        Assert.True(Check("The feature works. " + sentence).Passed);
        Assert.Equal(acceptance, AcceptanceCriterionOwnershipMarker.HasAcceptanceGateOwnershipMarker(sentence));
        Assert.Equal(operatorOwned ? AcceptanceCriterionOwnershipClassification.OperatorOwned :
            AcceptanceCriterionOwnershipClassification.NotOperatorOwned,
            AcceptanceCriterionOwnershipMarker.Classify(sentence).Classification);
    }

    [Fact]
    public void Conductor_sentence_has_no_acceptance_gate_marker()
    {
        Assert.False(AcceptanceCriterionOwnershipMarker.HasAcceptanceGateOwnershipMarker(
            "Developer owns; Conductor acceptance gate executes. TEST-VERIFIABLE"));
    }

    private static AuthorBriefDraftCheck Check(string criterion)
    {
        var markdown = $$"""
            ## Measured premise
            - `docs/role-capability-matrix.md:1` defines owners.
            ## What to build
            A feature.
            ## Acceptance criteria
            - {{criterion}}
            ## Scope
            A feature.
            """;
        return Assert.Single(AuthorBriefDraftChecks.Run(markdown, CliAuthorDraftCommandTests.Fixture.MainSha,
            new CliAuthorDraftCommandTests.FakeRepository()), check => check.Name == "owner-sentence");
    }
}
