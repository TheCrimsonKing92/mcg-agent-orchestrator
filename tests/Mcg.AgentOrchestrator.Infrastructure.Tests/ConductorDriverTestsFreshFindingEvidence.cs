using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorDriverTestsFreshFindingEvidence
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void HeldSameCandidateResumesOnceWithExactFreshReceipt()
    {
        using var fixture = new FreshFindingEvidenceFixture();
        var driver = fixture.Driver();
        Assert.NotNull(UnchangedCandidateRule.Evaluate(fixture.Goal, fixture.Reviewer, fixture.Identity));
        Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Equal(0, fixture.Launches);
        var fresh = fixture.Receipt();
        fixture.Attach(fresh);

        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, fixture.Launches);
        Assert.Equal(RetryCause.NewTestFinding, Assert.Single(fixture.Causes));
        var continuation = Assert.IsType<FindingEvidenceReviewContinuation>(fixture.Reviewer.FindingEvidenceReviewContinuation);
        Assert.Equal(fresh.ReceiptId, Assert.Single(continuation.Selections).ReceiptId);
        Assert.Equal(FreshFindingEvidenceFixture.Now, continuation.PriorVerificationAt);
        Assert.Equal(fixture.Identity, fixture.Reviewer.LastDispatch!.CandidateIdentity);
        Assert.Contains("absent-from-recorded-review-context", continuation.Render(), StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Running, fixture.Reviewer.Status);
        Assert.NotEqual(GoalStatus.Completed, fixture.Goal.Status);
        var brief = fixture.Kernel.BuildTaskBrief(fixture.Goal.Id, fixture.Reviewer.Id, targetHeadCommit: FreshFindingEvidenceFixture.Candidate).Content;
        Assert.Contains(fresh.ReceiptId, brief, StringComparison.Ordinal);
        Assert.Contains("evidence_receipt:", brief, StringComparison.Ordinal);
        Assert.Contains("normal acceptance remain required", brief, StringComparison.Ordinal);

        fixture.Reload();
        fixture.Driver().AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(1, fixture.Launches);
        Assert.Single(fixture.Causes);
    }

    [Theory]
    [InlineData("zero-test")]
    [InlineData("partial")]
    [InlineData("failed")]
    [InlineData("wrong-candidate")]
    [InlineData("wrong-finding")]
    [InlineData("wrong-request")]
    [InlineData("wrong-outcome-request")]
    [InlineData("wrong-round")]
    [InlineData("wrong-basis")]
    [InlineData("superseded")]
    [InlineData("corrupt")]
    [InlineData("missing-exact-test")]
    public void InvalidReceiptCannotReleaseActualConductorHold(string invalid)
    {
        using var fixture = new FreshFindingEvidenceFixture();
        var receipt = fixture.Receipt(methods: invalid == "missing-exact-test" ? ["DifferentGuard"] : null);
        var arm = receipt.Arms!.Single();
        receipt = invalid switch
        {
            "zero-test" => receipt with { Arms = [arm with { ExecutedTestCount = 0 }] },
            "partial" => receipt with { Arms = [arm with { NotRunSelections = receipt.Request.Selections }] },
            "failed" => receipt with { Passed = false },
            "wrong-candidate" => receipt with { CandidateSha = new string('f', 40) },
            "wrong-finding" => receipt with { RequestDispositions = [receipt.RequestDispositions!.Single() with { FindingStableId = "other" }] },
            "wrong-request" => receipt with { RequestDispositions = [receipt.RequestDispositions!.Single() with { RequestIdentity = "other" }] },
            "wrong-round" => receipt with { FindingRoundFingerprint = "other" },
            "wrong-basis" => receipt with { ExecutionBasisIdentity = "other" },
            _ => receipt
        };
        fixture.Attach(receipt);
        if (invalid == "wrong-outcome-request")
            fixture.Kernel.RecordFindingEvidenceOutcome(fixture.Goal.Id, fixture.Tester.Id, FreshFindingEvidenceFixture.FindingId,
                new FindingEvidenceOutcome(true, receipt.ReceiptId, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence,
                    RequestedSelectionIdentity: "unrelated-request"), null);
        if (invalid == "corrupt") File.AppendAllText(arm.TestResultPaths!.Single(), "corrupted");
        if (invalid == "superseded")
            fixture.Kernel.RecordFindingEvidenceRequestDispositions(fixture.Goal.Id, fixture.Tester.Id,
                [receipt.RequestDispositions!.Single() with
                {
                    Decision = FindingEvidenceRequestDecision.Superseded,
                    Reason = "A later selection superseded this execution"
                }]);
        Assert.False(fixture.Select(out _, out _));

        var result = fixture.Driver().AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(0, fixture.Launches);
        Assert.Empty(fixture.Causes);
    }

    [Fact]
    public void CompleteRoundBatchesBothRequiredRequests()
    {
        using var fixture = new FreshFindingEvidenceFixture(twoRequests: true);
        var first = fixture.Receipt();
        fixture.Attach(first);
        fixture.Driver().AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(0, fixture.Launches);
        var second = fixture.Receipt("second-request", id: "second-receipt");
        fixture.Attach(second, "second-request");

        fixture.Driver().AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, fixture.Launches);
        Assert.Equal(new[] { first.ReceiptId, second.ReceiptId }.Order(),
            fixture.Reviewer.FindingEvidenceReviewContinuation!.Selections.Select(item => item.ReceiptId).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedCompositeRequiresEverySourceReceiptToBeComplete(bool partial)
    {
        using var fixture = new FreshFindingEvidenceFixture(twoRequests: true, compositeRequest: true);
        var first = fixture.Receipt();
        var second = fixture.Receipt("second-request", id: "second-receipt");
        if (partial) first = first with { Arms = [first.Arms!.Single() with { NotRunSelections = first.Request.Selections }] };
        fixture.RecordCompositeReuse(first, second);

        fixture.Driver().AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(partial ? 0 : 1, fixture.Launches);
        if (!partial) Assert.Equal(2, fixture.Reviewer.FindingEvidenceReviewContinuation!.Selections.Count);
    }

    [Fact]
    public void EvidenceExecutedBeforeVerdictButDeliveredAfterwardIsFresh()
    {
        using var fixture = new FreshFindingEvidenceFixture();
        fixture.Attach(fixture.Receipt());
        Assert.True(fixture.Tester.LastVerification!.CompletedAt < fixture.Reviewer.VerificationHistory.Last().CompletedAt);
        Assert.True(fixture.Select(out var selected, out var reason), reason);
        Assert.Equal(fixture.Reviewer.VerificationHistory.Last().CompletedAt, selected!.PriorVerificationAt);
    }

    [Fact]
    public void ExecutionAfterVerdictAlsoDeliversOneContinuation()
    {
        using var fixture = new FreshFindingEvidenceFixture();
        fixture.ReexecuteTesterAfterVerdict();
        fixture.Attach(fixture.Receipt());
        fixture.Driver().AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal(RetryCause.NewTestFinding, Assert.Single(fixture.Causes));
    }

    [Fact]
    public void FailedReviewerReceivesFreshEvidenceWithoutAnOperatorRetry()
    {
        using var fixture = new FreshFindingEvidenceFixture(retryReview: false);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Reviewer.Status);
        fixture.Attach(fixture.Receipt());

        fixture.Driver().AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, fixture.Launches);
        Assert.Equal(RetryCause.NewTestFinding, Assert.Single(fixture.Causes));
        Assert.Equal(RetryCause.NewTestFinding, fixture.Reviewer.FindingEvidenceReviewContinuation!.Cause);
        Assert.Single(fixture.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried && item.TaskId == fixture.Reviewer.Id));
        Assert.NotEqual(GoalStatus.Completed, fixture.Goal.Status);
    }

    [Fact]
    public void RemainingSourceFindingKeepsSourceCorrectionRoute()
    {
        using var fixture = new FreshFindingEvidenceFixture(sourceFinding: true);
        fixture.Attach(fixture.Receipt());
        Assert.False(fixture.Select(out _, out var reason));
        Assert.Equal("source-correction-or-required-request-unavailable", reason);
        fixture.Driver().AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(0, fixture.Launches);
        Assert.Empty(fixture.Causes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WrongGoalOrDispatchLineageCannotResume(bool wrongGoal)
    {
        using var fixture = new FreshFindingEvidenceFixture();
        fixture.Attach(fixture.Receipt());
        var snapshot = fixture.Kernel.ExportGoalSnapshot(fixture.Goal.Id);
        var tasks = snapshot.Tasks.Select(task => task.RequiredRole == AgentRole.Tester ? task with
        {
            DispatchHistory = wrongGoal ? task.DispatchHistory!.Select(dispatch => dispatch with { GoalId = GoalId.New() }).ToArray() : task.DispatchHistory,
            LastDispatch = wrongGoal ? task.LastDispatch! with { GoalId = GoalId.New() } : task.LastDispatch,
            LastVerification = wrongGoal ? task.LastVerification : task.LastVerification! with { DispatchStartedAt = FreshFindingEvidenceFixture.Now.AddDays(-1) },
            VerificationHistory = wrongGoal ? task.VerificationHistory : task.VerificationHistory!.Select(record => record with { DispatchStartedAt = FreshFindingEvidenceFixture.Now.AddDays(-1) }).ToArray()
        } : task).ToArray();
        fixture.Kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot with { Tasks = tasks }], []));
        var goal = fixture.Goal;
        Assert.False(FindingEvidenceReceiptSelector.TrySelectReviewContinuation(goal,
            goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer), FreshFindingEvidenceFixture.Candidate,
            fixture.Identity, request => request, ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(fixture.Settings), out _, out _));
        Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Driver().AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Equal(0, fixture.Launches);
        Assert.Empty(fixture.Causes);
    }

    [Fact]
    public void OrdinaryPeerReceiptAndLegacyUnknownDeliveryStayHeld()
    {
        using var fixture = new FreshFindingEvidenceFixture();
        fixture.Attach(fixture.Receipt());
        var snapshot = fixture.Kernel.ExportGoalSnapshot(fixture.Goal.Id);
        var changedTasks = snapshot.Tasks.Select(task => task.RequiredRole == AgentRole.Reviewer
            ? task with { DispatchHistory = task.DispatchHistory!.Select(dispatch => dispatch with { FindingEvidenceReviewContext = null }).ToArray() }
            : task).ToArray();
        var goal = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot with { Tasks = changedTasks }], [])).Goals.Single();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        Assert.False(FindingEvidenceReceiptSelector.TrySelectReviewContinuation(goal, reviewer,
            FreshFindingEvidenceFixture.Candidate, fixture.Identity, request => request,
            ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(fixture.Settings), out _, out var reason));
        Assert.Equal("review-context-or-lineage-unavailable", reason);
        Assert.NotNull(UnchangedCandidateRule.Evaluate(goal, reviewer, fixture.Identity));
    }

    [Fact]
    public void ExactReceiptAlreadyDeliveredCannotBuyAnotherReview()
    {
        using var fixture = new FreshFindingEvidenceFixture();
        fixture.Attach(fixture.Receipt());
        var snapshot = fixture.Kernel.ExportGoalSnapshot(fixture.Goal.Id);
        var tasks = snapshot.Tasks.Select(task => task.RequiredRole == AgentRole.Reviewer ? task with
        {
            DispatchHistory = task.DispatchHistory!.Select(dispatch => dispatch with
            {
                FindingEvidenceReviewContext = FindingEvidenceReviewContext.Capture(fixture.Goal, fixture.Reviewer,
                    FreshFindingEvidenceFixture.Candidate, dispatch.DispatchedAt)
            }).ToArray()
        } : task).ToArray();
        fixture.Kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot with { Tasks = tasks }], []));
        Assert.False(fixture.Select(out _, out var reason));
        Assert.Equal("evidence-already-delivered", reason);

        fixture.Driver().AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, fixture.Launches);
        Assert.Empty(fixture.Causes);
    }
}
