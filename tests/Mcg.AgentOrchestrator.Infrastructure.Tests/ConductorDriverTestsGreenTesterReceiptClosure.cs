using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsGreenTesterReceiptClosure
{
    private const string CandidateSha = "abc1234abcdef1234abcdef1234abcdef1234abcd12";

    [Fact]
    public void TwoGreenReceiptsCompleteTesterWithoutRetryAndSurviveReload()
    {
        using var fixture = new ClosureFixture();
        fixture.Report(TwoEvidenceFindings());
        fixture.AdvanceToDecision();

        Assert.Empty(fixture.Retried);
        Assert.Equal(2, fixture.Requests.Count);
        AssertClosed(fixture.Goal, fixture.Requester, "receipt-first", "receipt-second");
        var restored = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(fixture.Kernel.ExportSnapshot()))!);
        AssertClosed(restored.GetGoal(fixture.Goal.Id), restored.GetTask(fixture.Goal.Id, fixture.Requester.Id),
            "receipt-first", "receipt-second");
    }

    [Fact]
    public void AdditionalCoverageFindingKeepsDeliveryRetry()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([FirstEvidenceFinding(), AdditionalFinding(FindingCategory.TestCoverage)]);
        fixture.AdvanceToDecision();
        AssertDeliveryRetry(fixture);
        Assert.Equal(2, fixture.Requester.VerificationHistory.Last().MergedReviewFindings!.Count);
    }

    [Fact]
    public void AdditionalCorrectnessFindingKeepsDeliveryRetry()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([FirstEvidenceFinding(), AdditionalFinding(FindingCategory.Correctness)]);
        fixture.AdvanceToDecision();
        AssertDeliveryRetry(fixture);
    }

    [Fact]
    public void NonValidOutcomeKeepsDeliveryRetryDespitePassingReceipt()
    {
        using var fixture = new ClosureFixture(outcomeReason: FindingEvidenceOutcomeReason.VacuousEvidence);
        fixture.Report([FirstEvidenceFinding()]);
        fixture.AdvanceToDecision();
        AssertDeliveryRetry(fixture);
        var verification = fixture.Requester.VerificationHistory.Last();
        var finding = Assert.Single(verification.MergedReviewFindings!);
        Assert.Equal(FindingEvidenceOutcomeReason.VacuousEvidence, finding.EvidenceOutcome!.ResultReason);
        Assert.True(Assert.Single(verification.FindingEvidenceReceipts!).Passed);
    }

    [Fact]
    public void DifferentReviewedCandidateKeepsDeliveryRetry()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([FirstEvidenceFinding()], reviewedSha: "def5678abcdef1234abcdef1234abcdef1234abcd12");
        fixture.AdvanceToDecision();
        AssertDeliveryRetry(fixture);
        Assert.Equal(CandidateSha, Assert.Single(fixture.Requester.VerificationHistory.Last().FindingEvidenceReceipts!).CandidateSha);
    }

    [Fact]
    public void ReportedWorkerBlockerKeepsDeliveryRetry()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([FirstEvidenceFinding()], blockers: "exact-blocker - missing prerequisite");
        fixture.AdvanceToDecision();
        AssertDeliveryRetry(fixture);
    }

    [Fact]
    public void ReportedFailingTestsKeepDeliveryRetry()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([FirstEvidenceFinding()], tests: "fail - ClosureTests.RejectsCandidate");
        fixture.AdvanceToDecision();
        AssertDeliveryRetry(fixture);
    }

    [Fact]
    public void MissingReviewedCandidateKeepsDeliveryRetry()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([FirstEvidenceFinding()], reviewedSha: null);
        fixture.AdvanceToDecision();
        AssertDeliveryRetry(fixture);
    }

    [Fact]
    public void ReviewerGreenReceiptsKeepDeliveryRetry()
    {
        using var fixture = new ClosureFixture(AgentRole.Reviewer);
        fixture.Report(TwoEvidenceFindings());
        fixture.AdvanceToDecision();
        AssertDeliveryRetry(fixture);
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public void ReattachedGreenReceiptsCloseTesterWithoutAnotherRunOrRetry()
    {
        using var fixture = new ClosureFixture();
        // The first round delivers green receipts but its separate blocker prevents closure.
        fixture.Report(TwoEvidenceFindings(), blockers: "exact-blocker - prerequisite not ready");
        fixture.AdvanceToDecision();
        AssertDeliveryRetry(fixture);
        // Retry clears LastVerification; the receipt-bearing round remains in history.
        var receiptIds = fixture.Requester.VerificationHistory.Last().FindingEvidenceReceipts!
            .Select(receipt => receipt.ReceiptId).Order(StringComparer.Ordinal).ToArray();

        fixture.Report(TwoEvidenceFindings());
        fixture.AdvanceToDecision();

        Assert.Equal([fixture.Requester.Id], fixture.Retried);
        Assert.Equal(2, fixture.Requests.Count);
        AssertClosed(fixture.Goal, fixture.Requester, "receipt-first", "receipt-second");
        Assert.Equal(receiptIds, fixture.Requester.LastVerification!.FindingEvidenceReceipts!
            .Select(receipt => receipt.ReceiptId).Order(StringComparer.Ordinal));
        Assert.Contains(fixture.Goal.Timeline, item =>
            item.Message.Contains("disposition=reused-covered-green", StringComparison.Ordinal));
    }

    [Fact]
    public void AdvisoryFindingRemainsOpenWhileBlockingEvidenceCloses()
    {
        using var fixture = new ClosureFixture();
        var advisory = EvidenceFinding("Optional improvement", "advisory") with
        {
            Severity = FindingSeverity.Advisory, Category = FindingCategory.CodeQuality
        };
        fixture.Report([FirstEvidenceFinding(), advisory]);
        fixture.AdvanceToDecision();
        Assert.Empty(fixture.Retried);
        AssertClosed(fixture.Goal, fixture.Requester, "receipt-first");
        Assert.Equal(advisory, fixture.Requester.LastVerification!.MergedReviewFindings!
            .Single(finding => finding.StableId == "advisory"));
    }

    [Fact]
    public void ClosureIsIdempotentOnLaterTicks()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([FirstEvidenceFinding()]);
        fixture.AdvanceToDecision();
        var verificationCount = fixture.Requester.VerificationHistory.Count;
        fixture.AdvanceOnce();
        Assert.Empty(fixture.Retried);
        Assert.Equal(verificationCount, fixture.Requester.VerificationHistory.Count);
        AssertClosed(fixture.Goal, fixture.Requester, "receipt-first");
    }

    private static ReviewFinding FirstEvidenceFinding() => EvidenceFindingWithRequest(
        "First focused receipt is missing", "receipt-first", classes: ["ConductorDriverTests"]);

    private static ReviewFinding[] TwoEvidenceFindings() =>
    [
        FirstEvidenceFinding(),
        EvidenceFindingWithRequest("Second focused receipt is missing", "receipt-second",
            project: "Core.Tests", classes: ["GoalLifecycleTests"])
    ];

    private static ReviewFinding AdditionalFinding(FindingCategory category) => EvidenceFindingWithRequest(
        "Another blocking finding remains", "additional", category, classes: ["ConductorDriverTests"]);

    private static void AssertDeliveryRetry(ClosureFixture fixture)
    {
        Assert.Equal([fixture.Requester.Id], fixture.Retried);
        Assert.Contains("finding evidence-on-demand:", Assert.Single(fixture.RetryMessages), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Goal.Timeline, item =>
            item.Message.StartsWith("FINDING_RECEIPT_CLOSURE", StringComparison.Ordinal));
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Requester.Status);
    }

    private static void AssertClosed(Goal goal, TaskSpec requester, params string[] findingIds)
    {
        Assert.Equal(WorkTaskStatus.Completed, requester.Status);
        var verification = requester.LastVerification!;
        Assert.True(verification.Succeeded);
        Assert.False(verification.WorkerResultPresent);
        Assert.Equal("conductor finding-receipt-closure", verification.Command);
        Assert.Equal(CandidateSha, verification.ReviewedCommit);
        var receipts = verification.FindingEvidenceReceipts!.ToDictionary(receipt => receipt.ReceiptId);
        var resolved = verification.MergedReviewFindings!
            .Where(finding => findingIds.Contains(finding.StableId)).ToArray();
        Assert.Equal(findingIds.Length, resolved.Length);
        Assert.All(resolved, finding =>
        {
            Assert.Equal(ReviewFindingState.Resolved, finding.State);
            var outcome = finding.EvidenceOutcome!;
            Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence, outcome.ResultReason);
            Assert.Contains(outcome.ReceiptId!, finding.Description, StringComparison.Ordinal);
            Assert.Contains(outcome.RequestedSelectionIdentity!, finding.Description, StringComparison.Ordinal);
            Assert.Contains(CandidateSha, finding.Description, StringComparison.Ordinal);
            Assert.True(receipts[outcome.ReceiptId!].Passed);
        });
        var note = Assert.Single(goal.Timeline.Where(item =>
            item.Message.StartsWith("FINDING_RECEIPT_CLOSURE", StringComparison.Ordinal)));
        Assert.Equal(ProgressKind.TaskNote, note.Kind);
        Assert.Equal(requester.Id, note.TaskId);
        var expectedReceipts = resolved.Select(finding => finding.EvidenceOutcome!.ReceiptId!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        Assert.Equal($"FINDING_RECEIPT_CLOSURE task={requester.Id.Value[..8]} " +
            $"findings={string.Join(',', findingIds.Order(StringComparer.Ordinal))} " +
            $"receipts={string.Join(',', expectedReceipts)} candidate={CandidateSha}; no worker dispatched", note.Message);
    }

    private sealed class ClosureFixture : IDisposable
    {
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly ConductorDriver _driver;
        private int _tick;
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Requester { get; }
        public List<TaskId> Retried { get; } = [];
        public List<string> RetryMessages { get; } = [];
        public List<string> Requests { get; } = [];

        public ClosureFixture(AgentRole role = AgentRole.Tester, FindingEvidenceOutcomeReason? outcomeReason = null)
        {
            (Kernel, Goal) = SoftwareGoal();
            Requester = Goal.Tasks.Single(task => task.RequiredRole == role);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Requester.Id))
                PassVerification(Kernel, Goal, task, hasCommittedChanges: task.RequiredRole == AgentRole.Developer);
            _driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    _root, runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                {
                    Requests.Add(request);
                    var green = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                        _root, request, CandidateSha);
                    return green with
                    {
                        Arms = green.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray(),
                        OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence
                    };
                },
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                {
                    Retried.Add(taskId);
                    RetryMessages.Add(message);
                    return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    Kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId,
                        outcomeReason is null ? outcome : outcome with { ResultReason = outcomeReason }, receipt));
        }

        public void Report(IReadOnlyList<ReviewFinding> findings, string? reviewedSha = CandidateSha,
            string blockers = "none", string tests = "deferred - acceptance owns execution")
        {
            DispatchTask(Kernel, Goal, Requester, "test", baseCommit: reviewedSha);
            var stdout = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: none", "commands: inspect focused behavior",
                $"tests: {tests}", "commit: none", $"blockers: {blockers}",
                $"findings: {JsonSerializer.Serialize(findings)}", "touched_anchors: []", "verdict: needs-work",
                "model_fit: fixture/model - adequate - source verification", "skills: none", "confidence: high",
                "END_WORKER_RESULT");
            Kernel.RecordDispatchExecutionResult(Goal.Id, Requester.Id, new TaskVerificationRecord(
                "test", "C:\\tmp", 0, stdout, "", DateTimeOffset.UtcNow,
                StandardOutputPath: "C:\\tmp\\worker.out.log", WorkerResultPresent: true));
        }

        public void AdvanceToDecision()
        {
            var retryCount = Retried.Count;
            // Bounded state-machine steps; completion/retry signals decide the outcome.
            for (var step = 0; step < 6 && Requester.Status != WorkTaskStatus.Completed && Retried.Count == retryCount; step++)
                AdvanceOnce();
        }

        public void AdvanceOnce()
        {
            _driver.BeginTick(Kernel, ++_tick);
            _driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
