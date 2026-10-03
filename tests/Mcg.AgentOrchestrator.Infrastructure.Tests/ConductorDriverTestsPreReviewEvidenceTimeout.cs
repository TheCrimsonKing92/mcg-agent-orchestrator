using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPreReviewEvidenceTimeout
{
    private const string CandidateSha = "timeout-candidate-sha";
    private const string TimeoutCheck =
        "acceptance-check-timeout: reviewer-focused-evidence-sample elapsed=10m budget=10m";

    [Xunit.Fact]
    public void FirstTimeout_HoldsWithoutDeveloperRetryAndRecordsTimeout()
    {
        var fixture = new Fixture();

        var result = fixture.Advance();

        Assert.Empty(fixture.Retries);
        Assert.Empty(fixture.Dispatches);
        Assert.Empty(fixture.Escalations);
        Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Single(fixture.Requests);
        var receipt = Assert.Single(fixture.Reviewer.PreReviewEvidenceHistory);
        Assert.Equal([TimeoutCheck], receipt.EvidenceTimeoutChecks);
        Assert.Equal(CandidateSha, receipt.CandidateSha);
        Assert.Empty(receipt.FailingTestIdentities);
        Assert.Equal(1, receipt.FailedCheckCount);
        Assert.Equal(TimeoutCheck, Assert.Single(receipt.Checks).Name);
    }

    [Xunit.Fact]
    public void ConsecutiveTimeout_EscalatesWithoutDeveloperRetry()
    {
        var fixture = new Fixture();
        fixture.Advance();

        // A fresh driver must recover the sequence from the recorded receipt.
        fixture.Driver = fixture.CreateDriver();
        var result = fixture.Advance();

        Assert.Empty(fixture.Retries);
        Assert.Empty(fixture.Dispatches);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal(fixture.Requests[0], fixture.Requests[1]);
        Assert.Equal(2, fixture.Reviewer.PreReviewEvidenceHistory.Count);
        Assert.Equal([TimeoutCheck], fixture.Reviewer.PreReviewEvidenceReceipt!.EvidenceTimeoutChecks);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        var reason = Assert.Single(fixture.Escalations);
        Assert.StartsWith("PRE_REVIEW_EVIDENCE_TIMEOUT:", reason, StringComparison.Ordinal);
        Assert.Contains(TimeoutCheck, reason, StringComparison.Ordinal);
        Assert.Contains(CandidateSha, reason, StringComparison.Ordinal);
        foreach (var selection in fixture.Context.SelectedFocusedTests)
            Assert.Contains(selection, reason, StringComparison.Ordinal);
        Assert.False(reason.StartsWith("PRE_REVIEW_RED_UNCHANGED_CANDIDATE", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void TimeoutThenPass_RecordsGreenWithoutRetryOrEscalation()
    {
        var fixture = new Fixture();
        var outcomes = new Queue<Func<string, FocusedEvidenceRunResult>>(
            [TimeoutEvidence, PassingPreReviewEvidence]);
        fixture.RunEvidence = request => outcomes.Dequeue()(request);
        fixture.Advance();

        fixture.Advance();

        Assert.Empty(outcomes);
        Assert.Empty(fixture.Retries);
        Assert.Empty(fixture.Escalations);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal(fixture.Requests[0], fixture.Requests[1]);
        Assert.Single(fixture.Dispatches);
        var receipt = fixture.Reviewer.PreReviewEvidenceReceipt!;
        Assert.Equal(PreReviewEvidenceDisposition.Green, receipt.Disposition);
        Assert.Null(receipt.EvidenceTimeoutChecks);
        Assert.Equal(CandidateSha, receipt.CandidateSha);
        Assert.Equal(fixture.Context.SelectedFocusedTests, receipt.SelectedFocusedTests);
        Assert.Equal(2, fixture.Reviewer.PreReviewEvidenceHistory.Count);
    }

    [Xunit.Fact]
    public void TimeoutWithFailingIdentity_RetriesFocusedTestRepair()
    {
        const string identity = "SampleTests.CandidateRegression";
        var fixture = new Fixture
        {
            RunEvidence = request => TimeoutEvidence(request) with
            {
                Checks = [new AcceptanceCheckResult(TimeoutCheck, false, 124, "test failed",
                    FailingTestIdentities: [identity])]
            }
        };

        fixture.Advance();

        var retry = Assert.Single(fixture.Retries);
        Assert.Equal(fixture.Developer.Id, retry.TaskId);
        Assert.StartsWith("pre-review focused-test repair:", retry.Message, StringComparison.Ordinal);
        Assert.Contains(identity, retry.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Escalations);
        Assert.Single(fixture.Dispatches);
        Assert.Equal(PreReviewEvidenceDisposition.Red, fixture.Reviewer.PreReviewEvidenceReceipt!.Disposition);
        Assert.Null(fixture.Reviewer.PreReviewEvidenceReceipt.EvidenceTimeoutChecks);
    }

    [Xunit.Theory]
    [Xunit.InlineData("mixed")]
    [Xunit.InlineData("empty")]
    [Xunit.InlineData("passed-only")]
    public void NonTimeoutOnlyFailure_PreservesBuildRepair(string scenario)
    {
        var fixture = new Fixture
        {
            RunEvidence = request => TimeoutEvidence(request) with
            {
                Checks = scenario switch
                {
                    "mixed" => [new AcceptanceCheckResult(TimeoutCheck, false, 124, null),
                        new AcceptanceCheckResult("focused build", false, 1, "compiler error")],
                    "empty" => [],
                    _ => [new AcceptanceCheckResult("focused build", true, 0, null)]
                }
            }
        };

        fixture.Advance();

        Assert.StartsWith("pre-review build repair:", Assert.Single(fixture.Retries).Message,
            StringComparison.Ordinal);
        Assert.Empty(fixture.Escalations);
        Assert.Single(fixture.Dispatches);
        Assert.Null(fixture.Reviewer.PreReviewEvidenceReceipt!.EvidenceTimeoutChecks);
    }

    [Xunit.Fact]
    public void TimeoutThenBuildFailure_AllowsBuildRepairOnUnchangedCandidate()
    {
        var fixture = new Fixture();
        fixture.Advance();
        fixture.RunEvidence = request => TimeoutEvidence(request) with
        {
            Checks = [new AcceptanceCheckResult("focused build", false, 1, "compiler error")]
        };

        fixture.Advance();

        Assert.StartsWith("pre-review build repair:", Assert.Single(fixture.Retries).Message,
            StringComparison.Ordinal);
        Assert.Empty(fixture.Escalations);
        Assert.Single(fixture.Dispatches);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Null(fixture.Reviewer.PreReviewEvidenceReceipt!.EvidenceTimeoutChecks);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void ChangedCandidateOrSelection_ResetsTimeoutSequence(bool changeCandidate)
    {
        var fixture = new Fixture();
        fixture.Advance();
        fixture.Context = changeCandidate
            ? fixture.Context with { CandidateSha = "different-sha" }
            : fixture.Context with { SelectedFocusedTests = ["different-selection"] };

        var result = fixture.Advance();

        Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Empty(fixture.Retries);
        Assert.Empty(fixture.Dispatches);
        Assert.Empty(fixture.Escalations);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal([TimeoutCheck], fixture.Reviewer.PreReviewEvidenceReceipt!.EvidenceTimeoutChecks);
    }

    [Xunit.Fact]
    public void ReorderedDuplicateSelection_MatchesConsecutiveTimeout()
    {
        var fixture = new Fixture();
        fixture.Context = fixture.Context with { SelectedFocusedTests = ["selection-A", "selection-B"] };
        fixture.Advance();
        fixture.Context = fixture.Context with
        {
            CandidateSha = CandidateSha.ToUpperInvariant(),
            SelectedFocusedTests = ["selection-B", "selection-A", "selection-B"]
        };

        fixture.Advance();

        Assert.Empty(fixture.Retries);
        Assert.Empty(fixture.Dispatches);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.StartsWith("PRE_REVIEW_EVIDENCE_TIMEOUT:", Assert.Single(fixture.Escalations),
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void TimeoutMarker_SurvivesSerializationAndChangesReceiptEquality()
    {
        var fixture = new Fixture();
        fixture.Advance();
        var receipt = fixture.Reviewer.PreReviewEvidenceReceipt!;

        var restored = System.Text.Json.JsonSerializer.Deserialize<PreReviewEvidenceReceipt>(
            System.Text.Json.JsonSerializer.Serialize(receipt))!;

        Assert.Equal([TimeoutCheck], restored.EvidenceTimeoutChecks);
        Assert.True(receipt.ContentEquals(restored));
        Assert.False(receipt.ContentEquals(restored with { EvidenceTimeoutChecks = null }));
        Assert.False(receipt.ContentEquals(restored with { EvidenceTimeoutChecks = ["other-timeout"] }));
    }

    private static FocusedEvidenceRunResult TimeoutEvidence(string request) =>
        new(request, Accepted: true, Passed: false, Summary: "focused evidence hit its budget",
            Checks: [new AcceptanceCheckResult(TimeoutCheck, false, 124, "no TRX produced")]);

    // All boundaries are delegates or the in-memory kernel; no process or clock is observed.
    private sealed class Fixture
    {
        private int _receiptRecords;

        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Reviewer { get; }
        public PreReviewEvidenceContext Context { get; set; } = FocusedPreReviewContext(CandidateSha);
        public Func<string, FocusedEvidenceRunResult> RunEvidence { get; set; } = TimeoutEvidence;
        public List<(TaskId TaskId, string Message)> Retries { get; } = [];
        public List<GoalId> Dispatches { get; } = [];
        public List<string> Requests { get; } = [];
        public List<string> Escalations { get; } = [];
        public ConductorDriver Driver { get; set; }

        public Fixture()
        {
            (Kernel, Goal) = SoftwareGoal("Pre-review timeout routing");
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Reviewer.Id))
                PassVerification(Kernel, Goal, task);
            Driver = CreateDriver();
        }

        public ConductorAdvanceResult Advance() =>
            Driver.AdvanceOnce(Kernel.GetGoal(Goal.Id), ConductorAutonomyPolicy.Permissive);

        public ConductorDriver CreateDriver() => MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => Context,
            runFocusedEvidence: (_, request) =>
            {
                Requests.Add(request);
                return RunEvidence(request);
            },
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                Kernel.RecordPreReviewEvidence(goalId, taskId, receipt with
                {
                    RecordedAt = DateTimeOffset.UnixEpoch.AddSeconds(++_receiptRecords)
                }),
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
            {
                Retries.Add((taskId, message));
                return Kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: roundKind);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                Retries.Add((taskId, message));
                return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: goal =>
            {
                Dispatches.Add(goal.Id);
                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, reason) => Escalations.Add(reason));
    }
}
