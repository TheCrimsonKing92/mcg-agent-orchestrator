using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: the parser is pure and each case uses only local values.
public sealed class AuthorBriefDraftResultParserLeadingNarrativeTests
{
    [Theory]
    [InlineData("Drafting the brief from what I verified at HEAD.\n", "")]
    [InlineData("Drafting the brief from what I verified at HEAD. ", " \r\n\t")]
    [InlineData("Using [verified] evidence and \"quoted\" context.\n", "")]
    [InlineData("", "")]
    public void Draft_preserves_markdown_with_brace_free_prefix(string prefix, string suffix)
    {
        const string markdown = "# Verified brief\nKeep {braces} inside the markdown.";
        var json = JsonSerializer.Serialize(new { kind = "draft", markdown });

        var result = AuthorBriefDraftResultParser.Parse(prefix + json + suffix);

        Assert.NotNull(result);
        Assert.Equal("draft", result.Kind);
        Assert.Equal(markdown, result.Markdown);
        Assert.Null(result.Reason);
        Assert.Empty(result.EvidenceReferences);
    }

    [Fact]
    public void Stale_preserves_reason_and_evidence_after_narrative()
    {
        const string reason = "The requested behavior already exists.";
        string[] evidenceReferences = ["docs/role-capability-matrix.md:1", "docs/operator-runbook.md:1"];
        var json = JsonSerializer.Serialize(new { kind = "stale", reason, evidenceReferences });

        var result = AuthorBriefDraftResultParser.Parse("The source confirms the premise is stale.\n" + json);

        Assert.NotNull(result);
        Assert.Equal("stale", result.Kind);
        Assert.Null(result.Markdown);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(evidenceReferences, result.EvidenceReferences);
    }

    [Theory]
    [InlineData("{\"kind\":\"draft\",\"markdown\":\"brief\"}\nHere is the result.")]
    [InlineData("Narrative with } before the object.\n{\"kind\":\"draft\",\"markdown\":\"brief\"}")]
    [InlineData("{\"kind\":\"draft\",\"markdown\":\"brief\"} {}")]
    [InlineData("```json\n{\"kind\":\"draft\",\"markdown\":\"brief\"}\n```")]
    [InlineData("Drafting the brief.\n{\"kind\":\"draft\",\"markdown\":\" \"}")]
    [InlineData("Narrative with no object at all.")]
    [InlineData("Drafting the brief.\n{\"kind\":\"draft\",\"markdown\":\"brief\"}\nHere is the result.")]
    [InlineData("Drafting the brief.\n{\"kind\":\"draft\",\"markdown\":\"brief\"} {}")]
    [InlineData("Narrative with { stray brace.\n{\"kind\":\"draft\",\"markdown\":\"brief\"}")]
    [InlineData("The premise is stale.\n{\"kind\":\"stale\",\"reason\":\"old\",\"evidenceReferences\":[]}")]
    public void Invalid_output_returns_null(string output)
    {
        Assert.Null(AuthorBriefDraftResultParser.Parse(output));
    }
}
