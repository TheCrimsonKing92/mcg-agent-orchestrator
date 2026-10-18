using Mcg.AgentOrchestrator.Core;
using static ConductorDriverTests;

// Pure rule and scenario-owned kernel; parallel-safe.
public sealed class PreTesterEvidenceRefreshRuleTests
{
    [Theory]
    [InlineData("unusable", true, RetryCause.EnvironmentApparatusFailure, 0, true)]
    [InlineData("green", true, RetryCause.EnvironmentApparatusFailure, 0, false)]
    [InlineData("red", true, RetryCause.EnvironmentApparatusFailure, 0, false)]
    [InlineData("actionable-red", true, RetryCause.EnvironmentApparatusFailure, 0, false)]
    [InlineData("started", true, RetryCause.EnvironmentApparatusFailure, 0, false)]
    [InlineData("unusable", false, RetryCause.EnvironmentApparatusFailure, 0, false)]
    [InlineData("unusable", true, RetryCause.Unknown, 0, false)]
    [InlineData("unusable", true, RetryCause.NewSourceFinding, 0, false)]
    [InlineData("unusable", true, RetryCause.ProviderInterruption, 0, false)]
    [InlineData("unusable", true, RetryCause.EnvironmentApparatusFailure, 1, false)]
    [InlineData(null, true, RetryCause.EnvironmentApparatusFailure, 0, false)]
    public void OnlyFirstApparatusRetryRefreshes(string? outcome, bool apparatus, RetryCause cause,
        int count, bool refresh)
    {
        var prior = outcome is null ? null : new PreTesterEvidenceEntry(outcome, "abc1234", "old", [], [], null, []);
        Assert.Equal(refresh ? PreTesterEvidenceRefreshDecision.Refresh : PreTesterEvidenceRefreshDecision.Reuse,
            PreTesterEvidenceRefreshRule.Decide(prior, apparatus, cause, count));
    }

    [Fact]
    public void MarkerRoundTripsAndIsIndependentOfEvidenceIndex()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var prior = new PreTesterEvidenceEntry("unusable", "abc1234", "old", [], [], null, []);
        kernel.RecordFindingEvidenceRun(goal.Id, tester.Id, PreTesterEvidenceIndexLines.FormatMarker(prior));
        var marker = new PreTesterEvidenceRefreshMarker("abc1234", "old", "fresh");
        var line = PreTesterEvidenceRefreshRule.FormatMarker(marker);
        Assert.Equal("finding-evidence pre-tester-refresh candidate_sha=abc1234; superseded_receipt_id=old; fresh_receipt_id=fresh", line);
        Assert.True(PreTesterEvidenceRefreshRule.TryParse(line, out var parsed));
        Assert.Equal(marker, parsed);
        Assert.Null(PreTesterEvidenceIndexLines.Parse(line));
        kernel.RecordFindingEvidenceRequest(goal.Id, tester.Id, line);
        Assert.Equal("old", PreTesterEvidenceIndexLines.Latest(goal, tester.Id, "abc1234")!.ReceiptId);
        Assert.Equal(1, PreTesterEvidenceRefreshRule.CountForCandidate(goal, tester.Id, "ABC1234"));
        Assert.Equal(0, PreTesterEvidenceRefreshRule.CountForCandidate(goal, tester.Id, "bbb2222"));
        Assert.Equal(0, PreTesterEvidenceRefreshRule.CountForCandidate(goal, TaskId.New(), "abc1234"));
        Assert.Equal(marker, PreTesterEvidenceRefreshRule.LatestForCandidate(goal, tester.Id, "abc1234"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("finding-evidence pre-tester-refresh candidate_sha=a; superseded_receipt_id=b", false)]
    [InlineData("finding-evidence pre-tester-refresh candidate_sha=a; superseded_receipt_id=b; fresh_receipt_id=", false)]
    public void MalformedMarkersDoNotCount(string line, bool expected) =>
        Assert.Equal(expected, PreTesterEvidenceRefreshRule.TryParse(line, out _));

    [Theory]
    [InlineData("fresh", "unusable", true)]
    [InlineData("old", "unusable", false)]
    [InlineData("fresh", "green", false)]
    [InlineData("fresh", "red", false)]
    [InlineData("fresh", "actionable-red", false)]
    public void OnlyUnusableFreshReceiptEscalates(string receipt, string outcome, bool escalate)
    {
        var marker = new PreTesterEvidenceRefreshMarker("abc1234", "old", "fresh");
        Assert.Equal(escalate ? PreTesterEvidenceRefreshDecision.EscalateRepeat : PreTesterEvidenceRefreshDecision.Reuse,
            PreTesterEvidenceRefreshRule.DecideAfterRun(marker, receipt, outcome));
        Assert.Equal(PreTesterEvidenceRefreshDecision.Reuse,
            PreTesterEvidenceRefreshRule.DecideAfterRun(null, receipt, outcome));
    }
}
