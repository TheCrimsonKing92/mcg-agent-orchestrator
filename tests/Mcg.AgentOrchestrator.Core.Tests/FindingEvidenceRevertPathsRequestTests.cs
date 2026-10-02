using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class FindingEvidenceRevertPathsRequestTests
{
    [Xunit.Fact]
    public void Parse_TargetedControl_RoundTripsPaths()
    {
        var finding = Parse(",\"negative_control\":\"revert-src\",\"revert_paths\":[\"src/A.cs\",\"src/B.cs\"]");
        Assert.Equal(new[] { "src/A.cs", "src/B.cs" }, finding.EvidenceRequest!.RevertPaths);
        var json = JsonSerializer.Serialize(new[] { finding });
        Assert.Contains("\"revert_paths\":[\"src/A.cs\",\"src/B.cs\"]", json, StringComparison.Ordinal);
        Assert.True(ReviewFindingConvergence.TryParseJson(json, "[]", out var replay, out var diagnostic), diagnostic);
        Assert.Equal(finding.EvidenceRequest.RevertPaths, Assert.Single(replay.Findings).EvidenceRequest!.RevertPaths);
    }

    [Xunit.Fact]
    public void Parse_PathsWithoutControl_RejectsRequest()
    {
        Assert.False(ReviewFindingConvergence.TryParseJson(Findings(",\"revert_paths\":[\"src/A.cs\"]"),
            "[]", out _, out var diagnostic));
        Assert.Contains("revert_paths requires negative_control 'revert-src'", diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("null")]
    [Xunit.InlineData("1")]
    [Xunit.InlineData("{}")]
    [Xunit.InlineData("\"src/A.cs\"")]
    public void Parse_NonArrayPaths_RejectsOrKeepsNull(string value)
    {
        var parsed = ReviewFindingConvergence.TryParseJson(Findings(",\"negative_control\":\"revert-src\",\"revert_paths\":" + value),
            "[]", out var round, out _);
        Assert.Equal(value == "null", parsed);
        if (parsed) Assert.Null(Assert.Single(round.Findings).EvidenceRequest!.RevertPaths);
    }

    [Xunit.Fact]
    public void Identity_TargetedPaths_AreDistinctAndCanonical()
    {
        var request = Parse(",\"negative_control\":\"revert-src\"").EvidenceRequest!;
        var legacy = FindingEvidenceExecutionClassifier.BuildRequestIdentity(request);
        Assert.Equal("Core.Tests:ProbeTests|negative_control=revert-src", legacy);
        Assert.DoesNotContain("revert_paths", JsonSerializer.Serialize(request), StringComparison.Ordinal);
        var targeted = request with { RevertPaths = ["src/A.cs", "src/B.cs"] };
        var identity = FindingEvidenceExecutionClassifier.BuildRequestIdentity(targeted);
        Assert.NotEqual(legacy, identity);
        Assert.Equal(identity, FindingEvidenceExecutionClassifier.BuildRequestIdentity(request with
            { RevertPaths = ["./src/B.cs", "src\\A.cs", "src/A.cs"] }));
        Assert.NotEqual(identity, FindingEvidenceExecutionClassifier.BuildRequestIdentity(request with
            { RevertPaths = ["src/A.cs"] }));
        Assert.NotEqual(legacy, FindingEvidenceExecutionClassifier.BuildRequestIdentity(request with { RevertPaths = [] }));
        // JSON encoding prevents one comma-containing filename colliding with two paths.
        Assert.NotEqual(identity, FindingEvidenceExecutionClassifier.BuildRequestIdentity(request with
            { RevertPaths = ["src/A.cs,src/B.cs"] }));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Merge_NarrowedPaths_DropsPriorOutcome(bool equivalent)
    {
        var finding = Parse(",\"negative_control\":\"revert-src\",\"revert_paths\":[\"src/A.cs\",\"src/B.cs\"]");
        var outcome = new FindingEvidenceOutcome(true, "receipt");
        var prior = finding with { EvidenceOutcome = outcome };
        var next = finding with { EvidenceRequest = finding.EvidenceRequest! with
            { RevertPaths = equivalent ? ["src/B.cs", "./src/A.cs"] : ["src/A.cs"] } };
        var merged = Assert.Single(ReviewFindingConvergence.ApplyRound([prior], new ReviewFindingRound([next], [])));
        if (equivalent) Assert.Equal(outcome, merged.EvidenceOutcome);
        else Assert.Null(merged.EvidenceOutcome);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Classify_PriorReceipt_CoversOnlyMatchingPaths(bool equivalent)
    {
        var finding = Parse(",\"negative_control\":\"revert-src\",\"revert_paths\":[\"src/A.cs\"]");
        var request = finding.EvidenceRequest!;
        var receipt = new FindingEvidenceReceipt("receipt", "sha", request with
            { RevertPaths = equivalent ? ["./src/A.cs"] : null }, true, true, "green",
            RequestDispositions: [new FindingEvidenceRequestDisposition(finding.StableId,
                FindingEvidenceExecutionClassifier.BuildRequestIdentity(request), "executed-standalone")],
            NegativeControlOutcome: FindingEvidenceNegativeControlOutcome.CompileRed);
        var task = new TaskSpec(TaskId.New(), "Verify control", AgentRole.Tester);
        task.RecordVerification(new TaskVerificationRecord("focused evidence", "worktree", 0, "ok", "",
            DateTimeOffset.UtcNow, FindingEvidenceReceipts: [receipt]));
        Assert.Equal(equivalent ? FindingEvidenceExecutionState.ExecutedOnCandidate : FindingEvidenceExecutionState.PendingExecution,
            FindingEvidenceExecutionClassifier.Classify(task, finding, "sha"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(FindingEvidenceRevertPathsRejection.EmptyList, "revert-paths-empty")]
    [Xunit.InlineData(FindingEvidenceRevertPathsRejection.UnderTests, "revert-paths-under-tests")]
    [Xunit.InlineData(FindingEvidenceRevertPathsRejection.OutsideSrc, "revert-paths-outside-src")]
    [Xunit.InlineData(FindingEvidenceRevertPathsRejection.NotChangedByGoal, "revert-paths-not-changed")]
    public void Receipt_Rejection_RoundTripsTypedReason(FindingEvidenceRevertPathsRejection reason, string wire)
    {
        var request = Parse(",\"negative_control\":\"revert-src\"").EvidenceRequest!;
        var receipt = new FindingEvidenceReceipt("receipt", "sha", request, true, true, wire,
            NegativeControlOutcome: FindingEvidenceNegativeControlOutcome.Inconclusive, RevertPathsRejection: reason);
        var json = JsonSerializer.Serialize(receipt);
        Assert.Contains(wire, json, StringComparison.Ordinal);
        Assert.Equal(reason, JsonSerializer.Deserialize<FindingEvidenceReceipt>(json)!.RevertPathsRejection);
    }

    [Xunit.Fact]
    public void Receipt_CompileRed_RoundTripsNewWireValue()
    {
        var receipt = new FindingEvidenceReceipt("receipt", "sha", new FindingEvidenceRequest([]), true, true,
            "summary", NegativeControlOutcome: FindingEvidenceNegativeControlOutcome.CompileRed);
        var json = JsonSerializer.Serialize(receipt);
        Assert.Contains("negative-control-compile-red", json, StringComparison.Ordinal);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.CompileRed,
            JsonSerializer.Deserialize<FindingEvidenceReceipt>(json)!.NegativeControlOutcome);
    }

    private static ReviewFinding Parse(string option)
    {
        Assert.True(ReviewFindingConvergence.TryParseJson(Findings(option), "[]", out var round, out var diagnostic), diagnostic);
        return Assert.Single(round.Findings);
    }

    private static string Findings(string option) =>
        "[{\"stable_id\":\"control\",\"state\":\"open\",\"location\":{\"file\":\"src/A.cs\",\"region\":\"A.Run\"}," +
        "\"description\":\"Prove test sensitivity\",\"evidence_request\":{\"selections\":[{\"test_project\":\"Core.Tests\",\"test_class\":\"ProbeTests\"}]" +
        option + "}}]";
}
