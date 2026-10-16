using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: all inputs are in-memory snapshots; no process or filesystem state.
public sealed class FindingLookupTests
{
    private static readonly DateTimeOffset Completed = DateTimeOffset.Parse("2026-10-16T07:00:00Z");
    private static readonly FindingEvidenceRequest Request = new([new("tests/Core.csproj", "BuildTests")]);

    [Fact]
    public void ExactId_ReturnsFindingTaskIdentityAndReceipts()
    {
        var finding = Finding("build-timeout");
        var receipt = Receipt("receipt-1");
        var goal = GoalWith(Record([finding], [receipt]));
        var result = FindingLookup.Find([goal], "BUILD-TIMEOUT");
        var match = Assert.Single(result.Matches);
        Assert.Equal("BUILD-TIMEOUT", result.Query);
        Assert.Equal(finding, match.Finding);
        Assert.Equal(goal.Id, match.GoalId);
        Assert.Equal(goal.Tasks[0].Id, match.TaskId);
        Assert.Equal(1, match.TaskNumber);
        Assert.Equal(AgentRole.Reviewer, match.Role);
        Assert.Equal("candidate-sha", match.ReviewedCommit);
        Assert.Equal(Completed, match.CompletedAt);
        Assert.Equal(receipt, Assert.Single(match.Receipts));
        Assert.Equal("candidate-sha", match.Receipts[0].CandidateSha);
        Assert.True(match.Receipts[0].Accepted);
        Assert.False(match.Receipts[0].Passed);
        Assert.Equal("build failed", match.Receipts[0].Summary);
        Assert.Equal(FindingEvidenceArmDisposition.Red, Assert.Single(match.Receipts[0].Arms!).Disposition);
        Assert.Null(match.MissingReceiptReason);
    }

    [Fact]
    public void ExactId_WinsOverLongerIdsSharingThePrefix()
    {
        var goal = GoalWith(Record([Finding("build-timeout-2"), Finding("build-timeout")]));
        Assert.Equal("build-timeout", Assert.Single(FindingLookup.Find([goal], "build-timeout").Matches).Finding.StableId);
    }

    [Fact]
    public void AmbiguousPrefix_ReturnsEveryMatch()
    {
        var goal = GoalWith(Record([Finding("build-timeout"), Finding("build-broken")]));
        Assert.Equal(["build-timeout", "build-broken"],
            FindingLookup.Find([goal], "BUILD-").Matches.Select(match => match.Finding.StableId));
    }

    [Fact]
    public void SameIdOnTwoRecords_ReturnsBothWithOwnReceipts()
    {
        var early = Record([Finding("build-timeout")], [Receipt("early")]);
        var late = early with { CompletedAt = Completed.AddMinutes(1), FindingEvidenceReceipts = [Receipt("late")] };
        var goal = GoalWith(late, early);
        var matches = FindingLookup.Find([goal], "build-timeout").Matches;
        Assert.Equal([Completed, Completed.AddMinutes(1)], matches.Select(match => match.CompletedAt));
        Assert.Equal(["early", "late"], matches.Select(match => Assert.Single(match.Receipts).ReceiptId));
    }

    [Fact]
    public void UnknownId_ReturnsEmpty()
    {
        var goal = GoalWith(Record([Finding("build-timeout")]));
        Assert.Empty(FindingLookup.Find([goal], "unknown").Matches);
        Assert.Empty(FindingLookup.Find([GoalWith(Record(null))], "build").Matches);
    }

    [Fact]
    public void EvidenceRequestWithoutReceipt_ReturnsMissingReceiptReason()
    {
        var goal = GoalWith(Record([Finding("build-timeout")]));
        var match = Assert.Single(FindingLookup.Find([goal], "build-timeout").Matches);
        Assert.Empty(match.Receipts);
        Assert.Equal(FindingLookup.MissingReceiptReason("build-timeout"), match.MissingReceiptReason);
        Assert.Contains("build-timeout", match.MissingReceiptReason!);
        Assert.StartsWith(FindingLookup.MissingReceiptReasonPrefix, match.MissingReceiptReason!);
    }

    [Fact]
    public void NoEvidenceRequest_ReturnsNoReasonAndNoReceipts()
    {
        var receipt = Receipt("unrequested") with { RequestDispositions = [new("build-timeout", "identity", "executed")] };
        var goal = GoalWith(Record([Finding("build-timeout") with { EvidenceRequest = null }], [receipt]));
        var match = Assert.Single(FindingLookup.Find([goal], "build-timeout").Matches);
        Assert.Empty(match.Receipts);
        Assert.Null(match.MissingReceiptReason);
    }

    [Fact]
    public void ReceiptOnAnotherRecord_IsNotAttached()
    {
        var goal = GoalWith(Record([Finding("build-timeout")]), Record([], [Receipt("unrelated-record")]));
        var match = Assert.Single(FindingLookup.Find([goal], "build-timeout").Matches);
        Assert.Empty(match.Receipts);
        Assert.NotNull(match.MissingReceiptReason);
    }

    [Fact]
    public void ExplicitDispositionNamingOtherFinding_IsNotAttached()
    {
        var receipt = Receipt("other") with { RequestDispositions = [new("other-finding", "identity", "executed")] };
        var goal = GoalWith(Record([Finding("build-timeout")], [receipt]));
        Assert.Empty(Assert.Single(FindingLookup.Find([goal], "build-timeout").Matches).Receipts);
    }

    [Fact]
    public void ExplicitReferences_MatchWithoutRequestEquivalence()
    {
        var differentRequest = new FindingEvidenceRequest([new("other.csproj", "OtherTests")]);
        var outcomeReceipt = Receipt("outcome") with { Request = differentRequest };
        var dispositionReceipt = Receipt("disposition") with
        {
            Request = differentRequest,
            RequestDispositions = [new("build-timeout", "identity", "executed")]
        };
        var finding = Finding("build-timeout") with { EvidenceOutcome = new(true, "outcome") };
        var goal = GoalWith(Record([finding], [outcomeReceipt, dispositionReceipt]));
        Assert.Equal(["outcome", "disposition"], Assert.Single(FindingLookup.Find([goal], "build-timeout").Matches)
            .Receipts.Select(receipt => receipt.ReceiptId));
    }

    [Fact]
    public void NullReceiptRequest_IsNotAttached()
    {
        var finding = Finding("build-timeout") with { EvidenceOutcome = new(true, "malformed") };
        var goal = GoalWith(Record([finding], [Receipt("malformed") with { Request = null! }]));
        Assert.Empty(Assert.Single(FindingLookup.Find([goal], "build-timeout").Matches).Receipts);
    }

    [Fact]
    public void ExactMatchesAcrossGoals_ExcludeLongerIdsAndSortByGoal()
    {
        var first = GoalWith(Record([Finding("build-timeout"), Finding("build-timeout-more")]));
        var second = GoalWith(Record([Finding("build-timeout")]));
        var matches = FindingLookup.Find([second, first], "build-timeout").Matches;
        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.Equal("build-timeout", match.Finding.StableId));
        Assert.Equal(new[] { first.Id.Value, second.Id.Value }.Order(StringComparer.Ordinal),
            matches.Select(match => match.GoalId.Value));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void BlankQuery_Throws(string query) =>
        Assert.Throws<ArgumentException>(() => FindingLookup.Find([], query));

    private static ReviewFinding Finding(string id) => new(id, ReviewFindingState.Open,
        new("build.cs", "Build"), "Build did not complete", EvidenceRequest: Request);

    private static FindingEvidenceReceipt Receipt(string id) => new(id, "candidate-sha", Request,
        true, false, "build failed", [new(FindingEvidenceArm.Candidate, "candidate-sha",
            FindingEvidenceArmDisposition.Red, true, false, "compile error")]);

    private static TaskVerificationSnapshot Record(IReadOnlyList<ReviewFinding>? findings,
        IReadOnlyList<FindingEvidenceReceipt>? receipts = null) =>
        new("review", "", 0, "", "", Completed, ReviewedCommit: "candidate-sha",
            MergedReviewFindings: findings, FindingEvidenceReceipts: receipts);

    private static Goal GoalWith(params TaskVerificationSnapshot[] records)
    {
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(GoalId.New(), "Finding lookup", [new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer)]);
        var snapshot = kernel.ExportSnapshot();
        var goal = snapshot.Goals.Single();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goal with { Tasks = [goal.Tasks.Single() with { VerificationHistory = records }] }]
        }).Goals.Single();
    }
}
