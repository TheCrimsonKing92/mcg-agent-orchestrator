using Mcg.AgentOrchestrator.Core;

public sealed class RetryAdmissionReceiptContextTests
{
    [Xunit.Theory]
    [Xunit.InlineData(1, false)]
    [Xunit.InlineData(13, true)]
    public void EvidenceIdentitiesDescribeOnlyThePreparedCandidate(int currentReceiptCount, bool truncated)
    {
        var candidate = new string('a', 40);
        var priorCandidate = new string('b', 40);
        var now = DateTimeOffset.Parse("2026-09-09T12:00:00Z");
        var task = new TaskSpec(TaskId.New(), "Review candidate", AgentRole.Reviewer);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Preserve useful retry evidence identities", [task]);
        var request = new FindingEvidenceRequest([new FindingEvidenceSelection("Core.Tests", "RetryAdmissionTests")]);
        var current = Enumerable.Range(0, currentReceiptCount)
            .Select(index => new FindingEvidenceReceipt($"current-{index:D2}", candidate.ToUpperInvariant(),
                request, true, true, "current receipt"))
            .ToArray();
        task.RecordVerification(new TaskVerificationRecord("test", "worktree", 0, "passed", "", now,
            FindingEvidenceReceipts: [new FindingEvidenceReceipt("obsolete", priorCandidate, request, true, true, "old"), .. current]));
        var dispatch = new TaskDispatchRecord("worker", "command", "worktree", now,
            BaseCommit: priorCandidate, ResultCommit: candidate);
        var receipt = new RetryAdmissionReceipt("retry", RetryCause.UnchangedContextRepeat,
            new RetryContextFingerprint(1, "fingerprint"), RetryAdmissionDecision.Prevented,
            RetryAdmissionRoute.SameRole, PaidRouteClassification.Paid, now, now);

        var enriched = RetryAdmissionReceiptContext.Enrich(goal, task, dispatch,
            new RetryAdmissionResult(RetryAdmissionDecision.Prevented, receipt)).Receipt;

        Assert.Equal(candidate, enriched.CandidateSha);
        Assert.Equal(current.Take(12).Select(item => item.ReceiptId), enriched.EvidenceIdentities);
        Assert.Equal(truncated, enriched.EvidenceIdentitiesTruncated);
        Assert.DoesNotContain("obsolete", enriched.EvidenceIdentities!);
    }
}
