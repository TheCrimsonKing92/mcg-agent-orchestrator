using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its files and SQLite store, and model execution is seamed.
public sealed class BoardFillPremiseVerifierTestsRangeEvidence
{
    [Theory]
    [InlineData("codex-cli")]
    [InlineData("claude-cli")]
    public async Task Valid_lines_and_ranges_complete_with_evidence_unchanged(string profile)
    {
        using var fixture = new BoardFillAssessmentTestFixture();
        foreach (var evidence in new[] { "src/Feature/File.cs:2-9", "src/Feature/File.cs:1", "./src/Feature/File.cs:3" })
        {
            var result = await Verify(fixture, profile, evidence);
            Assert.Equal("complete", result.Status);
            Assert.Null(result.Detail);
            Assert.Null(result.InvalidEvidence);
            Assert.Equal(new BoardFillPremiseVerdict(1, "verified", evidence), Assert.Single(result.Verdicts));
        }
    }

    [Theory]
    [InlineData("codex-cli")]
    [InlineData("claude-cli")]
    public async Task Invalid_evidence_keeps_exact_detail_and_discriminating_reason(string profile)
    {
        using var fixture = new BoardFillAssessmentTestFixture();
        foreach (var (evidence, reason) in new[]
        {
            ("src/Feature/File.cs:5-11", "line-outside"),
            ("src/Feature/File.cs:9-2", "line-outside"),
            ("src/Feature/File.cs:9999999999999999999999", "line-outside"),
            ("src/Feature/File.cs:1-9999999999999999999999", "line-outside"),
            ("src\\Feature\\File.cs:1", "untracked")
        })
        {
            var result = await Verify(fixture, profile, evidence);
            Assert.Equal("invalid", result.Status);
            Assert.Equal("invalid-evidence:1", result.Detail);
            Assert.Equal($"{evidence} ({reason})", result.InvalidEvidence);
            Assert.Empty(result.Verdicts);
        }
    }

    [Fact]
    public void Contract_accepts_ranges_rejects_missing_ends_and_states_path_syntax()
    {
        var result = BoardFillVerifierContract.Parse(Answer("src/Feature/File.cs:2-9"), 1);
        Assert.Equal("complete", result.Status);
        Assert.Equal("src/Feature/File.cs:2-9", Assert.Single(result.Verdicts).Evidence);
        var malformed = BoardFillVerifierContract.Parse(Answer("src/Feature/File.cs:2-"), 1);
        Assert.Equal("invalid", malformed.Status);
        Assert.Equal("missing-evidence:1", malformed.Detail);
        Assert.Empty(malformed.Verdicts);
        using var schema = JsonDocument.Parse(BoardFillVerifierContract.JsonSchema);
        Assert.Equal(@"^[^\s:]+:[1-9][0-9]*(-[1-9][0-9]*)?$", schema.RootElement.GetProperty("properties")
            .GetProperty("verdicts").GetProperty("items").GetProperty("properties").GetProperty("evidence")
            .GetProperty("pattern").GetString());
        Assert.Contains("Number bullets from 1 in order; return exactly one verdict per bullet whose evidence is one repository-relative file path with forward slashes, cited as path:line for one line or path:start-end for a range, with no leading ./ and no absolute path.",
            BoardFillVerifierContract.Prompt("- A premise", BoardFillAssessmentTestFixture.Head));
    }

    [Fact]
    public async Task Invalid_evidence_survives_assessment_storage()
    {
        using var fixture = new BoardFillAssessmentTestFixture();
        var result = await Verify(fixture, "codex-cli", "src/Feature/File.cs:5-11");
        var round = fixture.Finished();
        var assessment = BoardFillFileability.Evaluate(round, BoardFillAssessmentTestFixture.Brief, result,
            BoardFillPreflightChecks.Run(BoardFillAssessmentTestFixture.Brief), BoardFillAssessmentTestFixture.NoDepends,
            fixture.Item, [], BoardFillAssessmentTestFixture.Ready);
        fixture.Store.Assess(round.Id, assessment);
        Assert.Equal("src/Feature/File.cs:5-11 (line-outside)",
            Assert.Single(fixture.Store.ReadAll()).Assessment!.Verification.InvalidEvidence);
    }

    private static string Answer(string evidence) => JsonSerializer.Serialize(new
    {
        verdicts = new[] { new { bullet = 1, verdict = "verified", evidence } }
    });

    private static async Task<BoardFillPremiseVerification> Verify(
        BoardFillAssessmentTestFixture fixture, string profile, string evidence)
    {
        var answer = Answer(evidence);
        var raw = profile == "codex-cli" ? answer : JsonSerializer.Serialize(new
        {
            type = "result", is_error = false, structured_output = JsonSerializer.Deserialize<JsonElement>(answer)
        });
        var catalog = new ModelFunctionCatalog([new(
            ModelFunctionPurposes.BoardFillVerifier, ModelLane.Capable,
            new ModelProfile("test", "unused", ModelCapability.Text, SubscriptionMode.ApiKey),
            Subscription: new(profile, "verifier-alias", "high"))]);
        var calls = 0;
        var verifier = new BoardFillPremiseVerifier(() => catalog, new BoardFillAssessmentTestFixture.Repository(), fixture.Root,
            (_, _) => { calls++; return Task.FromResult(new PanelProcessResult(0, raw, "")); });
        var result = await verifier.VerifyAsync(BoardFillVerifierContract.Premise(BoardFillAssessmentTestFixture.Brief),
            BoardFillAssessmentTestFixture.Head, CancellationToken.None);
        Assert.Equal(1, calls);
        return result;
    }
}
