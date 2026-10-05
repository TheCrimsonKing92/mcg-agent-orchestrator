using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BoardFillPremiseVerifierTests
{
    private const string Answer = """
        {"verdicts":[{"bullet":1,"verdict":"verified","evidence":"src/Feature/File.cs:1"}]}
        """;
    private static ModelFunctionCatalog Catalog(string profile = "codex-cli") => new([new(
        ModelFunctionPurposes.BoardFillVerifier, ModelLane.Capable,
        new ModelProfile("test", "unused", ModelCapability.Text, SubscriptionMode.ApiKey),
        Subscription: new(profile, "verifier-alias", "high"))]);

    [Theory]
    [InlineData("codex-cli")]
    [InlineData("claude-cli")]
    public async Task Valid_response_verifies_only_premise_at_head_without_writes(string profile)
    {
        using var h = new BoardFillAssessmentTestFixture();
        var before = Directory.GetFiles(h.Root).ToDictionary(path => path, File.ReadAllBytes);
        WorkerProcessRunRequest? captured = null;
        var raw = profile == "codex-cli" ? Answer : JsonSerializer.Serialize(new { type = "result", is_error = false,
            structured_output = JsonSerializer.Deserialize<JsonElement>(Answer) });
        var verifier = new BoardFillPremiseVerifier(() => Catalog(profile), new BoardFillAssessmentTestFixture.Repository(), h.Root,
            (request, _) => { captured = request; return Task.FromResult(new PanelProcessResult(0, raw, "")); });
        var premise = BoardFillVerifierContract.Premise(BoardFillAssessmentTestFixture.Brief);
        var result = await verifier.VerifyAsync(premise, BoardFillAssessmentTestFixture.Head, CancellationToken.None);
        Assert.Equal("complete", result.Status);
        Assert.Equal(new BoardFillPremiseVerdict(1, "verified", "src/Feature/File.cs:1"), Assert.Single(result.Verdicts));
        Assert.NotNull(captured);
        Assert.Equal(h.Root, captured.WorkingDirectory);
        Assert.Contains("--model 'verifier-alias'", captured.Command);
        Assert.Contains(profile == "codex-cli" ? "--sandbox read-only" : "--permission-mode plan --tools 'Read,Grep,Glob'", captured.Command);
        var payload = captured.StandardInput![(captured.StandardInput!.LastIndexOf('\n') + 1)..];
        using var document = JsonDocument.Parse(payload);
        Assert.Equal(2, document.RootElement.EnumerateObject().Count());
        Assert.Equal(premise, document.RootElement.GetProperty("measuredPremise").GetString());
        Assert.Equal(BoardFillAssessmentTestFixture.Head, document.RootElement.GetProperty("mainHead").GetString());
        Assert.DoesNotContain("What to build", captured.StandardInput);
        Assert.DoesNotContain("Acceptance criteria", captured.StandardInput);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(h.Root).Order());
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [Theory]
    [InlineData("broken", "malformed-json")]
    [InlineData("{\"verdicts\":[]}", "missing-verdict:1")]
    [InlineData("{\"verdicts\":[{\"bullet\":1,\"verdict\":\"verified\",\"evidence\":\"\"}]}", "missing-evidence:1")]
    [InlineData("{\"verdicts\":[{\"bullet\":1,\"verdict\":\"verified\"}]}", "missing-evidence:1")]
    [InlineData("{\"verdicts\":[{\"bullet\":1,\"verdict\":\"yes\",\"evidence\":\"src/Feature/File.cs:1\"}]}", "invalid-verdict:1")]
    [InlineData("{\"verdicts\":[],\"extra\":true}", "invalid-contract")]
    [InlineData("{\"verdicts\":[],\"verdicts\":[]}", "invalid-contract")]
    public async Task Invalid_json_records_specific_reason_and_blocks_fileability(string json, string reason)
    {
        using var h = new BoardFillAssessmentTestFixture();
        var verifier = new BoardFillPremiseVerifier(() => Catalog(), new BoardFillAssessmentTestFixture.Repository(), h.Root,
            (_, _) => Task.FromResult(new PanelProcessResult(0, json, "")));
        var result = await verifier.VerifyAsync(BoardFillVerifierContract.Premise(BoardFillAssessmentTestFixture.Brief),
            BoardFillAssessmentTestFixture.Head, CancellationToken.None);
        Assert.Equal("invalid", result.Status);
        Assert.Equal(reason, result.Detail);
        AssertBlocked(h, result);
    }

    [Theory]
    [InlineData("unbound", "unavailable", "expected-one-binding")]
    [InlineData("wrong-profile", "unavailable", "wrong-profile")]
    [InlineData("catalog", "unavailable", "catalog:IOException")]
    [InlineData("exception", "failed", "InvalidOperationException")]
    [InlineData("timeout", "failed", "timeout")]
    [InlineData("exit", "failed", "exit-code:7")]
    [InlineData("head-before", "failed", "repository-changed")]
    [InlineData("head-after", "failed", "repository-changed")]
    [InlineData("evidence", "invalid", "invalid-evidence:1")]
    public async Task Invocation_failures_are_explicit_and_never_fileable(string fault, string status, string reason)
    {
        using var h = new BoardFillAssessmentTestFixture();
        var repository = new BoardFillAssessmentTestFixture.Repository();
        if (fault == "head-before") repository.Head = "other";
        if (fault == "evidence") repository.Lines = 0;
        var calls = 0;
        var verifier = new BoardFillPremiseVerifier(() => fault == "catalog" ? throw new IOException() :
            fault == "unbound" ? ModelFunctionCatalog.Empty : Catalog(fault == "wrong-profile" ? "invalid" : "codex-cli"), repository, h.Root,
            (_, _) =>
            {
                calls++;
                if (fault == "exception") throw new InvalidOperationException();
                if (fault == "head-after") repository.Head = "other";
                return Task.FromResult(new PanelProcessResult(fault == "exit" ? 7 : 0, Answer, "", fault == "timeout"));
            });
        var result = await verifier.VerifyAsync(BoardFillVerifierContract.Premise(BoardFillAssessmentTestFixture.Brief),
            BoardFillAssessmentTestFixture.Head, CancellationToken.None);
        Assert.Equal(status, result.Status);
        Assert.Equal(reason, result.Detail);
        Assert.Equal(fault is "unbound" or "wrong-profile" or "catalog" or "head-before" ? 0 : 1, calls);
        AssertBlocked(h, result);
    }

    [Fact]
    public void Contract_requires_one_distinct_verdict_per_bullet()
    {
        var premise = "## Measured premise\n- First\n  continuation\n* Second";
        Assert.Equal(2, BoardFillVerifierContract.BulletCount(premise));
        var two = Answer.Replace("}]}", ",\"extra\":true}]}");
        Assert.Equal("invalid-contract", BoardFillVerifierContract.Parse(two, 1).Detail);
        Assert.Equal("missing-verdict:2", BoardFillVerifierContract.Parse(Answer, 2).Detail);
        using var document = JsonDocument.Parse(Answer);
        var row = document.RootElement.GetProperty("verdicts")[0].GetRawText();
        var repeated = "{\"verdicts\":[" + row + "," + row + "]}";
        Assert.Equal("duplicate-verdict:1", BoardFillVerifierContract.Parse(repeated, 2).Detail);
        var complete = Answer.Replace("}]}", "},{\"bullet\":2,\"verdict\":\"contradicted\",\"evidence\":\"src/Feature/File.cs:2\"}]}");
        var parsed = BoardFillVerifierContract.Parse(complete, 2);
        Assert.Equal("complete", parsed.Status);
        Assert.Equal(new[] { "verified", "contradicted" }, parsed.Verdicts.Select(verdict => verdict.Verdict));
    }

    [Fact]
    public async Task No_premise_bullets_never_start_a_process()
    {
        using var h = new BoardFillAssessmentTestFixture();
        var verifier = new BoardFillPremiseVerifier(() => Catalog(), new BoardFillAssessmentTestFixture.Repository(), h.Root,
            (_, _) => throw new Exception("No premise must not invoke process"));
        var result = await verifier.VerifyAsync("## Measured premise\nNo bullet", BoardFillAssessmentTestFixture.Head, CancellationToken.None);
        Assert.Equal("unavailable", result.Status);
        Assert.Equal("no-premise-bullets", result.Detail);
        AssertBlocked(h, result);
    }

    [Fact]
    public async Task Positive_provider_fault_blocks_even_valid_structured_output()
    {
        using var h = new BoardFillAssessmentTestFixture();
        var raw = JsonSerializer.Serialize(new { type = "result", is_error = true,
            structured_output = JsonSerializer.Deserialize<JsonElement>(Answer) });
        var verifier = new BoardFillPremiseVerifier(() => Catalog("claude-cli"), new BoardFillAssessmentTestFixture.Repository(), h.Root,
            (_, _) => Task.FromResult(new PanelProcessResult(0, raw, "")));
        var result = await verifier.VerifyAsync(BoardFillVerifierContract.Premise(BoardFillAssessmentTestFixture.Brief),
            BoardFillAssessmentTestFixture.Head, CancellationToken.None);
        Assert.Equal("failed", result.Status);
        Assert.Equal("provider-fault", result.Detail);
        AssertBlocked(h, result);
    }

    [Theory]
    [InlineData("empty", "empty-model-alias")]
    [InlineData("control", "malformed-model-alias")]
    [InlineData("effort", "invalid-effort")]
    [InlineData("missing-subscription", "missing-subscription")]
    [InlineData("duplicate", "expected-one-binding")]
    public async Task Invalid_binding_never_invokes_the_process(string fault, string reason)
    {
        using var h = new BoardFillAssessmentTestFixture();
        var binding = Catalog().Bindings[0];
        binding = binding with { Subscription = fault == "missing-subscription" ? null : binding.Subscription! with
        {
            ModelAlias = fault == "empty" ? " " : fault == "control" ? "alias\n" : "alias",
            ReasoningEffort = fault == "effort" ? "invalid" : "high"
        } };
        var catalog = new ModelFunctionCatalog(fault == "duplicate" ? [binding, binding] : [binding]);
        var verifier = new BoardFillPremiseVerifier(() => catalog, new BoardFillAssessmentTestFixture.Repository(), h.Root,
            (_, _) => throw new Exception("Invalid binding must not start a process"));
        var result = await verifier.VerifyAsync(BoardFillVerifierContract.Premise(BoardFillAssessmentTestFixture.Brief),
            BoardFillAssessmentTestFixture.Head, CancellationToken.None);
        Assert.Equal("unavailable", result.Status);
        Assert.Equal(reason, result.Detail);
        AssertBlocked(h, result);
    }

    private static void AssertBlocked(BoardFillAssessmentTestFixture h, BoardFillPremiseVerification verification)
    {
        var assessment = BoardFillFileability.Evaluate(h.Finished(), BoardFillAssessmentTestFixture.Brief, verification,
            BoardFillPreflightChecks.Run(BoardFillAssessmentTestFixture.Brief), BoardFillAssessmentTestFixture.NoDepends,
            h.Item, [], BoardFillAssessmentTestFixture.Ready);
        Assert.False(assessment.Fileable);
        Assert.Contains($"verification:{verification.Status} {verification.Detail}", assessment.FailingReasons);
    }
}
