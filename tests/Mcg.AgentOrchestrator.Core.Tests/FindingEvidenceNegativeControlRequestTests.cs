using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

public sealed class FindingEvidenceNegativeControlRequestTests
{
    [Xunit.Fact]
    public void RevertSrcParsesAndRoundTripsThroughFindingsJson()
    {
        Assert.True(ReviewFindingConvergence.TryParseJson(Findings(",\"negative_control\":\"revert-src\""),
            "[]", out var round, out var diagnostic), diagnostic);
        var request = Assert.Single(round.Findings).EvidenceRequest!;
        Assert.Equal(FindingEvidenceNegativeControl.RevertSrc, request.NegativeControl);
        var json = JsonSerializer.Serialize(round.Findings);
        Assert.Contains("\"negative_control\":\"revert-src\"", json, StringComparison.Ordinal);
        Assert.True(ReviewFindingConvergence.TryParseJson(json, "[]", out var replay, out diagnostic), diagnostic);
        Assert.Equal(request.NegativeControl, Assert.Single(replay.Findings).EvidenceRequest!.NegativeControl);
    }

    [Xunit.Theory]
    [Xunit.InlineData("\"revert-all\"")]
    [Xunit.InlineData("\"REVERT_SRC\"")]
    [Xunit.InlineData("\"RevertSrc\"")]
    [Xunit.InlineData("1")]
    [Xunit.InlineData("true")]
    [Xunit.InlineData("{}")]
    public void UnknownModeRejectsTheFindingRequest(string value)
    {
        Assert.False(ReviewFindingConvergence.TryParseJson(Findings(",\"negative_control\":" + value),
            "[]", out _, out var diagnostic));
        Assert.Contains("negative_control must be 'revert-src'", diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MissingModeKeepsLegacyJsonAndRequestIdentity()
    {
        Assert.True(ReviewFindingConvergence.TryParseJson(Findings(""), "[]", out var round, out var diagnostic), diagnostic);
        var request = Assert.Single(round.Findings).EvidenceRequest!;
        Assert.Null(request.NegativeControl);
        Assert.DoesNotContain("negative_control", JsonSerializer.Serialize(request), StringComparison.Ordinal);
        Assert.Equal("Core.Tests:ProbeTests", FindingEvidenceExecutionClassifier.BuildRequestIdentity(request));
        Assert.Equal("Core.Tests:ProbeTests|negative_control=revert-src",
            FindingEvidenceExecutionClassifier.BuildRequestIdentity(request with
            { NegativeControl = FindingEvidenceNegativeControl.RevertSrc }));
        var receipt = new FindingEvidenceReceipt("receipt", "sha", request, true, true, "green");
        Assert.DoesNotContain("NegativeControlOutcome", JsonSerializer.Serialize(receipt), StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(FindingEvidenceNegativeControlOutcome.Demonstrated, "negative-control-demonstrated")]
    [Xunit.InlineData(FindingEvidenceNegativeControlOutcome.NotDemonstrated, "negative-control-not-demonstrated")]
    [Xunit.InlineData(FindingEvidenceNegativeControlOutcome.Inconclusive, "negative-control-inconclusive")]
    public void ReceiptPersistsTypedOutcomeWithWireValue(FindingEvidenceNegativeControlOutcome outcome, string wire)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var receipt = new FindingEvidenceReceipt("receipt", "sha", new FindingEvidenceRequest([]), true, true,
            "summary", NegativeControlOutcome: outcome);
        var json = JsonSerializer.Serialize(receipt, options);
        Assert.Contains(wire, json, StringComparison.Ordinal);
        Assert.Equal(outcome, JsonSerializer.Deserialize<FindingEvidenceReceipt>(json, options)!.NegativeControlOutcome);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Tester, TaskComplexity.Simple)]
    [Xunit.InlineData(AgentRole.Tester, TaskComplexity.Complex)]
    [Xunit.InlineData(AgentRole.Reviewer, TaskComplexity.Simple)]
    [Xunit.InlineData(AgentRole.Reviewer, TaskComplexity.Complex)]
    public void RequestInstructionAppearsOncePerRole(AgentRole role, TaskComplexity complexity)
    {
        var lines = SdlcRolePromptRequirements.BuildPlainText(role, complexity).Split(Environment.NewLine);
        Assert.Contains("negative_control:\"revert-src\"", Assert.Single(lines,
            line => line.Contains("negative_control", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public void OrdinaryOrIncompleteReceiptDoesNotSuppressControlExecution(bool requestedControl, bool recordedOutcome)
    {
        Assert.True(ReviewFindingConvergence.TryParseJson(Findings(",\"negative_control\":\"revert-src\""),
            "[]", out var round, out var diagnostic), diagnostic);
        var finding = Assert.Single(round.Findings);
        var request = finding.EvidenceRequest!;
        var receipt = new FindingEvidenceReceipt("receipt", "sha", request with
            { NegativeControl = requestedControl ? FindingEvidenceNegativeControl.RevertSrc : null },
            true, true, "summary", RequestDispositions:
            [new FindingEvidenceRequestDisposition(finding.StableId,
                FindingEvidenceExecutionClassifier.BuildRequestIdentity(request), "executed-standalone")],
            NegativeControlOutcome: recordedOutcome ? FindingEvidenceNegativeControlOutcome.Demonstrated : null);
        var task = new TaskSpec(TaskId.New(), "Verify control", AgentRole.Tester);
        task.RecordVerification(new TaskVerificationRecord("focused evidence", "worktree", 0, "ok", "",
            DateTimeOffset.UtcNow, FindingEvidenceReceipts: [receipt]));
        Assert.Equal(requestedControl && recordedOutcome ? FindingEvidenceExecutionState.ExecutedOnCandidate :
            FindingEvidenceExecutionState.PendingExecution, FindingEvidenceExecutionClassifier.Classify(task, finding, "sha"));
    }

    private static string Findings(string option) =>
        "[{\"stable_id\":\"negative-control\",\"state\":\"open\",\"location\":{\"file\":\"src/A.cs\",\"region\":\"A.Run\"}," +
        "\"description\":\"Prove test sensitivity\",\"evidence_request\":{\"selections\":[{\"test_project\":\"Core.Tests\",\"test_class\":\"ProbeTests\"}]" +
        option + "}}]";
}
