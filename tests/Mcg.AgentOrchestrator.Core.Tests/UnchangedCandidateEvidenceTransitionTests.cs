using Mcg.AgentOrchestrator.Core;

public sealed class UnchangedCandidateEvidenceTransitionTests
{
    [Fact]
    public void UnrelatedLaterPeerReceiptDoesNotReleaseReviewer()
    {
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var peer = new TaskSpec(TaskId.New(), "Unrelated evidence", AgentRole.Tester);
        var goal = kernel.CreateGoal("Review currency", [reviewer, peer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var identity = new CandidateIdentity("patch", "base", "manifest");
        var at = DateTimeOffset.Parse("2026-10-27T18:28:26Z");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord("fixture", "review", "C:\\tmp", at, CandidateIdentity: identity));
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord("review", "C:\\tmp", 0,
            "WORKER_RESULT:\nblockers: none\nverdict: pass\nfindings: []\ntouched_anchors: []\nEND_WORKER_RESULT", "", at,
            WorkerResultPresent: true, CandidateIdentity: identity));
        kernel.RetryTask(goal.Id, reviewer.Id, "Unchanged context", RetryCause.UnchangedContextRepeat);
        kernel.RecordTaskDispatch(goal.Id, peer.Id, new TaskDispatchRecord("fixture", "test", "C:\\tmp", at.AddMinutes(1)));
        kernel.RecordTaskVerification(goal.Id, peer.Id, new TaskVerificationRecord("test", "C:\\tmp", 0, "ok", "", at.AddMinutes(2),
            FindingEvidenceReceipts: [new("unrelated", "other-candidate", new FindingEvidenceRequest([]), true, true, "green")]));

        Assert.NotNull(UnchangedCandidateRule.Evaluate(goal, reviewer, identity));
    }

    [Fact]
    public void TransitionBindsExactPriorVerificationAndSourceCandidate()
    {
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Bound context", [reviewer]);
        var identity = new CandidateIdentity("patch", "base", "manifest");
        var at = DateTimeOffset.Parse("2026-10-27T18:28:26Z");
        var prior = new TaskVerificationRecord("review", "C:\\tmp", 0, "", "", at,
            ReviewedCommit: "candidate", CandidateIdentity: identity, DispatchStartedAt: at.AddMinutes(-1));
        var transition = new FindingEvidenceReviewContinuation(goal.Id.Value, reviewer.Id.Value, "candidate", identity,
            at, at.AddMinutes(-1), ["old-execution"],
            [new("finding", "request", reviewer.Id.Value, at, at.AddMinutes(-1), "round", "new-receipt", "new-execution")]);

        Assert.True(transition.AppliesTo(goal, reviewer, prior, identity));
        Assert.False(transition.AppliesTo(goal, reviewer, prior with { CompletedAt = at.AddTicks(1) }, identity));
        Assert.False(transition.AppliesTo(goal, reviewer, prior, new CandidateIdentity("changed", "base", "manifest")));
        Assert.False((transition with { PriorExecutionIdentities = ["new-execution"] }).AppliesTo(goal, reviewer, prior, identity));
        Assert.Equal(RetryCause.NewTestFinding, transition.Cause);
        Assert.Equal(identity, prior.CandidateIdentity);
    }
}
