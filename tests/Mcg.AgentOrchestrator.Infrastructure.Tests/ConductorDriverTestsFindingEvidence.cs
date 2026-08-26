using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Xml.Linq;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsFindingEvidence
{
    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_evidence_request_runs_focused_evidence_and_retries_reviewer_only")]
    public void ConductorDriverReviewerEvidenceRequestRunsFocusedEvidenceAndRetriesReviewerOnly()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var request = "Infrastructure.Tests:ConductorDriverTests";
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "missing focused conductor evidence",
            findings:
            [
                EvidenceFindingWithRequest(
                    "missing focused conductor evidence",
                    category: FindingCategory.TestEvidence,
                    classes: ["ConductorDriverTests"])
            ]);
        var focusedRuns = 0;
        var retriedTaskIds = new List<TaskId>();
        string? retryMessage = null;
        RetryRoundKind? retryRoundKind = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, actualRequest) =>
            {
                focusedRuns++;
                Assert.Equal(request, actualRequest);
                return new FocusedEvidenceRunResult(
                    actualRequest,
                    Accepted: true,
                    Passed: true,
                    Summary: "focused evidence passed: 1 check; receipts: C:\\tmp\\trx",
                    Checks:
                    [
                        new AcceptanceCheckResult(
                            "reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~ConductorDriverTests",
                            true,
                            0,
                            null,
                            ArtifactsPath: "C:\\tmp\\trx")
                    ]);
            },
            retryTaskWithRoundKind: (gid, tid, msg, roundKind) =>
            {
                retriedTaskIds.Add(tid);
                retryMessage = msg;
                retryRoundKind = roundKind;
                return kernel.RetryTask(gid, tid, msg, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (gid, tid, msg) => kernel.RecordFindingEvidenceRequest(gid, tid, msg),
            recordFindingEvidenceRun: (gid, tid, msg) => kernel.RecordFindingEvidenceRun(gid, tid, msg),
            recordFindingEvidenceOutcome: (gid, tid, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(gid, tid, stableId, outcome, receipt));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Equal([reviewer.Id], retriedTaskIds);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Contains("finding evidence-on-demand", retryMessage);
        var receipt = Assert.Single(reviewer.VerificationHistory.SelectMany(item => item.FindingEvidenceReceipts ?? []));
        Assert.Contains("C:\\tmp\\trx", receipt.Summary);
        var evidenceOutcome = Assert.Single(reviewer.VerificationHistory.Last().MergedReviewFindings!).EvidenceOutcome;
        Assert.Equal(FindingEvidenceOutcomeReason.Unknown, evidenceOutcome?.ResultReason);
        Assert.Equal(RetryRoundKind.Mechanical, retryRoundKind);
        Assert.Equal(RetryRoundKind.Mechanical, reviewer.PendingRetryRoundKind);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.Contains("reason=unknown", StringComparison.Ordinal));
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.Contains("outcome=unknown", StringComparison.Ordinal));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Theory]
    [Xunit.InlineData("cdbed959", FindingCategory.Correctness)]
    [Xunit.InlineData("cdde61cc", FindingCategory.Unspecified)]
    public void StructuredRequestIsSuppressedByUnresolvedWritableBlocker(
        string incident,
        FindingCategory category)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        const string evidenceBlocker = "Infrastructure.Tests ConductorDriverTests receipts are missing.";
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            $"{evidenceBlocker}; defect remains",
            findings:
            [
                EvidenceFindingWithRequest(
                    evidenceBlocker,
                    category: FindingCategory.Correctness,
                    classes: ["ConductorDriverTests"]),
                new ReviewFinding(
                    "other-blocker",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Test.cs", "Run"),
                    "defect remains",
                    FindingSeverity.Blocking,
                    category)
            ]);
        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                Assert.Equal("Infrastructure.Tests:ConductorDriverTests", request);
                return new FocusedEvidenceRunResult(request, true, true, "focused request passed", []);
            },
            retryTask: (goalId, taskId, message) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            recordFindingEvidenceSuppressed: (goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity) =>
            {
                kernel.RecordFindingEvidenceSuppressed(
                    goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity);
                kernel.RecordFindingEvidenceSuppressed(
                    goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity);
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(developer.Id, retriedTaskId);
        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        var suppression = Assert.Single(goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.FindingEvidenceSuppressed));
        Assert.Equal(reviewer.Id, suppression.TaskId);
        Assert.Contains($"goal_id={goal.Id}", suppression.Message, StringComparison.Ordinal);
        Assert.Contains($"task_id={reviewer.Id}", suppression.Message, StringComparison.Ordinal);
        Assert.Contains("candidate_sha=abc1234", suppression.Message, StringComparison.Ordinal);
        Assert.Contains("blocker_ids=missing-receipts,other-blocker", suppression.Message, StringComparison.Ordinal);
        Assert.Contains("evidence_request_id=evidence-request-", suppression.Message, StringComparison.Ordinal);
        Assert.Contains("chosen_owner=Developer", suppression.Message, StringComparison.Ordinal);
        Assert.Contains("reason=unresolved-writable-blockers-on-unchanged-candidate", suppression.Message, StringComparison.Ordinal);
        Assert.Contains("suppression_identity=evidence-suppression-", suppression.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.FindingEvidenceRunRecorded);
        Assert.Contains(
            "finding-evidence-suppressed",
            kernel.BuildTaskBrief(goal.Id, developer.Id).Content,
            StringComparison.Ordinal);
        Assert.Equal("Finding evidence suppressed", DashboardDisplayNames.Display(ProgressKind.FindingEvidenceSuppressed));
        Assert.Contains(incident, new[] { "cdbed959", "cdde61cc" });
    }

    [Xunit.Fact]
    public void WritableEvidenceRequestingFindingSuppressesItsOwnFocusedRun()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "The writable defect needs source repair and carries a focused evidence request.",
            findings:
            [
                EvidenceFindingWithRequest(
                    "The writable defect needs source repair and carries a focused evidence request.",
                    id: "self-owned-writable-request",
                    category: FindingCategory.Correctness,
                    classes: ["ConductorDriverTests"])
            ]);
        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "focused request passed", []);
            },
            retryTask: (goalId, taskId, message) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            recordFindingEvidenceSuppressed: (goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity) =>
                kernel.RecordFindingEvidenceSuppressed(
                    goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(developer.Id, retriedTaskId);
        var suppression = Assert.Single(goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.FindingEvidenceSuppressed));
        Assert.Contains("blocker_ids=self-owned-writable-request", suppression.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void SuppressionIdentityIgnoresHarmlessFindingDescriptionRewording()
    {
        var first = CaptureReviewerSuppression(
            "abc1234",
            EvidenceFindingWithRequest(
                "Focused evidence is required.",
                id: "acceptance-request",
                category: FindingCategory.AcceptanceOwned,
                classes: ["ConductorDriverTests"]),
            new ReviewFinding(
                "source-defect",
                ReviewFindingState.Open,
                new ReviewFindingLocation("src/Test.cs", "Run"),
                "Source defect remains.",
                FindingSeverity.Blocking,
                FindingCategory.Correctness));
        var reworded = CaptureReviewerSuppression(
            "abc1234",
            EvidenceFindingWithRequest(
                "Please run the same focused evidence.",
                id: "acceptance-request",
                category: FindingCategory.AcceptanceOwned,
                classes: ["ConductorDriverTests"]),
            new ReviewFinding(
                "source-defect",
                ReviewFindingState.Open,
                new ReviewFindingLocation("src/Test.cs", "Run"),
                "The same source defect is still open, phrased differently.",
                FindingSeverity.Blocking,
                FindingCategory.Correctness));

        Assert.Equal(0, first.FocusedRuns);
        Assert.Equal(0, reworded.FocusedRuns);
        Assert.Equal(Assert.Single(first.Suppressions).Identity, Assert.Single(reworded.Suppressions).Identity);
    }

    [Xunit.Fact]
    public void PartialWritableBlockerResolutionStillSuppressesFocusedRun()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var firstLocation = new ReviewFindingLocation("src/First.cs", "Run");
        var secondLocation = new ReviewFindingLocation("src/Second.cs", "Run");
        var requestFinding = EvidenceFindingWithRequest(
            "Focused evidence is required.",
            id: "acceptance-request",
            category: FindingCategory.AcceptanceOwned,
            classes: ["ConductorDriverTests"]);
        var firstBlocker = new ReviewFinding(
            "first-source-defect",
            ReviewFindingState.Open,
            firstLocation,
            "First source defect remains.",
            FindingSeverity.Blocking,
            FindingCategory.Correctness);
        var secondBlocker = new ReviewFinding(
            "second-source-defect",
            ReviewFindingState.Open,
            secondLocation,
            "Second source defect remains.",
            FindingSeverity.Blocking,
            FindingCategory.TestCoverage);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "Focused evidence and two source repairs are required.",
            findings: [requestFinding, firstBlocker, secondBlocker],
            reviewedCommit: "abc1234");
        kernel.RetryTask(goal.Id, reviewer.Id, "recheck partially resolved source blockers");
        DispatchTask(
            kernel,
            goal,
            reviewer,
            "review",
            reviewFindingTouchedAnchors: [firstLocation, secondLocation],
            baseCommit: "abc1234");
        FailStructuredReviewerRound(
            kernel,
            goal,
            reviewer,
            "review",
            [requestFinding, firstBlocker with { State = ReviewFindingState.Resolved }, secondBlocker]);
        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "focused request passed", []);
            },
            retryTask: (goalId, taskId, message) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordFindingEvidenceSuppressed: (goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity) =>
                kernel.RecordFindingEvidenceSuppressed(
                    goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(developer.Id, retriedTaskId);
        var suppression = Assert.Single(goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.FindingEvidenceSuppressed));
        Assert.Contains("blocker_ids=second-source-defect", suppression.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("first-source-defect", suppression.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ChangedWritableBlockerSetCreatesNewSuppressionIdentity()
    {
        var request = EvidenceFindingWithRequest(
            "Focused evidence is required.",
            id: "acceptance-request",
            category: FindingCategory.AcceptanceOwned,
            classes: ["ConductorDriverTests"]);
        var firstBlocker = new ReviewFinding(
            "first-source-defect",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/First.cs", "Run"),
            "First source defect remains.",
            FindingSeverity.Blocking,
            FindingCategory.Correctness);
        var secondBlocker = new ReviewFinding(
            "second-source-defect",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/Second.cs", "Run"),
            "Second source defect remains.",
            FindingSeverity.Blocking,
            FindingCategory.TestCoverage);

        var first = CaptureReviewerSuppression("abc1234", request, firstBlocker);
        var changed = CaptureReviewerSuppression("abc1234", request, firstBlocker, secondBlocker);

        Assert.NotEqual(Assert.Single(first.Suppressions).Identity, Assert.Single(changed.Suppressions).Identity);
        Assert.Equal(["first-source-defect"], Assert.Single(first.Suppressions).BlockerIds);
        Assert.Equal(
            ["first-source-defect", "second-source-defect"],
            Assert.Single(changed.Suppressions).BlockerIds);
    }

    [Xunit.Fact]
    public void UnavailableCandidateShaSuppressesFocusedRunWhileWritableBlockerRemains()
    {
        var capture = CaptureReviewerSuppression(
            "not-a-candidate-sha",
            EvidenceFindingWithRequest(
                "Focused evidence is required.",
                id: "acceptance-request",
                category: FindingCategory.AcceptanceOwned,
                classes: ["ConductorDriverTests"]),
            new ReviewFinding(
                "source-defect",
                ReviewFindingState.Open,
                new ReviewFindingLocation("src/Test.cs", "Run"),
                "Source defect remains.",
                FindingSeverity.Blocking,
                FindingCategory.Correctness));

        Assert.Equal(0, capture.FocusedRuns);
        Assert.Equal(AgentRole.Developer, capture.RetriedRole);
        var suppression = Assert.Single(capture.Suppressions);
        Assert.Equal("unavailable", suppression.CandidateSha);
        Assert.Equal("candidate-sha-unavailable-with-unresolved-writable-blockers", suppression.Reason);
    }

    [Xunit.Fact]
    public void MultipleEvidenceRequestsProduceIndependentSuppressionIdentities()
    {
        var capture = CaptureReviewerSuppression(
            "abc1234",
            EvidenceFindingWithRequest(
                "Infrastructure evidence is required.",
                id: "infrastructure-request",
                category: FindingCategory.AcceptanceOwned,
                classes: ["ConductorDriverTests"]),
            EvidenceFindingWithRequest(
                "Core evidence is required.",
                id: "core-request",
                category: FindingCategory.AcceptanceOwned,
                project: "Core.Tests",
                classes: ["ReviewFindingRoutingTests"]),
            new ReviewFinding(
                "source-defect",
                ReviewFindingState.Open,
                new ReviewFindingLocation("src/Test.cs", "Run"),
                "Source defect remains.",
                FindingSeverity.Blocking,
                FindingCategory.Correctness));

        Assert.Equal(0, capture.FocusedRuns);
        Assert.Equal(AgentRole.Developer, capture.RetriedRole);
        Assert.Equal(2, capture.Suppressions.Length);
        Assert.Equal(2, capture.Suppressions.Select(item => item.RequestId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, capture.Suppressions.Select(item => item.Identity).Distinct(StringComparer.Ordinal).Count());
        Assert.All(capture.Suppressions, item => Assert.Equal(["source-defect"], item.BlockerIds));
    }

    [Xunit.Fact]
    public void StructuredRequestRunsWhenOnlyAdvisoryWritableFindingRemains()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "focused receipt missing",
            findings:
            [
                EvidenceFindingWithRequest("focused receipt missing", classes: ["ConductorDriverTests"]),
                new ReviewFinding(
                    "advisory-cleanup",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Test.cs", "Run"),
                    "Optional cleanup.",
                    FindingSeverity.Advisory,
                    FindingCategory.Correctness)
            ]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "focused request passed", []);
            },
            retryTask: (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.FindingEvidenceSuppressed);
    }

    [Xunit.Fact]
    public void StructuredRequestRunsForNewCandidateShaDespitePriorWritableBlocker()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "focused receipt missing; source defect remains",
            findings:
            [
                EvidenceFindingWithRequest(
                    "focused receipt missing",
                    category: FindingCategory.AcceptanceOwned,
                    classes: ["ConductorDriverTests"]),
                new ReviewFinding(
                    "source-defect",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Test.cs", "Run"),
                    "Source defect remains.",
                    FindingSeverity.Blocking,
                    FindingCategory.Correctness)
            ],
            reviewedCommit: "abc1234");
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("def5678"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "new candidate evidence passed", []);
            },
            retryTask: (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.FindingEvidenceSuppressed);
    }

    [Xunit.Fact]
    public void StructuredRequestRunsAfterEveryWritableBlockerIsExplicitlyResolved()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var sourceLocation = new ReviewFindingLocation("src/Test.cs", "Run");
        var requestFinding = EvidenceFindingWithRequest(
            "focused receipt missing",
            category: FindingCategory.AcceptanceOwned,
            classes: ["ConductorDriverTests"]);
        var sourceFinding = new ReviewFinding(
            "source-defect",
            ReviewFindingState.Open,
            sourceLocation,
            "Source defect remains.",
            FindingSeverity.Blocking,
            FindingCategory.Correctness);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "focused receipt missing; source defect remains",
            findings: [requestFinding, sourceFinding],
            reviewedCommit: "abc1234");
        kernel.RetryTask(goal.Id, reviewer.Id, "recheck resolved source blocker");
        DispatchTask(
            kernel,
            goal,
            reviewer,
            "review",
            reviewFindingTouchedAnchors: [sourceLocation],
            baseCommit: "abc1234");
        FailStructuredReviewerRound(
            kernel,
            goal,
            reviewer,
            "review",
            [requestFinding, sourceFinding with { State = ReviewFindingState.Resolved }]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "resolved blocker evidence passed", []);
            },
            retryTask: (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.FindingEvidenceSuppressed);
    }

    [Xunit.Fact]
    public void TesterStructuredRequestReceivesFindingBoundReceiptInRetryContext()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer);
        DispatchTask(kernel, goal, tester, "test");
        var finding = EvidenceFindingWithRequest(
            "Conductor routing needs an executed receipt.",
            id: "tester-evidence",
            category: FindingCategory.Correctness,
            project: "Mcg.AgentOrchestrator.Infrastructure.Tests",
            classes: ["ConductorDriverTests"]);
        var output = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: test",
            "tests: fail - receipt missing",
            "blockers: exact-blocker - receipt missing",
            $"findings: {JsonSerializer.Serialize(new[] { finding })}",
            "touched_anchors: []",
            "model_fit: test/test - adequate - fixture - fixture",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            tester.Id,
            new TaskVerificationRecord(
                "test", "C:\\tmp", 1, output, "", DateTimeOffset.UtcNow, WorkerResultPresent: true));

        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "tester requested evidence passed", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        var testerBrief = kernel.BuildTaskBrief(goal.Id, tester.Id).Content;
        Assert.Contains("evidence_receipt:", testerBrief, StringComparison.Ordinal);
        Assert.Contains("tester requested evidence passed", testerBrief, StringComparison.Ordinal);
        var nonRequesterBrief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;
        Assert.Contains("evidence_index:", nonRequesterBrief, StringComparison.Ordinal);
        Assert.DoesNotContain("evidence_receipt:", nonRequesterBrief, StringComparison.Ordinal);
        Assert.DoesNotContain("tester requested evidence passed", nonRequesterBrief, StringComparison.Ordinal);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == tester.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.Contains("role=Tester", StringComparison.Ordinal) &&
            evt.Message.Contains("finding_id=tester-evidence", StringComparison.Ordinal));
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == tester.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.Contains("role=Tester", StringComparison.Ordinal) &&
            evt.Message.Contains("finding_id=tester-evidence", StringComparison.Ordinal));
        var recorded = tester.VerificationHistory.Last().MergedReviewFindings!.Single(item => item.StableId == "tester-evidence");
        Assert.True(recorded.EvidenceOutcome?.Honoured);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Mcg.AgentOrchestrator.Infrastructure.Tests", "Infrastructure.Tests")]
    [Xunit.InlineData("Mcg.AgentOrchestrator.Core.Tests", "Core.Tests")]
    [Xunit.InlineData("Infrastructure.Tests", "Infrastructure.Tests")]
    [Xunit.InlineData("Core.Tests", "Core.Tests")]
    [Xunit.InlineData("Infrastructure", "Infrastructure.Tests")]
    [Xunit.InlineData("Core", "Core.Tests")]
    [Xunit.InlineData("C:\\repo\\tests\\Mcg.AgentOrchestrator.Infrastructure.Tests\\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "Infrastructure.Tests")]
    public void ReviewerStructuredRequestAcceptsEveryFocusedEvidenceProjectForm(
        string project,
        string canonicalProject)
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The project form must reach focused evidence execution.",
            id: "project-form",
            project: project,
            classes: ["ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                Assert.Equal($"{canonicalProject}:ConductorDriverTests", request);
                return new FocusedEvidenceRunResult(request, true, true, "project form accepted", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == "project-form")
            .EvidenceOutcome;
        Assert.True(outcome?.Honoured);
        Assert.Null(outcome?.Reason);
    }

    [Xunit.Fact]
    public void ReviewerStructuredRequestRoundTripsSystemEmittedFullyQualifiedName()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The system-emitted selection must round-trip unchanged.",
            id: "system-round-trip",
            classes: ["FullyQualifiedName~ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        string? observedRequest = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                observedRequest = request;
                return new FocusedEvidenceRunResult(request, true, true, "round-trip accepted", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(
            "Infrastructure.Tests:FullyQualifiedName~ConductorDriverTests",
            observedRequest);
        Assert.Equal(
            "FullyQualifiedName~ConductorDriverTests",
            reviewer.VerificationHistory.Last().FindingEvidenceReceipts!.Single()
                .Request.Selections.Single().TestClass);
    }

    [Xunit.Fact]
    public void ReviewerStructuredRequestPreservesExactFilterTokenWhitespace()
    {
        const string originalToken = "  FullyQualifiedName~MissingSelectionTests.MissingMethod  ";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The exact structured token must reach typed executor rejection.",
            id: "exact-structured-token",
            classes: [originalToken]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        string? observedRequest = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                observedRequest = request;
                return new FocusedEvidenceRunResult(
                    request,
                    Accepted: false,
                    Passed: false,
                    Summary: "selection rejected",
                    Checks: [],
                    Rejection: new FocusedEvidenceRejection(
                        FocusedEvidenceRejectionCode.UnresolvableSelection,
                        originalToken,
                        "selection rejected"));
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal("Infrastructure.Tests:" + originalToken, observedRequest);
        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == "exact-structured-token")
            .EvidenceOutcome;
        Assert.Equal(FindingEvidenceNotHonouredReason.UnparseableSelection, outcome?.Reason);
        Assert.Contains(originalToken, outcome?.Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ReviewerStructuredRequestKeepsMultipleFqnSelectionsExecutorValid()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "Each structured selection must remain a complete executor item.",
            id: "multiple-fqn-selections",
            classes:
            [
                "FullyQualifiedName~GoalAcceptanceVerifierTests",
                "FullyQualifiedName~ConductorDriverTests"
            ]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        string? observedRequest = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                observedRequest = request;
                return new FocusedEvidenceRunResult(request, true, true, "selections accepted", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(
            "Infrastructure.Tests:FullyQualifiedName~ConductorDriverTests; " +
            "Infrastructure.Tests:FullyQualifiedName~GoalAcceptanceVerifierTests",
            observedRequest);
    }

    [Xunit.Fact]
    public void ReviewerRequestSharedFileAddsSiblingTestClasses()
    {
        var request = CaptureNormalizedFindingEvidenceRequest(
            ["GoalAcceptanceVerifierTests"],
            (_, project, requestedClass) =>
                FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
                    InfrastructureTestSupport.FindRepositoryRoot(),
                    project,
                    requestedClass));

        Assert.Contains("Infrastructure.Tests:GoalAcceptanceVerifierTests", request, StringComparison.Ordinal);
        Assert.Contains(
            "Infrastructure.Tests:GoalAcceptanceVerifierDotnetBuildSlotTests",
            request,
            StringComparison.Ordinal);
        Assert.Contains("Infrastructure.Tests:AcceptanceOutputCaptureTests", request, StringComparison.Ordinal);
        Assert.Contains("Infrastructure.Tests:HermeticVerificationEnvironmentTests", request, StringComparison.Ordinal);
        Assert.Contains("Infrastructure.Tests:RealProcessShardAlphaSmokeTests", request, StringComparison.Ordinal);
        Assert.Contains("Infrastructure.Tests:RealProcessShardBetaSmokeTests", request, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ReviewerRequestSingleClassFileKeepsOriginalSelection()
    {
        var request = CaptureNormalizedFindingEvidenceRequest(
            ["ConductorDriverTests"],
            (_, project, requestedClass) =>
                FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
                    InfrastructureTestSupport.FindRepositoryRoot(),
                    project,
                    requestedClass));

        Assert.Equal("Infrastructure.Tests:ConductorDriverTests", request);
    }

    [Xunit.Fact]
    public void ReviewerRequestSiblingExpansionContainsEveryOriginalSelection()
    {
        string[] filters =
        [
            "GoalAcceptanceVerifierTests",
            "FullyQualifiedName~GoalAcceptanceVerifierTests",
            "FullyQualifiedName~GoalAcceptanceVerifierTests|FullyQualifiedName~ConductorDriverTests",
            "GoalAcceptanceVerifierTests.MissingMethod",
            "ThisClassIsNotDeclaredAnywhereTests"
        ];

        foreach (var filter in filters)
        {
            var before = CaptureNormalizedFindingEvidenceRequest([filter]);
            var after = CaptureNormalizedFindingEvidenceRequest(
                [filter],
                (_, project, requestedClass) =>
                    FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
                        InfrastructureTestSupport.FindRepositoryRoot(),
                        project,
                        requestedClass));

            var beforeSelections = before.Split("; ", StringSplitOptions.RemoveEmptyEntries);
            var afterSelections = after.Split("; ", StringSplitOptions.RemoveEmptyEntries);
            Assert.All(beforeSelections, selection => Assert.Contains(selection, afterSelections));
        }
    }

    [Xunit.Fact]
    public void ReviewerRequestUnresolvedClassKeepsOriginalSelection()
    {
        var request = CaptureNormalizedFindingEvidenceRequest(
            ["ThisClassIsNotDeclaredAnywhereTests"],
            (_, project, requestedClass) =>
                FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
                    InfrastructureTestSupport.FindRepositoryRoot(),
                    project,
                    requestedClass));

        Assert.Equal("Infrastructure.Tests:ThisClassIsNotDeclaredAnywhereTests", request);
    }

    private static string CaptureNormalizedFindingEvidenceRequest(
        IReadOnlyList<string> classes,
        Func<Goal, string, string, IReadOnlyList<string>>? resolveSiblingClasses = null)
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "Normalize the focused evidence selections.",
            id: "normalize-focused-evidence",
            classes: classes.ToArray());
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        string? observedRequest = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                observedRequest = request;
                return new FocusedEvidenceRunResult(request, true, true, "selection normalized", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            resolveFindingEvidenceSiblingClasses: resolveSiblingClasses);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        return Assert.IsType<string>(observedRequest);
    }

    [Xunit.Fact]
    public void ReviewerStructuredRequestRoundTripsSystemEmittedCompositeFilter()
    {
        var emitted = ConductorDriver.BuildPreReviewEvidenceContext(
            "abc1234",
            ["src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.OperatorShell.cs"]);
        var emittedRequest = Assert.IsType<string>(emitted.FocusedRequest);
        var separator = emittedRequest.IndexOf(':', StringComparison.Ordinal);
        var project = emittedRequest[..separator];
        var originalFilter = emittedRequest[(separator + 1)..].TrimStart();
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The planner-emitted composite filter must round-trip unchanged.",
            id: "system-composite-round-trip",
            project: project,
            classes: [originalFilter]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        string? observedRequest = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                observedRequest = request;
                return new FocusedEvidenceRunResult(request, true, true, "composite accepted", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Contains("|", originalFilter, StringComparison.Ordinal);
        Assert.Contains("Category!=HostIntegration", originalFilter, StringComparison.Ordinal);
        Assert.Equal(project + ":" + originalFilter, observedRequest);
    }

    [Xunit.Fact]
    public void ReviewerStructuredRequestPreservesUnpaddedExactFilterToken()
    {
        const string originalToken = "FullyQualifiedName~ConductorDriverTests.MissingMethod";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "An unresolvable token must retain its exact spelling.",
            id: "unpadded-exact-token",
            classes: [originalToken]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                Assert.Equal("Infrastructure.Tests:" + originalToken, request);
                return new FocusedEvidenceRunResult(
                    request,
                    Accepted: false,
                    Passed: false,
                    Summary: "selection rejected",
                    Checks: [],
                    Rejection: new FocusedEvidenceRejection(
                        FocusedEvidenceRejectionCode.UnresolvableSelection,
                        originalToken,
                        "selection rejected"));
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == "unpadded-exact-token")
            .EvidenceOutcome;
        Assert.Equal(FindingEvidenceNotHonouredReason.UnparseableSelection, outcome?.Reason);
        Assert.Contains(originalToken, outcome?.Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void SourceDiscoveryFailureUsesTypedSelectionApparatusDisposition()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "Unreadable source is an apparatus failure, not an invalid selector.",
            id: "source-discovery-apparatus",
            classes: ["FullyQualifiedName~ConductorDriverTests.MissingMethod"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: false,
                Passed: false,
                Summary: "source discovery failed",
                Checks: [],
                Rejection: new FocusedEvidenceRejection(
                    FocusedEvidenceRejectionCode.SourceDiscoveryFailure,
                    " FullyQualifiedName~ConductorDriverTests.MissingMethod",
                    "source discovery failed")),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == "source-discovery-apparatus")
            .EvidenceOutcome;
        Assert.False(outcome?.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.SelectionApparatusFailure, outcome?.Reason);
    }

    [Xunit.Fact]
    public void UnresolvableSelectionDoesNotRerunOnNextRound()
    {
        const string originalToken = "FullyQualifiedName~CliCommandTests.PersistentRunnerCommands";
        const string stableId = "unresolvable-selection";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "An unresolvable selection must be replaced before it runs again.",
            id: stableId,
            classes: [originalToken]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var observedRequests = new List<string>();
        string? blockingRetryMessage = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                observedRequests.Add(request);
                return new FocusedEvidenceRunResult(
                    request,
                    Accepted: false,
                    Passed: false,
                    Summary: "selection rejected",
                    Checks: [],
                    Rejection: new FocusedEvidenceRejection(
                        FocusedEvidenceRejectionCode.UnresolvableSelection,
                        originalToken,
                        "selection does not resolve"));
            },
            retryTask: (goalId, taskId, message) =>
            {
                if (taskId == tester.Id)
                {
                    blockingRetryMessage = message;
                }

                return kernel.RetryTask(goalId, taskId, message);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                if (taskId == tester.Id)
                {
                    blockingRetryMessage = message;
                }

                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceOutcome: (goalId, taskId, findingId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, findingId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(["Infrastructure.Tests:" + originalToken], observedRequests);
        var mergedFinding = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == stableId);
        Assert.Equal(ReviewFindingState.Open, mergedFinding.State);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnparseableSelection, mergedFinding.EvidenceOutcome?.Reason);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Contains("- stable_id: " + stableId, blockingRetryMessage, StringComparison.Ordinal);
        var retryBrief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;
        Assert.Contains("verdict=not-honoured", retryBrief, StringComparison.Ordinal);
        Assert.Contains("reason=unparseable-selection", retryBrief, StringComparison.Ordinal);
        Assert.Contains(originalToken, retryBrief, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void TransientEvidenceFailureRerunsOnNextRound()
    {
        const string originalToken = "FullyQualifiedName~ConductorDriverTests.MissingMethod";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "A source-discovery failure must remain retryable.",
            id: "transient-source-discovery",
            classes: [originalToken]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(
                    request,
                    Accepted: false,
                    Passed: false,
                    Summary: "source discovery failed",
                    Checks: [],
                    Rejection: new FocusedEvidenceRejection(
                        FocusedEvidenceRejectionCode.SourceDiscoveryFailure,
                        originalToken,
                        "source discovery failed"));
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, findingId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, findingId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRuns);
    }

    [Xunit.Fact]
    public void UnsupportedProjectDoesNotRerunOnNextRound()
    {
        const string stableId = "unsupported-project";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "An unsupported project request must be replaced before it runs again.",
            id: stableId);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(
                    request,
                    Accepted: false,
                    Passed: false,
                    Summary: "project is unsupported",
                    Checks: [],
                    Rejection: new FocusedEvidenceRejection(
                        FocusedEvidenceRejectionCode.UnsupportedProject,
                        "Infrastructure.Tests",
                        "project is unsupported"));
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, findingId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, findingId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        var mergedFinding = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == stableId);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, mergedFinding.EvidenceOutcome?.Reason);
    }

    public static TheoryData<FindingEvidenceNotHonouredReason?, int> FindingEvidenceRefusalDispositionCases => new()
    {
        { FindingEvidenceNotHonouredReason.Unknown, 0 },
        { FindingEvidenceNotHonouredReason.UnsupportedProject, 0 },
        { FindingEvidenceNotHonouredReason.UnparseableSelection, 0 },
        { FindingEvidenceNotHonouredReason.CandidateShaMissing, 1 },
        { FindingEvidenceNotHonouredReason.ExecutorUnavailable, 1 },
        { FindingEvidenceNotHonouredReason.SelectionApparatusFailure, 1 },
        { FindingEvidenceNotHonouredReason.RunFailed, 1 },
        { FindingEvidenceNotHonouredReason.SupersededByActionableRed, 0 },
        { FindingEvidenceNotHonouredReason.PerRoundCap, 0 },
        { null, 0 },
        { (FindingEvidenceNotHonouredReason)int.MaxValue, 0 }
    };

    [Xunit.Theory]
    [Xunit.MemberData(nameof(FindingEvidenceRefusalDispositionCases))]
    public void PriorFindingEvidenceRefusalDispositionControlsNextRound(
        FindingEvidenceNotHonouredReason? reason,
        int expectedFocusedRuns)
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "A prior refusal must control whether focused evidence is requested again.",
            id: "prior-refusal-disposition");
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        kernel.RecordFindingEvidenceOutcome(
            goal.Id,
            reviewer.Id,
            finding.StableId,
            new FindingEvidenceOutcome(Honoured: false, Reason: reason));
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "focused evidence passed", []);
            },
            retryTask: (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, findingId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, findingId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(expectedFocusedRuns, focusedRuns);
    }

    [Xunit.Fact]
    public void ReceiptIdMakesPriorPermanentRefusalRetryableWithoutMatchingReceipt()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "A prior receipt id must make even a permanent refusal retryable.",
            id: "receipt-id-priority");
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        kernel.RecordFindingEvidenceOutcome(
            goal.Id,
            reviewer.Id,
            finding.StableId,
            new FindingEvidenceOutcome(
                Honoured: false,
                ReceiptId: "receipt-1",
                Reason: FindingEvidenceNotHonouredReason.UnsupportedProject));
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "focused evidence passed", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, findingId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, findingId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
    }

    [Xunit.Fact]
    public void ReceiptlessUnclassifiedEvidenceOutcomesDoNotRerun()
    {
        FindingEvidenceNotHonouredReason?[] unclassifiedReasons =
        [
            FindingEvidenceNotHonouredReason.Unknown,
            FindingEvidenceNotHonouredReason.PerRoundCap,
            null,
            (FindingEvidenceNotHonouredReason)int.MaxValue
        ];

        foreach (var reason in unclassifiedReasons)
        {
            var (kernel, goal) = SoftwareGoal();
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            {
                PassVerification(kernel, goal, task);
            }

            var finding = EvidenceFindingWithRequest(
                "An unclassified receiptless outcome must not replay unchanged.",
                id: "unclassified-receiptless-outcome");
            FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
            kernel.RecordFindingEvidenceOutcome(
                goal.Id,
                reviewer.Id,
                finding.StableId,
                new FindingEvidenceOutcome(Honoured: false, Reason: reason));
            var focusedRuns = 0;
            TaskId? retriedTaskId = null;
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
                runFocusedEvidence: (_, request) =>
                {
                    focusedRuns++;
                    return new FocusedEvidenceRunResult(request, true, true, "must not run", []);
                },
                retryTask: (goalId, taskId, message) =>
                {
                    retriedTaskId = taskId;
                    return kernel.RetryTask(goalId, taskId, message);
                });

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(0, focusedRuns);
            Assert.Equal(tester.Id, retriedTaskId);
        }
    }

    [Xunit.Fact]
    public void FindingEvidenceRawLengthCannotBeTrimmedBelowBound()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var paddedFilter = "ConductorDriverTests" + new string(' ', 1024);
        var finding = EvidenceFindingWithRequest(
            "Whitespace padding must not bypass the selection bound.",
            id: "raw-filter-bound",
            classes: [paddedFilter]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "unexpected", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(
            FindingEvidenceNotHonouredReason.UnparseableSelection,
            reviewer.VerificationHistory.Last().MergedReviewFindings!.Single().EvidenceOutcome?.Reason);
    }

    [Xunit.Fact]
    public void FocusedSelectionApparatusFailurePreservesReceiptAndRetriesRequesterOnly()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The selection must execute at least one test.",
            id: "zero-selection",
            classes: ["GoalAcceptanceVerifierTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var retriedTaskIds = new List<TaskId>();
        var check = new AcceptanceCheckResult(
            "reviewer focused evidence",
            Passed: false,
            ExitCode: 8,
            OutputTail: "0 tests were selected",
            TestResultPaths: ["C:\\receipts\\zero.trx"],
            FailureClassification: AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
            ExecutedTestCount: 0);
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: false,
                Summary: "focused selection apparatus failure",
                Checks: [check],
                Arms:
                [
                    new FocusedEvidenceArmRunResult(
                        FindingEvidenceArm.Candidate,
                        "abc1234",
                        FindingEvidenceArmDisposition.ApparatusFailure,
                        Accepted: true,
                        Passed: false,
                        "executed=0",
                        [check])
                ],
                OutcomeReason: FindingEvidenceOutcomeReason.ApparatusFailure),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskIds.Add(taskId);
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal([reviewer.Id], retriedTaskIds);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!.Single().EvidenceOutcome;
        Assert.False(outcome?.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.SelectionApparatusFailure, outcome?.Reason);
        Assert.Equal(FindingEvidenceOutcomeReason.ApparatusFailure, outcome?.ResultReason);
        Assert.NotNull(outcome?.ReceiptId);
        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.Equal(FindingEvidenceArmDisposition.ApparatusFailure, receipt.Arms!.Single().Disposition);
        Assert.Contains("zero.trx", receipt.Arms.Single().ReceiptPaths!.Single(), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void EquivalentRequestsRunOnceAndShareReceiptIdentity()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var first = EvidenceFindingWithRequest(
            "First finding needs the shared run.",
            id: "first-request",
            classes: ["GoalAcceptanceVerifierTests", "ConductorDriverTests"]);
        var second = EvidenceFindingWithRequest(
            "Second finding needs the same shared run.",
            id: "second-request",
            classes: ["ConductorDriverTests", "GoalAcceptanceVerifierTests", "ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "shared evidence required", findings: [first, second]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                Assert.Equal(
                    "Infrastructure.Tests:ConductorDriverTests; " +
                    "Infrastructure.Tests:GoalAcceptanceVerifierTests",
                    request);
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!;
        var firstReceipt = recorded.Single(finding => finding.StableId == "first-request").EvidenceOutcome?.ReceiptId;
        var secondReceipt = recorded.Single(finding => finding.StableId == "second-request").EvidenceOutcome?.ReceiptId;
        Assert.NotNull(firstReceipt);
        Assert.Equal(firstReceipt, secondReceipt);
        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence,
            recorded.Single(finding => finding.StableId == "first-request").EvidenceOutcome?.ResultReason);
        Assert.Equal(firstReceipt, receipt.ReceiptId);
        Assert.Collection(
            receipt.Arms!,
            arm =>
            {
                Assert.Equal(FindingEvidenceArm.Candidate, arm.Arm);
                Assert.NotEmpty(arm.ReceiptPaths!);
            },
            arm =>
            {
                Assert.Equal(FindingEvidenceArm.Baseline, arm.Arm);
                Assert.NotEmpty(arm.ReceiptPaths!);
                Assert.NotEmpty(arm.FailingTestIdentities!);
            });
        Assert.Contains(
            "failing_tests: EvidenceTests.RejectsBaseline",
            kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content,
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void CandidateAndBaselineGreenRecordsClosedVacuousEvidenceOutcome()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "vacuous evidence must be explicit",
            findings: [EvidenceFindingWithRequest("Run an always-green check.", id: "vacuous")]);
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
                DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Green),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!.Single();
        Assert.True(recorded.EvidenceOutcome?.Honoured);
        Assert.Equal(FindingEvidenceOutcomeReason.VacuousEvidence, recorded.EvidenceOutcome?.ResultReason);
        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.False(receipt.Passed);
        Assert.All(receipt.Arms!, arm => Assert.Equal(FindingEvidenceArmDisposition.Green, arm.Disposition));
        Assert.Contains("reason=vacuous-evidence", kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void BaselineBuildFailureRecordsInconclusiveRatherThanRed()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "baseline must execute",
            findings: [EvidenceFindingWithRequest("Baseline is structurally broken.", id: "baseline-build")]);
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
                DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Inconclusive),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!.Single();
        Assert.Equal(FindingEvidenceOutcomeReason.BaselineInconclusive, recorded.EvidenceOutcome?.ResultReason);
        var baseline = Assert.Single(
            reviewer.VerificationHistory.Last().FindingEvidenceReceipts!.Single().Arms!,
            arm => arm.Arm == FindingEvidenceArm.Baseline);
        Assert.Equal(FindingEvidenceArmDisposition.Inconclusive, baseline.Disposition);
        Assert.NotEqual(FindingEvidenceArmDisposition.Red, baseline.Disposition);
    }

    [Xunit.Fact]
    public void CompatibleSameProjectRequestsRunInOneBatch()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var findings = Enumerable.Range(1, 4)
            .Select(index => EvidenceFindingWithRequest(
                $"Finding {index} needs focused evidence.",
                id: $"request-{index}",
                classes: [$"FocusedEvidence{index}Tests"]))
            .ToArray();
        FailReviewerNeedsWork(kernel, goal, reviewer, "four distinct evidence requests", findings: findings);
        var requests = new List<string>();
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                requests.Add(request);
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(
            [
                "Infrastructure.Tests:FocusedEvidence1Tests; " +
                "Infrastructure.Tests:FocusedEvidence2Tests; " +
                "Infrastructure.Tests:FocusedEvidence3Tests; " +
                "Infrastructure.Tests:FocusedEvidence4Tests"
            ],
            requests);
        Assert.Single(requests);
        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!;
        foreach (var stableId in new[] { "request-1", "request-2", "request-3", "request-4" })
        {
            var outcome = recorded.Single(finding => finding.StableId == stableId).EvidenceOutcome;
            Assert.True(outcome?.Honoured);
        }
        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.All(receipt.RequestDispositions!, disposition =>
            Assert.Equal("executed-batched", disposition.Disposition));
    }

    [Xunit.Fact]
    public void IncompatibleSameProjectRequestsRecordTypedStandaloneAndPendingReasons()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "incompatible same-project evidence requests",
            findings:
            [
                EvidenceFindingWithRequest("Bare class request.", id: "bare", classes: ["ConductorDriverTests"]),
                EvidenceFindingWithRequest(
                    "Expression request.",
                    id: "expression",
                    classes: ["FullyQualifiedName~GoalAcceptanceVerifierTests"])
            ]);
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
                DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Green),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.Contains(receipt.RequestDispositions!, disposition =>
            disposition.FindingStableId == "bare" &&
            disposition.Disposition == "executed-standalone" &&
            disposition.Reason == "incompatible-filter-semantics");
        Assert.Contains(receipt.RequestDispositions!, disposition =>
            disposition.FindingStableId == "expression" &&
            disposition.Disposition == "pending-standalone" &&
            disposition.Reason == "incompatible-filter-semantics");
    }

    [Xunit.Fact]
    public void ActionableCandidateRedSupersedesRemainingRoundAndRetriesDeveloper()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
        }

        var first = EvidenceFindingWithRequest(
            "The first Developer-owned fixture needs focused evidence.",
            id: "fixture-red",
            classes: ["GateReadyCandidateProjectorTests"]);
        var compatible = EvidenceFindingWithRequest(
            "A compatible request should share the project invocation.",
            id: "same-project",
            classes: ["ConductorDriverTests"]);
        var pending = EvidenceFindingWithRequest(
            "A distinct project request must not run after the actionable RED.",
            id: "pending-core",
            project: "Core.Tests",
            classes: ["GoalLifecycleTests"]);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "candidate RED must fail fast",
            findings: [first, compatible, pending]);

        var focusedRuns = new List<string>();
        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var dispatchStarts = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns.Add(request);
                return CandidateRedFindingEvidence(request, candidateSha);
            },
            dispatchAndStart: _ =>
            {
                dispatchStarts++;
                return DispatchStartOutcome.Started();
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(developer.Id, retriedTaskId);
        Assert.Equal(1, dispatchStarts);
        var request = Assert.Single(focusedRuns);
        Assert.Equal(
            "Infrastructure.Tests:ConductorDriverTests; " +
            "Infrastructure.Tests:GateReadyCandidateProjectorTests",
            request);
        Assert.Contains("ACTIONABLE_CANDIDATE_RED", retryMessage, StringComparison.Ordinal);
        Assert.Contains(candidateSha, retryMessage, StringComparison.Ordinal);
        Assert.Contains(
            "GateReadyCandidateProjectorTests.DefaultHarnessProducesSerializedResourceKey",
            retryMessage,
            StringComparison.Ordinal);

        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!;
        var pendingOutcome = recorded.Single(finding => finding.StableId == "pending-core").EvidenceOutcome;
        Assert.False(pendingOutcome?.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.SupersededByActionableRed, pendingOutcome?.Reason);
        Assert.Equal(FindingEvidenceOutcomeReason.CandidateRed, pendingOutcome?.ResultReason);
        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.Equal(candidateSha, receipt.CandidateSha);
        Assert.Contains(receipt.RequestDispositions!, disposition =>
            disposition.FindingStableId == "pending-core" &&
            disposition.Reason == "superseded-by-actionable-red");
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
    }

    [Xunit.Fact]
    public void ActionableRedRunningDownstreamEscalatesWithReceiptWithoutDispatch()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task, hasCommittedChanges: task.RequiredRole == AgentRole.Developer);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "candidate RED collides with a running downstream task",
            findings:
            [
                EvidenceFindingWithRequest(
                    "Developer-owned fixture is RED.",
                    id: "fixture-red",
                    classes: ["GateReadyCandidateProjectorTests"])
            ]);
        var dispatchStarts = 0;
        string? escalationReason = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) => CandidateRedFindingEvidence(request, candidateSha),
            dispatchAndStart: _ =>
            {
                dispatchStarts++;
                return DispatchStartOutcome.Started();
            },
            retryTaskWithRoundKind: (_, _, _, _) => throw new InvalidOperationException(
                "Cannot retry Developer task while downstream Tester task has a running process."),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            writeEscalation: (_, _, reason) => escalationReason = reason);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(0, dispatchStarts);
        Assert.Contains("ACTIONABLE_CANDIDATE_RED_LIFECYCLE_CONFLICT", escalationReason, StringComparison.Ordinal);
        Assert.Contains(candidateSha, escalationReason, StringComparison.Ordinal);
        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.Contains(receipt.ReceiptId, escalationReason, StringComparison.Ordinal);
        Assert.Contains("running process", escalationReason, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void MixedBatchCandidateRedRoutesOnlyWhenFailingIdentityMatchesDeveloperOwnedRequest()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var operatorOwned = EvidenceFindingWithRequest(
            "The operator-owned request selects the failing test.",
            id: "operator-request",
            category: FindingCategory.OperatorOwned,
            classes: ["GateReadyCandidateProjectorTests"]);
        var developerOwned = EvidenceFindingWithRequest(
            "The Developer-owned request selects a different test.",
            id: "developer-request",
            classes: ["ConductorDriverTests"]);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "candidate RED attribution must stay request-bound",
            findings: [operatorOwned, developerOwned]);

        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) => CandidateRedFindingEvidence(request, candidateSha),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(reviewer.Id, retriedTaskId);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("ACTIONABLE_CANDIDATE_RED", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void CandidateRedAttributionRequiresTestClassBoundary()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "candidate RED attribution must respect class boundaries",
            findings:
            [
                EvidenceFindingWithRequest(
                    "Operator request owns the actual failure.",
                    id: "operator-request",
                    category: FindingCategory.OperatorOwned,
                    classes: ["ConductorDriverTests2"]),
                EvidenceFindingWithRequest(
                    "Developer request is only a name prefix.",
                    id: "developer-request",
                    classes: ["ConductorDriverTests"])
            ]);

        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) => CandidateRedFindingEvidence(
                request,
                candidateSha,
                "ConductorDriverTests2.FailingMember"),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(reviewer.Id, retriedTaskId);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("ACTIONABLE_CANDIDATE_RED", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void StaleFindingEvidenceReceiptDoesNotSuppressRerunAfterCandidateChanges()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "Focused evidence must bind to the current candidate.",
            id: "candidate-bound");
        FailReviewerNeedsWork(kernel, goal, reviewer, "candidate-bound evidence", findings: [finding]);
        var candidateSha = "abc1234";
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        candidateSha = "def5678";
        FailReviewerNeedsWork(kernel, goal, reviewer, "candidate-bound evidence", findings: [finding]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRuns);
        var receipts = reviewer.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .DistinctBy(receipt => receipt.ReceiptId)
            .ToArray();
        Assert.Equal(2, receipts.Length);
        Assert.Contains(receipts, receipt => receipt.CandidateSha == "abc1234");
        Assert.Contains(receipts, receipt => receipt.CandidateSha == "def5678");
    }

    [Xunit.Fact]
    public void ChangedFindingRoundAtSameCandidateDoesNotReusePriorReceipt()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The first finding round needs focused evidence.",
            id: "same-sha-new-round");
        FailReviewerNeedsWork(kernel, goal, reviewer, "first finding round", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var repeatedFinding = finding with { Description = "A changed finding round still needs focused evidence." };
        FailReviewerNeedsWork(kernel, goal, reviewer, "changed finding round", findings: [repeatedFinding]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRuns);
        var receipts = reviewer.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .DistinctBy(receipt => receipt.ReceiptId)
            .ToArray();
        Assert.Equal(2, receipts.Length);
        Assert.All(receipts, receipt => Assert.Equal(candidateSha, receipt.CandidateSha));
        Assert.Equal(2, receipts.Select(receipt => receipt.FindingRoundFingerprint).Distinct().Count());
    }

    [Xunit.Theory]
    [Xunit.InlineData("Unsupported.Tests")]
    [Xunit.InlineData("Mcg.AgentOrchestrator.App")]
    public void InvalidAndValidRequestsAreProcessedIndependently(string unsupportedProject)
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var invalid = EvidenceFindingWithRequest(
            "Unsupported project should be refused.",
            id: "invalid-request",
            project: unsupportedProject,
            classes: ["UnknownTests"]);
        var valid = EvidenceFindingWithRequest(
            "Valid request should still run.",
            id: "valid-request",
            classes: ["ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "mixed evidence requests", findings: [invalid, valid]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "valid evidence passed", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!;
        var invalidOutcome = recorded.Single(finding => finding.StableId == "invalid-request").EvidenceOutcome;
        var validOutcome = recorded.Single(finding => finding.StableId == "valid-request").EvidenceOutcome;
        Assert.False(invalidOutcome?.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, invalidOutcome?.Reason);
        Assert.Contains(unsupportedProject, invalidOutcome?.Detail, StringComparison.Ordinal);
        Assert.Contains("Accepted forms: Core, Core.Tests", invalidOutcome?.Detail, StringComparison.Ordinal);
        Assert.Contains("Mcg.AgentOrchestrator.Infrastructure.Tests", invalidOutcome?.Detail, StringComparison.Ordinal);
        Assert.Contains("full .csproj path", invalidOutcome?.Detail, StringComparison.Ordinal);
        Assert.Null(invalidOutcome?.ReceiptId);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.Contains("finding_id=invalid-request", StringComparison.Ordinal) &&
            evt.Message.Contains("candidate_sha=abc1234", StringComparison.Ordinal));
        Assert.True(validOutcome?.Honoured);
        Assert.NotNull(validOutcome?.ReceiptId);
        Assert.Null(validOutcome?.Reason);
        var retryBrief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;
        Assert.Contains("reason=unsupported-project", retryBrief, StringComparison.Ordinal);
        Assert.Contains("valid evidence passed", retryBrief, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void FindingEvidenceRequestAcceptsRegisteredCliInfrastructureProject()
    {
        const string cliProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/" +
            "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The extracted CLI project needs focused evidence.",
            id: "cli-extracted-project",
            project: "Infrastructure.Cli.Tests",
            classes: ["CliArgumentNormalizationTests", "CliCommandTestsAddTaskCommands"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "registered extracted project", findings: [finding]);
        string? request = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            getFindingEvidenceEngineSettings: _ => new AcceptanceGateEngineSettings
            {
                MtpInvocations = [new AcceptanceMtpInvocation { Project = cliProject }]
            },
            runFocusedEvidence: (_, value) =>
            {
                request = value;
                return new FocusedEvidenceRunResult(value, true, true, "registered project passed", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(
            "Infrastructure.Cli.Tests:CliArgumentNormalizationTests; Infrastructure.Cli.Tests:CliCommandTestsAddTaskCommands",
            request);
        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!;
        Assert.True(recorded.Single(item => item.StableId == "cli-extracted-project").EvidenceOutcome?.Honoured);
    }

    [Xunit.Fact]
    public void ResolvedFindingEvidenceRequestDoesNotRun()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var open = EvidenceFindingWithRequest(
            "Historical request is already resolved.",
            id: "resolved-request",
            classes: ["ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "historical finding was open", findings: [open]);
        kernel.RetryTask(goal.Id, reviewer.Id, "recheck resolved finding");
        var resolved = open with
        {
            State = ReviewFindingState.Resolved
        };
        FailReviewerNeedsWork(kernel, goal, reviewer, "historical finding was resolved", findings: [resolved]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "must not run", []);
            },
            retryTask: (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Null(reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(finding => finding.StableId == "resolved-request").EvidenceOutcome);
    }

    [Xunit.Fact]
    public void NotHonouredDetailTextDoesNotChangeRoutingDecision()
    {
        var retriedRoles = new List<AgentRole>();
        foreach (var detail in new[] { "first display-only detail", "completely different human wording" })
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            {
                PassVerification(kernel, goal, task);
            }
            var finding = EvidenceFindingWithRequest("Evidence is unavailable.", id: "detail-only");
            FailReviewerNeedsWork(kernel, goal, reviewer, "evidence unavailable", findings: [finding]);
            kernel.RecordFindingEvidenceOutcome(
                goal.Id,
                reviewer.Id,
                finding.StableId,
                new FindingEvidenceOutcome(
                    Honoured: false,
                    Reason: FindingEvidenceNotHonouredReason.ExecutorUnavailable,
                    Detail: detail));
            var driver = MakeDriver(
                retryTask: (goalId, taskId, message) =>
                {
                    var retried = kernel.RetryTask(goalId, taskId, message);
                    retriedRoles.Add(retried.RequiredRole);
                    return retried;
                });

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        }

        Assert.Equal([AgentRole.Reviewer, AgentRole.Reviewer], retriedRoles);
    }

    [Xunit.Fact]
    public void ProseOnlyEvidenceNamesRouteNormallyWithoutRunning()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        const string blocker =
            "Infrastructure.Tests FirstReceiptTests SecondReceiptTests ThirdReceiptTests " +
            "FourthReceiptTests FifthReceiptTests receipts are missing.";
        FailReviewerNeedsWork(kernel, goal, reviewer, blocker, findings: [EvidenceFinding(blocker)]);
        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runFocusedEvidence: (_, _) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult("", true, true, "should not run", []);
            },
            retryTask: (goalId, taskId, message) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordReviewerEvidenceRequestReceived: (goalId, taskId, message) =>
                kernel.RecordReviewerEvidenceRequestReceived(goalId, taskId, message));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(tester.Id, retriedTaskId);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Kind == ProgressKind.ReviewerEvidenceRequestReceived);
    }

    [Xunit.Fact]
    public void ExistingDerivedReceipt_RoutesTesterWithoutRerun()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        const string blocker = "Infrastructure.Tests ConductorDriverTests receipt is missing.";
        FailReviewerNeedsWork(kernel, goal, reviewer, blocker, findings: [EvidenceFinding(blocker)]);
        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, _) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult("", true, true, "should not run", []);
            },
            retryTask: (goalId, taskId, message) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordReviewerEvidenceRequestReceived: (goalId, taskId, message) =>
                kernel.RecordReviewerEvidenceRequestReceived(goalId, taskId, message));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(tester.Id, retriedTaskId);
    }

    [Xunit.Fact]
    public void SubstitutionCap_SurvivesSnapshotReload()
    {
        var (kernel, originalGoal) = SoftwareGoal();
        var originalReviewer = originalGoal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in originalGoal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, originalGoal, task);
        }

        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var goal = kernel.GetGoal(originalGoal.Id);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        const string blocker = "Infrastructure.Tests GoalAcceptanceVerifierTests receipt is missing.";
        FailReviewerNeedsWork(kernel, goal, reviewer, blocker, findings: [EvidenceFinding(blocker)]);
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("def5678"),
            retryTask: (goalId, taskId, message) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordReviewerEvidenceRequestReceived: (goalId, taskId, message) =>
                kernel.RecordReviewerEvidenceRequestReceived(goalId, taskId, message));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
    }

    [Xunit.Fact]
    public void FailingDerivedEvidence_RetriesReviewerWithReceipt()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        const string blocker = "Core.Tests GoalLifecycleTests receipt is missing.";
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            blocker,
            findings: [EvidenceFindingWithRequest(blocker, project: "Core.Tests", classes: ["GoalLifecycleTests"])]);
        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: false,
                Summary: "focused evidence failed; receipts: C:\\tmp\\failed-derived-trx",
                Checks: []),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordReviewerEvidenceRequestReceived: (goalId, taskId, message) =>
                kernel.RecordReviewerEvidenceRequestReceived(goalId, taskId, message),
            recordReviewerEvidenceRunRecorded: (goalId, taskId, message) =>
                kernel.RecordReviewerEvidenceRunRecorded(goalId, taskId, message),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(reviewer.Id, retriedTaskId);
        var failedReceipt = Assert.Single(reviewer.VerificationHistory.SelectMany(item => item.FindingEvidenceReceipts ?? []));
        Assert.False(failedReceipt.Passed);
        Assert.Contains("C:\\tmp\\failed-derived-trx", failedReceipt.Summary, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MissingStructuredRequestDoesNotDeriveFromProse()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var blocker = "Required receipts: Infrastructure.Tests ConductorDriverTests and GoalAcceptanceVerifierTests.";
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            blocker,
            findings:
            [
                new ReviewFinding(
                    "missing-receipts",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("tests/receipts", "focused"),
                    blocker,
                    FindingSeverity.Blocking,
                    FindingCategory.TestEvidence),
                new ReviewFinding(
                    "resolved-correctness",
                    ReviewFindingState.Resolved,
                    new ReviewFindingLocation("src/Resolved.cs", "Run"),
                    "resolved correctness finding must not block substitution",
                    FindingSeverity.Blocking,
                    FindingCategory.Correctness),
                new ReviewFinding(
                    "advisory-correctness",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Advisory.cs", "Run"),
                    "advisory correctness finding must not block substitution",
                    FindingSeverity.Advisory,
                    FindingCategory.Correctness)
            ]);
        string? focusedRequest = null;
        var retriedTaskIds = new List<TaskId>();
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRequest = request;
                return new FocusedEvidenceRunResult(request, true, true, "passed; receipts: C:\\tmp\\derived-trx", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskIds.Add(taskId);
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordReviewerEvidenceRequestReceived: (goalId, taskId, message) =>
                kernel.RecordReviewerEvidenceRequestReceived(goalId, taskId, message),
            recordReviewerEvidenceRunRecorded: (goalId, taskId, message) =>
                kernel.RecordReviewerEvidenceRunRecorded(goalId, taskId, message),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Null(focusedRequest);
        Assert.Equal([goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester).Id], retriedTaskIds);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.ReviewerEvidenceRequestReceived);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    private static SuppressionCapture CaptureReviewerSuppression(
        string candidateSha,
        params ReviewFinding[] findings)
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            string.Join("; ", findings.Select(finding => finding.Description)),
            findings: findings);
        var focusedRuns = 0;
        AgentRole? retriedRole = null;
        var suppressions = new List<SuppressionCall>();
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "focused request passed", []);
            },
            retryTask: (goalId, taskId, message) =>
            {
                retriedRole = goal.Tasks.Single(task => task.Id == taskId).RequiredRole;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordFindingEvidenceSuppressed: (_, _, actualCandidateSha, blockerIds, requestId, _, reason, identity) =>
                suppressions.Add(new SuppressionCall(
                    actualCandidateSha,
                    blockerIds.ToArray(),
                    requestId,
                    reason,
                    identity)));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        return new SuppressionCapture(suppressions.ToArray(), focusedRuns, retriedRole);
    }

    private sealed record SuppressionCapture(
        SuppressionCall[] Suppressions,
        int FocusedRuns,
        AgentRole? RetriedRole);

    private sealed record SuppressionCall(
        string CandidateSha,
        string[] BlockerIds,
        string RequestId,
        string Reason,
        string Identity);

}
