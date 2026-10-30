using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each fixture owns its manifest, attempt metadata, and result files.
public sealed class GateTestObligationCheckOutcomeTests
{
    internal const string Selector = "tsc --noEmit";
    internal const string CheckName = "npx tsc --noEmit";

    [Fact]
    public void PassingAttempt_InfersDeclaredCheckPassed_ObligationSatisfied()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (id, _, _) = fixture.WriteAttempt([]);
        WriteManifest(fixture, TypeCheck());
        WriteResult(fixture, id);
        var attempt = Assert.Single(ProspectiveEvidenceGateResolver.ReadAttempts(
            fixture.OrchestratorDirectory, fixture.Goal.Id, fixture.CandidateSha));
        var checks = Assert.IsType<ProspectiveEvidenceGateResolver.GateCheckOutcomes>(attempt.Checks);
        Assert.Equal(CheckName, Assert.Single(checks.Declared).Name);
        Assert.Empty(checks.Explicit);
        Assert.True(checks.PassedWithNoFailedChecks);

        var result = Evaluate(fixture, Selector);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(id, result.SourceId);
        Assert.Equal(new[] { Selector }, result.Selectors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedCheckFailure_OverridesPassingInference_ObligationFailed(bool failedList)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (id, _, _) = fixture.WriteAttempt([]);
        WriteManifest(fixture, TypeCheck());
        // An advisory failure can coexist with a passing gate. It still fails this obligation.
        WriteResult(fixture, id, failedChecks: failedList ? [CheckName] : [],
            criteria: failedList ? [] : [new { name = CheckName, passed = false, advisory = true }]);
        var attempt = Assert.Single(ProspectiveEvidenceGateResolver.ReadAttempts(
            fixture.OrchestratorDirectory, fixture.Goal.Id, fixture.CandidateSha));
        Assert.False(Assert.IsType<ProspectiveEvidenceGateResolver.GateCheckOutcomes>(attempt.Checks)
            .Explicit[CheckName]);

        var result = Evaluate(fixture, Selector);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Failed, result.Outcome);
        Assert.Equal(id, result.SourceId);
        Assert.Equal(new[] { Selector }, result.Selectors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{\"kind\":\"faulted\",\"acceptance\":{\"passed\":true,\"failedChecks\":[]}}")]
    [InlineData("{\"kind\":\"accepted\",\"acceptance\":{\"passed\":true}}")]
    [InlineData("{\"kind\":\"accepted\",\"acceptance\":{\"passed\":false,\"failedChecks\":[]}}")]
    public void MissingCheckOutcome_LeavesDeclaredSelectorPending(string? resultJson)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (id, _, metadata) = fixture.WriteAttempt([]);
        WriteManifest(fixture, TypeCheck());
        if (resultJson is not null)
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(metadata)!, id + ".result.json"), resultJson);
        var attempt = Assert.Single(ProspectiveEvidenceGateResolver.ReadAttempts(
            fixture.OrchestratorDirectory, fixture.Goal.Id, fixture.CandidateSha));
        var checks = Assert.IsType<ProspectiveEvidenceGateResolver.GateCheckOutcomes>(attempt.Checks);
        Assert.Single(checks.Declared);
        Assert.Empty(checks.Explicit);
        Assert.False(checks.PassedWithNoFailedChecks);

        var result = Evaluate(fixture, Selector);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, result.Outcome);
        Assert.Equal(id, result.SourceId);
        Assert.Equal(new[] { Selector }, result.Selectors);
    }

    [Fact]
    public void OtherCandidatePassingResult_DoesNotSupplyMissingCheckOutcome()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (otherId, _, _) = fixture.WriteAttempt([], candidateSha: new string('b', 40));
        var (id, _, _) = fixture.WriteAttempt([]);
        WriteManifest(fixture, TypeCheck());
        WriteResult(fixture, otherId);
        Assert.Equal(id, Assert.Single(ProspectiveEvidenceGateResolver.ReadAttempts(
            fixture.OrchestratorDirectory, fixture.Goal.Id, fixture.CandidateSha)).Id);

        var result = Evaluate(fixture, Selector);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, result.Outcome);
        Assert.Equal(id, result.SourceId);
    }

    [Fact]
    public void MetadataResultPath_ReadsNamedResultInsteadOfSibling()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (id, _, metadata) = fixture.WriteAttempt([]);
        WriteManifest(fixture, TypeCheck());
        WriteResult(fixture, id, criteria: [new { name = CheckName, passed = false }]);
        WriteResult(fixture, "selected-result");
        var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(metadata))!;
        properties["resultPath"] = JsonSerializer.SerializeToElement("selected-result.result.json");
        File.WriteAllText(metadata, JsonSerializer.Serialize(properties));

        var result = Evaluate(fixture, Selector);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(id, result.SourceId);
    }

    internal static AcceptanceManifestCheck TypeCheck() => new()
    {
        Name = CheckName, Type = "command", Command = "npx", Arguments = ["tsc", "--noEmit"]
    };

    internal static void WriteManifest(ProspectiveEvidenceGateFixture fixture,
        params AcceptanceManifestCheck[] checks)
    {
        Directory.CreateDirectory(fixture.OrchestratorDirectory);
        File.WriteAllText(Path.Combine(fixture.OrchestratorDirectory, "acceptance-manifest.json"),
            JsonSerializer.Serialize(new { checks }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    internal static void WriteResult(ProspectiveEvidenceGateFixture fixture, string id,
        bool passed = true, string[]? failedChecks = null, object[]? criteria = null) =>
        File.WriteAllText(Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts",
            fixture.Goal.Id.Value, id + ".result.json"), JsonSerializer.Serialize(new
        {
            kind = "accepted",
            acceptance = new { passed, failedChecks = failedChecks ?? [], unmetCriteria = criteria ?? [] }
        }));

    internal static GateTestObligationEvaluator.Result Evaluate(
        ProspectiveEvidenceGateFixture fixture, params string[] selectors)
    {
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, selectors);
        return Assert.Single(GateTestObligationEvaluator.Evaluate(
            kernel.GetGoal(fixture.Goal.Id), fixture.OrchestratorDirectory, fixture.CandidateSha));
    }
}
