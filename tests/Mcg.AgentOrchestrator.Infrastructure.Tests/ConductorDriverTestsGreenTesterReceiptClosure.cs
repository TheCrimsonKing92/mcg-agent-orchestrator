using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

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
    public void AdditionalCoverageFindingRoutesTester()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([FirstEvidenceFinding(), AdditionalFinding(FindingCategory.TestCoverage)]);
        fixture.AdvanceToDecision();
        AssertTesterClosureRoute(fixture, FindingReceiptClosureDiagnosis.FindingCategoryNotTestEvidence);
        Assert.Equal(2, fixture.Requester.VerificationHistory.Last().MergedReviewFindings!.Count);
    }

    [Fact]
    public void AdditionalCorrectnessFindingRoutesTester()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([FirstEvidenceFinding(), AdditionalFinding(FindingCategory.Correctness)]);
        fixture.AdvanceToDecision();
        AssertTesterClosureRoute(fixture, FindingReceiptClosureDiagnosis.FindingCategoryNotTestEvidence);
    }

    [Fact]
    public void NonValidOutcomeRoutesTesterWithPassingReceipt()
    {
        using var fixture = new ClosureFixture(outcomeReason: FindingEvidenceOutcomeReason.VacuousEvidence);
        fixture.Report([FirstEvidenceFinding()]);
        fixture.AdvanceToDecision();
        AssertTesterClosureRoute(fixture, FindingReceiptClosureDiagnosis.EvidenceOutcomeInvalid);
        var verification = fixture.Requester.VerificationHistory.Last();
        var finding = Assert.Single(verification.MergedReviewFindings!);
        Assert.Equal(FindingEvidenceOutcomeReason.VacuousEvidence, finding.EvidenceOutcome!.ResultReason);
        Assert.True(Assert.Single(verification.FindingEvidenceReceipts!).Passed);
    }

    [Fact]
    public void DifferentReviewedCandidateRoutesTesterWithCurrentReceipt()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([FirstEvidenceFinding()], reviewedSha: "def5678abcdef1234abcdef1234abcdef1234abcd12");
        fixture.AdvanceToDecision();
        AssertTesterClosureRoute(fixture, FindingReceiptClosureDiagnosis.ReviewedCommitDiffers);
        Assert.Equal(CandidateSha, Assert.Single(fixture.Requester.VerificationHistory.Last().FindingEvidenceReceipts!).CandidateSha);
    }

    [Fact]
    public void ReportedWorkerBlockerRoutesTesterWithPassingReceipt()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([FirstEvidenceFinding()], blockers: "exact-blocker - missing prerequisite");
        fixture.AdvanceToDecision();
        AssertTesterClosureRoute(fixture, FindingReceiptClosureDiagnosis.WorkerBlockersReported);
    }

    [Fact]
    public void ReportedFailingTestsRouteTesterWithPassingReceipt()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([FirstEvidenceFinding()], tests: "fail - ClosureTests.RejectsCandidate");
        fixture.AdvanceToDecision();
        AssertTesterClosureRoute(fixture, FindingReceiptClosureDiagnosis.FailingTestsReported);
    }

    [Fact]
    public void MissingReviewedCandidateRoutesTesterWithCurrentReceipt()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([FirstEvidenceFinding()], reviewedSha: null);
        fixture.AdvanceToDecision();
        AssertTesterClosureRoute(fixture, FindingReceiptClosureDiagnosis.ReviewedCommitMissing);
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
        // The first round returns the passing receipts to the Tester because its blocker prevents closure.
        fixture.Report(TwoEvidenceFindings(), blockers: "exact-blocker - prerequisite not ready");
        fixture.AdvanceToDecision();
        AssertTesterClosureRoute(fixture, FindingReceiptClosureDiagnosis.WorkerBlockersReported);
        // Tester retry clears LastVerification; receipts remain in history.
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

    [Theory]
    [InlineData("none", FindingEvidenceOutcomeReason.VacuousEvidence)]
    [InlineData("exact-blocker - negative control needs new test code", FindingEvidenceOutcomeReason.ValidEvidence)]
    public void PassingReceiptWithOpenTestEvidenceRoutesTesterOnFirstDelivery(
        string blockers, FindingEvidenceOutcomeReason outcomeReason)
    {
        using var fixture = new ClosureFixture(outcomeReason: outcomeReason);
        // Inconclusive execution selects transient recovery before any finding-evidence run.
        fixture.Report([FirstEvidenceFinding()], blockers: blockers, tests: "deferred - new test code is required");
        fixture.AdvanceToDecision();

        Assert.Single(fixture.Requests);
        var receipt = Assert.Single(fixture.Requester.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.True(receipt.Accepted);
        Assert.True(receipt.Passed);
        Assert.Equal(CandidateSha, receipt.CandidateSha);
        AssertTesterClosureRoute(fixture, blockers == "none" ? FindingReceiptClosureDiagnosis.EvidenceOutcomeInvalid : FindingReceiptClosureDiagnosis.WorkerBlockersReported);
        Assert.Contains(receipt.ReceiptId, Assert.Single(fixture.RetryMessages), StringComparison.Ordinal);
        Assert.Contains("receipt-first", Assert.Single(fixture.RetryMessages), StringComparison.Ordinal);
        fixture.AdvanceOnce();
        Assert.Single(fixture.Retried);
    }

    [Fact]
    public void TesterReRaiseWithoutUpstreamDeveloperEscalatesWithEvidenceIdentities()
    {
        using var fixture = new ClosureFixture(includeDeveloper: false);
        fixture.Report([FirstEvidenceFinding()], blockers: "exact-blocker - new test code is required");
        fixture.AdvanceToDecision();

        Assert.Single(fixture.Requests);
        var receipt = Assert.Single(fixture.Requester.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.True(receipt.Accepted && receipt.Passed);
        Assert.Equal(ReviewFindingState.Open,
            Assert.Single(fixture.Requester.VerificationHistory.Last().MergedReviewFindings!).State);
        AssertTesterClosureRoute(fixture, FindingReceiptClosureDiagnosis.WorkerBlockersReported);
        fixture.Report([FirstEvidenceFinding()], blockers: "exact-blocker - new test code is still required");
        fixture.AdvanceToDecision();
        Assert.Equal([fixture.Requester.Id], fixture.Retried);
        var escalation = Assert.Single(fixture.Escalations);
        Assert.Contains(CandidateSha, escalation, StringComparison.Ordinal);
        Assert.Contains(receipt.ReceiptId, escalation, StringComparison.Ordinal);
        Assert.Contains("receipt-first", escalation, StringComparison.Ordinal);
        Assert.Contains("no upstream Developer", escalation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(FindingReceiptClosureDiagnosis.WorkerBlockersReported, escalation, StringComparison.Ordinal);
        AssertNoEvidenceDeliveryRetries(fixture);
    }

    [Fact]
    public void ReRaiseRoutesDeveloperWithReattachedReceipt()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([FirstEvidenceFinding()], blockers: "exact-blocker - new test code is required");
        fixture.AdvanceToDecision();
        AssertTesterClosureRoute(fixture, FindingReceiptClosureDiagnosis.WorkerBlockersReported);
        var receiptId = Assert.Single(fixture.Requester.VerificationHistory.Last().FindingEvidenceReceipts!).ReceiptId;

        fixture.Report([FirstEvidenceFinding()], blockers: "exact-blocker - new test code is still required");
        fixture.AdvanceToDecision();

        Assert.Single(fixture.Requests);
        Assert.Equal([fixture.Requester.Id, fixture.Developer!.Id], fixture.Retried);
        Assert.Equal([RetryCause.CriterionEvidenceOwnerMismatch, RetryCause.NewSourceFinding], fixture.RetryCauses);
        Assert.Contains("route=developer", fixture.RetryMessages.Last(), StringComparison.Ordinal);
        Assert.Contains(FindingReceiptClosureDiagnosis.WorkerBlockersReported, fixture.RetryMessages.Last(), StringComparison.Ordinal);
        Assert.Contains(receiptId, fixture.RetryMessages.Last(), StringComparison.Ordinal);
        Assert.Contains(fixture.Goal.Timeline, item =>
            item.Message.Contains("disposition=reused-covered-green", StringComparison.Ordinal));
        AssertNoEvidenceDeliveryRetries(fixture);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("rejected")]
    [InlineData("apparatus-failed")]
    [InlineData("executor-unavailable")]
    public void NonPassingDeliveriesKeepTesterRetryCap(string failure)
    {
        using var fixture = new ClosureFixture(evidenceFailure: failure);
        var finding = failure == "refused"
            ? EvidenceFindingWithRequest("Unsupported project", "receipt-first", project: "Unknown.Tests",
                classes: ["ConductorDriverTests"])
            : FirstEvidenceFinding();
        for (var round = 0; round < 3; round++)
        {
            fixture.Report([finding]);
            fixture.AdvanceToDecision();
        }

        Assert.Equal([fixture.Requester.Id, fixture.Requester.Id], fixture.Retried);
        Assert.All(fixture.RetryMessages, message =>
            Assert.StartsWith("finding evidence-on-demand:", message, StringComparison.Ordinal));
        Assert.Equal(2, FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
            fixture.Goal.Timeline, fixture.Requester.Id, CandidateSha, "receipt-first"));
        Assert.Contains("retry cap reached", Assert.Single(fixture.Escalations), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Developer!.Id, fixture.Retried);
        Assert.Equal(failure is "refused" or "executor-unavailable" ? 0 : 3, fixture.Requests.Count);
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

    private static void AssertTesterClosureRoute(ClosureFixture fixture, string code)
    {
        Assert.Equal([fixture.Requester.Id], fixture.Retried);
        Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, Assert.Single(fixture.RetryCauses));
        var message = Assert.Single(fixture.RetryMessages);
        Assert.Contains(CandidateSha, message, StringComparison.Ordinal);
        Assert.Contains("route=tester", message, StringComparison.Ordinal);
        Assert.Contains(code, message, StringComparison.Ordinal);
        Assert.Contains("Resolve each finding they satisfy", message, StringComparison.Ordinal);
        Assert.Empty(fixture.Escalations);
        AssertNoEvidenceDeliveryRetries(fixture);
        Assert.DoesNotContain(fixture.Goal.Timeline, item =>
            item.Message.StartsWith("FINDING_RECEIPT_CLOSURE", StringComparison.Ordinal));
        Assert.Contains(fixture.Requester.VerificationHistory.Last().MergedReviewFindings!, finding =>
            finding.State == ReviewFindingState.Open && finding.Severity != FindingSeverity.Advisory);
    }

    private static void AssertNoEvidenceDeliveryRetries(ClosureFixture fixture)
    {
        Assert.All(fixture.Requester.VerificationHistory.Last().MergedReviewFindings!, finding =>
            Assert.Equal(0, FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
                fixture.Goal.Timeline, fixture.Requester.Id, CandidateSha, finding.StableId)));

        Assert.DoesNotContain(fixture.RetryMessages, message =>
            message.StartsWith("finding evidence-on-demand:", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Escalations, message =>
            message.Contains("retry cap reached", StringComparison.OrdinalIgnoreCase));
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

    internal sealed class ClosureFixture : IDisposable
    {
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly ConductorDriver _driver;
        private int _tick;
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Requester { get; }
        public TaskSpec? Developer => Goal.Tasks.SingleOrDefault(task => task.RequiredRole == AgentRole.Developer);
        public List<TaskId> Retried { get; } = [];
        public List<string> RetryMessages { get; } = [];
        public List<RetryCause> RetryCauses { get; } = [];
        public List<string> Escalations { get; } = [];
        public List<string> Requests { get; } = [];
        public FindingReceiptClosureDiagnosis? ReceiptDiagnosis { get; private set; }

        public ClosureFixture(AgentRole role = AgentRole.Tester, FindingEvidenceOutcomeReason? outcomeReason = null,
            bool includeDeveloper = true, string? evidenceFailure = null)
        {
            if (includeDeveloper)
            {
                (Kernel, Goal) = SoftwareGoal();
            }
            else
            {
                Kernel = new AgentOrchestratorKernel();
                Goal = Kernel.CreateGoal("Passing evidence without a Developer",
                    [new TaskSpec(TaskId.New(), "Verify the change", role)]);
                Kernel.ActivateGoal(Goal.Id, DefaultAgents());
            }
            Requester = Goal.Tasks.Single(task => task.RequiredRole == role);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Requester.Id))
                PassVerification(Kernel, Goal, task, hasCommittedChanges: task.RequiredRole == AgentRole.Developer);
            _driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    _root, runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: evidenceFailure == "executor-unavailable" ? null : (_, request) =>
                {
                    Requests.Add(request);
                    if (evidenceFailure == "candidate-red")
                        return CandidateRedFindingEvidence(request, CandidateSha);
                    if (evidenceFailure is "rejected" or "apparatus-failed")
                        return new FocusedEvidenceRunResult(request,
                            Accepted: evidenceFailure != "rejected", Passed: false,
                            Summary: "selection apparatus did not produce passing evidence", Checks: [],
                            OutcomeReason: FindingEvidenceOutcomeReason.ApparatusFailure,
                            Rejection: evidenceFailure == "rejected"
                                ? new FocusedEvidenceRejection(FocusedEvidenceRejectionCode.SourceDiscoveryFailure,
                                    request, "source discovery failed before selection")
                                : null);
                    var green = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                        _root, request, CandidateSha);
                    return green with
                    {
                        Arms = green.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray(),
                        OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence
                    };
                },
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                {
                    Retried.Add(taskId);
                    RetryMessages.Add(message);
                    RetryCauses.Add(cause);
                    return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
                },
                writeEscalation: (_, _, message) => Escalations.Add(message),
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                {
                    Kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId,
                        outcomeReason is null ? outcome : outcome with { ResultReason = outcomeReason }, receipt);
                    ReceiptDiagnosis = FindingReceiptClosureDiagnosis.Evaluate(Goal, Requester, CandidateSha);
                });
        }

        public void CompleteDeveloper() => PassVerification(Kernel, Goal, Developer!, hasCommittedChanges: true);

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
            var escalationCount = Escalations.Count;
            for (var step = 0; step < 6 && Requester.Status != WorkTaskStatus.Completed &&
                Retried.Count == retryCount && Escalations.Count == escalationCount; step++)
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
