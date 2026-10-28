using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPreTesterBudgetCoverage : IDisposable
{
    private const string CandidateSha = "abc1234";
    private const string Project = "Infrastructure.Tests";
    private readonly string _root = ConductorDriverTests.CreateTempDirectory();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("CoveredTests", Project, "green", true, false)]
    [InlineData("CoveredTests", "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "green", true, false)]
    [InlineData("UncoveredTests", Project, "green", true, true)]
    [InlineData("CoveredTests", Project, "red", true, true)]
    [InlineData("CoveredTests", Project, "green", false, true)]
    [InlineData("CoveredTests", "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "green", false, true)]
    public void PreTesterBudgetOnlyCapsSelectionsCoveredByItsReceipt(
        string requestedClass, string project, string outcome, bool retainAuthority, bool shouldRun)
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        var coordinator = Coordinator();
        var original = retainAuthority ? ExecutePreTester(coordinator, kernel, goal, tester, outcome) : null;
        if (!retainAuthority)
            kernel.RecordFindingEvidenceRun(goal.Id, tester.Id,
                PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                    outcome, CandidateSha, "pre-tester-receipt",
                    ["Infrastructure.Tests:CoveredTests"], ["UncoveredTests"], null, [])));
        var originalResult = original is null ? null : File.ReadAllText(original.ResultPath);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence requested",
            findings: [EvidenceFindingWithRequest("focused evidence requested",
                category: FindingCategory.TestEvidence, project: project,
                classes: [requestedClass])]);

        var requests = new List<string>();
        var preTesterAttachments = new List<FindingEvidenceReceipt>();
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
            focusedEvidenceAttemptCoordinator: coordinator,
            runFocusedEvidence: (_, request) =>
            {
                requests.Add(request);
                return Green(request);
            },
            retryTaskWithCause: (goalId, taskId, message, round, cause) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: round, retryCause: cause),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceRequestDispositions: (goalId, taskId, dispositions) =>
                kernel.RecordFindingEvidenceRequestDispositions(goalId, taskId, dispositions),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
            {
                if (outcome.DecisionReason == "pre-tester-covered")
                    preTesterAttachments.Add(Assert.IsType<FindingEvidenceReceipt>(receipt));
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt);
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(shouldRun ? 1 : 0, requests.Count);
        // Evidence delivery retries clear LastVerification; the recorded round retains
        // its findings, dispositions and original receipt provenance in kernel history.
        var verification = reviewer.VerificationHistory.Last();
        var finding = Assert.Single(verification.MergedReviewFindings!);
        var evidenceOutcome = Assert.IsType<FindingEvidenceOutcome>(finding.EvidenceOutcome);
        Assert.True(evidenceOutcome.Honoured);
        Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence, evidenceOutcome.ResultReason);
        Assert.Equal(ReviewFindingState.Open, finding.State);
        Assert.Equal(FindingEvidenceExecutionState.ExecutedOnCandidate,
            FindingEvidenceExecutionClassifier.Classify(reviewer, finding, CandidateSha));
        if (original is not null)
            Assert.Equal(originalResult, File.ReadAllText(original.ResultPath));
        if (shouldRun)
        {
            Assert.Empty(preTesterAttachments);
            Assert.Equal($"{Project}:{requestedClass}", Assert.Single(requests));
            var executed = Assert.Single(verification.FindingEvidenceReceipts!);
            Assert.NotEqual("pre-tester-receipt", executed.ReceiptId);
            Assert.Equal(CandidateSha, executed.CandidateSha);
            Assert.Equal(new FindingEvidenceSelection(Project, requestedClass), Assert.Single(executed.Request.Selections));
        }
        else
        {
            var attached = Assert.Single(preTesterAttachments);
            Assert.Equal(CandidateSha, attached.CandidateSha);
            Assert.Equal(new FindingEvidenceSelection(Project, requestedClass), Assert.Single(attached.Request.Selections));
            Assert.Equal(JsonSerializer.Serialize(original!.OriginalFocusedReceipt), JsonSerializer.Serialize(attached));
            Assert.Equal(original.FocusedEvidenceReceiptId, evidenceOutcome.ReceiptId);
            Assert.Equal([attached.ReceiptId], evidenceOutcome.SourceReceiptIds);
            var disposition = Assert.Single(verification.FindingEvidenceRequestDispositions!
                .Where(item => item.FindingStableId == finding.StableId));
            Assert.Equal(FindingEvidenceRequestDecision.CoveredByReceipt, disposition.Decision);
            Assert.Equal(ConductorDriver.BuildFindingEvidenceIdentity(new FindingEvidenceRequest(
                [new(Project, requestedClass)])), disposition.RequestIdentity);
            Assert.Equal([attached.ReceiptId], disposition.SourceReceiptIds);
        }
    }

    [Fact]
    public void CoveredReviewerRequestStillDefersToWritableBlocker()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        var coordinator = Coordinator();
        ExecutePreTester(coordinator, kernel, goal, tester, "green");
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence requested; source defect",
            findings:
            [
                EvidenceFindingWithRequest("focused evidence requested", classes: ["CoveredTests"]),
                new ReviewFinding("source-defect", ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Test.cs", "Run"), "source defect",
                    FindingSeverity.Blocking, FindingCategory.Correctness)
            ]);
        TaskId? retriedTaskId = null;
        var attachments = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
            focusedEvidenceAttemptCoordinator: coordinator,
            runFocusedEvidence: (_, _) => throw new InvalidOperationException("Writable blocker must route to Developer before evidence execution."),
            retryTask: (goalId, taskId, message) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
            {
                if (outcome.DecisionReason == "pre-tester-covered") attachments++;
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt);
            },
            recordFindingEvidenceSuppressed: (goalId, taskId, sha, blockerIds, requestId, owner, reason, identity) =>
                kernel.RecordFindingEvidenceSuppressed(
                    goalId, taskId, sha, blockerIds, requestId, owner, reason, identity));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(developer.Id, retriedTaskId);
        Assert.Equal(0, attachments);
        Assert.Empty(reviewer.VerificationHistory.Last().FindingEvidenceReceipts ?? []);
        Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.FindingEvidenceSuppressed);
    }

    private ConductorParallelAcceptanceAttemptCoordinator Coordinator() => new(
        Path.Combine(_root, "attempts"), TrunkBranchName.Default, runInline: true,
        acquireStableSlotLease: (_, _) => null);

    private FocusedEvidenceRunResult Green(string request) =>
        ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(_root, request, CandidateSha) with
        { OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence };

    private ConductorParallelAcceptanceAttempt ExecutePreTester(
        ConductorParallelAcceptanceAttemptCoordinator coordinator, AgentOrchestratorKernel kernel,
        Goal goal, TaskSpec tester, string outcome)
    {
        const string request = Project + ":CoveredTests";
        // Let the execution owner capture original basis/result/TRX authority. A marker alone
        // cannot create that authority, even when it claims GREEN at the same candidate.
        var decision = coordinator.EvaluateFocusedEvidence(
            ConductorParallelAcceptanceCandidate.Create(goal, 0, ["tests/CoveredTests.cs"], CandidateSha, "main123"),
            ConductorAutonomyPolicy.Permissive, request,
            (_, selected, _, _) => outcome == "green" ? Green(selected)
                : CandidateRedFindingEvidence(selected, CandidateSha, "CoveredTests.Fails"),
            new ConductorFocusedEvidenceRequestContext("pre-tester-deferred", "pre-tester-budget", [],
                ExecutionBasisIdentity: ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(new()),
                ReceiptId: "pre-tester-receipt"));
        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, decision.Kind);
        var attempt = decision.Attempt;
        Assert.NotNull(attempt.OriginalFocusedReceipt);
        if (outcome == "green")
            Assert.NotNull(coordinator.ReadOriginalFocusedReceipt(attempt));
        coordinator.MarkReconciled(attempt);
        kernel.RecordFindingEvidenceRun(goal.Id, tester.Id,
            PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                outcome, CandidateSha, attempt.FocusedEvidenceReceiptId!,
                [request], ["UncoveredTests"], attempt.ResultPath, [])));
        return attempt;
    }
}
