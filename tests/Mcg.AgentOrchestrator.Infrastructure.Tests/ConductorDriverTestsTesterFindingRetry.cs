using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using System.Text.Json;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsTesterFindingRetry
{
    private const string CandidateSha = "abc1234";
    private const string OldCandidateSha = "0ldc0de";

    [Xunit.Theory(DisplayName = "Developer_owned_finding_without_a_request_is_a_current_writable_defect")]
    [Xunit.InlineData(FindingCategory.Correctness)]
    [Xunit.InlineData(FindingCategory.TestCoverage)]
    [Xunit.InlineData(FindingCategory.SpecCompliance)]
    [Xunit.InlineData(FindingCategory.CodeQuality)]
    public void DeveloperOwnedFindingWithoutARequestIsACurrentWritableDefect(FindingCategory category)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        RecordTesterFinding(kernel, goal, tester, category, includeEvidenceRequest: false);

        var harness = new RetryHarness(kernel);
        var driver = harness.Driver(baselineDisposition: FindingEvidenceArmDisposition.Red);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, harness.FocusedRuns);
        Assert.Equal(developer.Id, harness.RetriedTaskId);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        // The writable decision carries the same identities as the two evidence-bound branches: the
        // Developer is told which finding it owns, and nothing claims a run produced this routing.
        Assert.Contains("stable_id: T-FINDING", harness.RetryMessage!, StringComparison.Ordinal);
        Assert.Contains("location: src/Test.cs::Test.T-FINDING", harness.RetryMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTIONABLE_CANDIDATE_RED", harness.RetryMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            goal.Timeline,
            evt => evt.Message.Contains("disposition=pending-execution-gate", StringComparison.Ordinal));
    }

    [Xunit.Theory(DisplayName = "Developer_owned_finding_awaiting_its_request_runs_that_evidence_before_Developer")]
    [Xunit.InlineData(FindingCategory.Correctness)]
    [Xunit.InlineData(FindingCategory.TestCoverage)]
    [Xunit.InlineData(FindingCategory.SpecCompliance)]
    [Xunit.InlineData(FindingCategory.CodeQuality)]
    public void DeveloperOwnedFindingAwaitingItsRequestRunsThatEvidenceBeforeDeveloper(FindingCategory category)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        RecordTesterFinding(kernel, goal, tester, category, includeEvidenceRequest: true);

        var harness = new RetryHarness(kernel);
        var driver = harness.Driver(baselineDisposition: FindingEvidenceArmDisposition.Red);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        // Measure the pending request before routing the surviving finding to the Developer.
        Assert.Equal(1, harness.FocusedRuns);
        AssertPassingEvidenceRoutesDeveloper(goal, developer, tester, harness);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == tester.Id &&
            evt.Message.Contains("disposition=pending-execution-gate", StringComparison.Ordinal) &&
            evt.Message.Contains("finding_ids=T-FINDING", StringComparison.Ordinal) &&
            evt.Message.Contains($"candidate_sha={CandidateSha}", StringComparison.Ordinal) &&
            evt.Message.Contains($"deferred_developer_task_id={developer.Id}", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "One_writable_finding_in_a_mixed_round_keeps_Developer_precedence")]
    public void OneWritableFindingInAMixedRoundKeepsDeveloperPrecedence()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        RecordTesterFindings(
            kernel,
            goal,
            tester,
            TesterFinding("T-FINDING", FindingCategory.Correctness, includeEvidenceRequest: true),
            TesterFinding("T-WRITABLE", FindingCategory.Correctness, includeEvidenceRequest: false));

        var harness = new RetryHarness(kernel);
        var driver = harness.Driver(baselineDisposition: FindingEvidenceArmDisposition.Red);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, harness.FocusedRuns);
        Assert.Equal(developer.Id, harness.RetriedTaskId);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "Receipt_already_taken_at_this_candidate_routes_the_blocker_to_Developer")]
    public void ReceiptAlreadyTakenAtThisCandidateRoutesTheBlockerToDeveloper()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        RecordTesterFinding(kernel, goal, tester, FindingCategory.Correctness, includeEvidenceRequest: true);
        AttachFindingEvidenceReceipt(kernel, goal, tester, "seeded-current", CandidateSha);

        var harness = new RetryHarness(kernel);
        var driver = harness.Driver(baselineDisposition: FindingEvidenceArmDisposition.Red);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        // One focused run per candidate per request: the finding survived its own evidence, so it is
        // now a demonstrated current blocker and the same-candidate request is not repeated.
        Assert.Equal(0, harness.FocusedRuns);
        Assert.Equal(developer.Id, harness.RetriedTaskId);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "Receipt_from_an_older_candidate_cannot_suppress_the_pending_run")]
    public void ReceiptFromAnOlderCandidateCannotSuppressThePendingRun()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        RecordTesterFinding(kernel, goal, tester, FindingCategory.Correctness, includeEvidenceRequest: true);
        AttachFindingEvidenceReceipt(kernel, goal, tester, "seeded-old", OldCandidateSha);

        var harness = new RetryHarness(kernel);
        var driver = harness.Driver(baselineDisposition: FindingEvidenceArmDisposition.Red);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, harness.FocusedRuns);
        AssertPassingEvidenceRoutesDeveloper(goal, developer, tester, harness);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "Candidate_RED_routes_the_concrete_defect_upstream_with_stable_identities")]
    public void CandidateRedRoutesTheConcreteDefectUpstreamWithStableIdentities()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        RecordTesterFinding(kernel, goal, tester, FindingCategory.Correctness, includeEvidenceRequest: true);

        var harness = new RetryHarness(kernel);
        var driver = harness.Driver(candidateRed: true);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, harness.FocusedRuns);
        Assert.Equal(developer.Id, harness.RetriedTaskId);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        // Stable finding identity and the exact candidate survive the owner change.
        Assert.Contains("ACTIONABLE_CANDIDATE_RED", harness.RetryMessage, StringComparison.Ordinal);
        Assert.Contains("finding_ids=T-FINDING", harness.RetryMessage, StringComparison.Ordinal);
        Assert.Contains($"candidate_sha={CandidateSha}", harness.RetryMessage, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Unavailable_candidate_keeps_the_blocker_with_Developer")]
    public void UnavailableCandidateKeepsTheBlockerWithDeveloper()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        RecordTesterFinding(kernel, goal, tester, FindingCategory.Correctness, includeEvidenceRequest: true);

        var harness = new RetryHarness(kernel);
        var driver = harness.Driver(
            baselineDisposition: FindingEvidenceArmDisposition.Red,
            candidateShaOverride: "unavailable");

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, harness.FocusedRuns);
        Assert.Equal(developer.Id, harness.RetriedTaskId);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact]
    public void DeveloperOwnedTesterFindingReopensDeveloperAfterItsLatestRetryProducedNoCommit()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        kernel.RetryTask(goal.Id, developer.Id, "A blocking source finding needs a correction.");
        PassVerification(kernel, goal, developer, hasCommittedChanges: false);
        RecordTesterFinding(kernel, goal, tester, FindingCategory.Correctness, includeEvidenceRequest: true);

        var harness = new RetryHarness(kernel);
        var driver = harness.Driver(baselineDisposition: FindingEvidenceArmDisposition.Red);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        // The no-commit retry settled the candidate. Its passing evidence leaves the finding open,
        // so the Developer must repair it before downstream verification.
        Assert.Equal(1, harness.FocusedRuns);
        AssertPassingEvidenceRoutesDeveloper(goal, developer, tester, harness);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void PendingDeveloperRetryPreventsCombinedTesterReviewerEvidenceRepeatAndDispatchesDeveloper(bool restart)
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var predecessor in goal.Tasks.TakeWhile(task => task.Id != developer.Id))
            PassVerification(kernel, goal, predecessor);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, candidateSha);
        kernel.RetryTask(
            goal.Id,
            developer.Id,
            "The operator supplied an upstream source correction.",
            retryCause: RetryCause.NewSourceFinding);
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, new PreReviewEvidenceReceipt(
            goal.Id.Value,
            ReviewerRound: 1,
            CandidateSha: candidateSha,
            SelectedFocusedTests: ["Infrastructure.Tests: ConductorDriverTests"],
            Disposition: PreReviewEvidenceDisposition.Green,
            PassedCheckCount: 1,
            FailedCheckCount: 0,
            Checks: [new PreReviewEvidenceCheckReceipt(
                "ConductorDriverTests",
                "Infrastructure.Tests: ConductorDriverTests",
                Passed: true,
                ExitCode: 0)],
            FailingTestIdentities: [],
            MappingReason: "fixture green evidence",
            EvidencePointer: "fixture://green",
            RecordedAt: DateTimeOffset.UtcNow));
        RecordTesterFinding(kernel, goal, tester, FindingCategory.Correctness, includeEvidenceRequest: true);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "reviewer source finding",
            findings: [EvidenceFindingWithRequest(
                "The unchanged candidate still has a blocking source defect.",
                id: "combined-reviewer-correctness",
                category: FindingCategory.Correctness)]);

        var focusedRuns = 0;
        IReadOnlyList<DispatchedTaskIdentity>? dispatched = null;
        var additionalRetries = new List<(TaskId Target, RetryRoundKind? Kind, RetryCause Cause)>();
        var starts = 0;
        var capacityAvailable = false;
        var pendingRetryAt = developer.LatestRetryAt;
        var admittedRetries = goal.Timeline.Count(evt => evt.TaskId == developer.Id && evt.Kind == ProgressKind.TaskRetried);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red, candidateSha);
            },
            dispatchAndStart: dispatchGoal =>
            {
                if (!capacityAvailable)
                    return DispatchStartOutcome.EmptyBatch("Fixture provider capacity temporarily unavailable.");
                var batch = DispatchReadinessRules.BuildReadyTaskParallelPlan(dispatchGoal, DefaultAgents())
                    .Batches.FirstOrDefault();
                var started = dispatchGoal.Tasks.Where(task =>
                    batch?.IntentIds.Contains(task.Id.Value) == true).ToArray();
                foreach (var task in started)
                {
                    starts++;
                    DispatchTask(kernel, dispatchGoal, task);
                    kernel.RecordTaskProcessStarted(dispatchGoal.Id, task.Id, new TaskProcessRecord(
                        12345, "test.exe", @"C:\tmp", @"C:\tmp\stdout", @"C:\tmp\stderr", @"C:\tmp\exit",
                        StartedAt: DateTimeOffset.UtcNow, CompletedAt: null, ExitCode: null));
                }
                var outcome = DispatchStartOutcome.Started(started);
                dispatched = outcome.DispatchedTasks;
                return outcome;
            },
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
            {
                additionalRetries.Add((taskId, roundKind, cause));
                return kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: roundKind);
            },
            recordTaskNote: (goalId, taskId, message) => kernel.RecordTaskNote(goalId, taskId, message));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var proposal = Assert.Single(additionalRetries);
        Assert.Equal((developer.Id, (RetryRoundKind?)null, RetryCause.NewSourceFinding), proposal);
        Assert.Equal(admittedRetries, goal.Timeline.Count(evt => evt.TaskId == developer.Id && evt.Kind == ProgressKind.TaskRetried));
        Assert.Equal(0, starts);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Null(tester.LastVerification);
        Assert.Null(reviewer.LastVerification);
        var notes = goal.Timeline.Count(evt => evt.TaskId == developer.Id && evt.Kind == ProgressKind.TaskRetryFeedbackUpdated);
        Assert.Equal(1, notes);
        Assert.NotEmpty(tester.VerificationHistory);
        Assert.NotEmpty(reviewer.VerificationHistory);
        if (restart)
        {
            var persisted = JsonSerializer.Serialize(kernel.ExportSnapshot());
            kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(persisted)!);
            goal = kernel.GetGoal(goal.Id);
            developer = goal.Tasks.Single(task => task.Id == developer.Id);
            tester = goal.Tasks.Single(task => task.Id == tester.Id);
            reviewer = goal.Tasks.Single(task => task.Id == reviewer.Id);
        }
        var timelineCount = goal.Timeline.Count;
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(timelineCount, goal.Timeline.Count);
        Assert.Single(additionalRetries);
        Assert.Equal(notes, goal.Timeline.Count(evt => evt.TaskId == developer.Id && evt.Kind == ProgressKind.TaskRetryFeedbackUpdated));
        capacityAvailable = true;
        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Single(additionalRetries);
        Assert.Equal(pendingRetryAt, developer.LatestRetryAt);
        Assert.Equal(0, focusedRuns);
        Assert.Single(reviewer.PreReviewEvidenceHistory);
        Assert.True(dispatched is not null, JsonSerializer.Serialize(new
        {
            Outcome = result.Outcome.ToString(),
            goal.Status,
            Tasks = goal.Tasks.Select(task => new { task.RequiredRole, task.Status })
        }));
        Assert.Equal(developer.Id, Assert.Single(dispatched!).TaskId);
        Assert.Equal(AgentRole.Developer, Assert.Single(dispatched!).Role);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        var brief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;
        Assert.Contains("T-FINDING", brief);
        Assert.Contains("combined-reviewer-correctness", brief);
        var next = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.IsType<ConductorAdvanceOutcome.Held>(next.Outcome);
        Assert.Single(additionalRetries);
        Assert.Equal(1, starts);

        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, "def5678");
        kernel.RecordTaskProcessRefreshed(goal.Id, developer.Id,
            developer.LastProcess! with { CompletedAt = DateTimeOffset.UtcNow, ExitCode = 0 }, null);
        kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
            "test.exe", @"C:\tmp", 0, "ok", "", DateTimeOffset.UtcNow, HasCommittedChanges: true));
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(tester.Id, Assert.Single(dispatched!).TaskId);
        Assert.Equal(2, starts);
        Assert.Null(tester.LastVerification);
        Assert.NotEmpty(tester.VerificationHistory);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Tester)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void TestEvidenceFindingUsesEvidencePath(AgentRole requestingRole)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == requestingRole);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        RecordTesterFinding(kernel, goal, tester, FindingCategory.TestEvidence, includeEvidenceRequest: true);

        var harness = new RetryHarness(kernel);
        var driver = harness.Driver(baselineDisposition: FindingEvidenceArmDisposition.Red);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, harness.FocusedRuns);
        if (requestingRole == AgentRole.Tester)
            AssertPassingEvidenceRoutesDeveloper(goal, developer, tester, harness);
        else
        {
            Assert.Equal(tester.Id, harness.RetriedTaskId);
            Assert.NotEqual(developer.Id, harness.RetriedTaskId);
        }
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void DeveloperOwnedTesterFindingWithoutCommittedDeveloperEscalates(bool includeEvidenceRequest)
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        RecordTesterFinding(kernel, goal, tester, FindingCategory.Correctness, includeEvidenceRequest);

        var harness = new RetryHarness(kernel);
        var driver = harness.Driver(baselineDisposition: FindingEvidenceArmDisposition.Red);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        // With no feasible upstream Developer there is nothing to defer, so the unchanged escalation
        // stands and no paid focused round is spent.
        Assert.Equal(0, harness.FocusedRuns);
        Assert.Null(harness.RetriedTaskId);
        Assert.Contains("could not be routed to an upstream Developer task", harness.Escalation, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    private static void AssertPassingEvidenceRoutesDeveloper(
        Goal goal, TaskSpec developer, TaskSpec tester, RetryHarness harness)
    {
        Assert.Equal(developer.Id, harness.RetriedTaskId);
        Assert.Equal(RetryCause.NewSourceFinding, harness.RetriedCause);
        var verification = tester.VerificationHistory.Last();
        var receipt = verification.FindingEvidenceReceipts!.Last();
        Assert.True(receipt.Accepted && receipt.Passed);
        Assert.Equal(CandidateSha, receipt.CandidateSha);
        Assert.Contains(receipt.ReceiptId, harness.RetryMessage!, StringComparison.Ordinal);
        Assert.Contains(CandidateSha, harness.RetryMessage!, StringComparison.Ordinal);
        Assert.Contains("passing evidence did not close", harness.RetryMessage!, StringComparison.Ordinal);
        var finding = Assert.Single(verification.MergedReviewFindings!);
        Assert.Equal(ReviewFindingState.Open, finding.State);
        Assert.Contains(finding.StableId, harness.RetryMessage!, StringComparison.Ordinal);
        Assert.Equal(0, FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
            goal.Timeline, tester.Id, CandidateSha, finding.StableId));
        Assert.Null(harness.Escalation);
    }

    private sealed class RetryHarness(AgentOrchestratorKernel kernel)
    {
        public int FocusedRuns { get; private set; }

        public TaskId? RetriedTaskId { get; private set; }

        public RetryCause? RetriedCause { get; private set; }

        public string? RetryMessage { get; private set; }

        public string? Escalation { get; private set; }

        public ConductorDriver Driver(
            FindingEvidenceArmDisposition baselineDisposition = FindingEvidenceArmDisposition.Red,
            bool candidateRed = false,
            string candidateShaOverride = CandidateSha) =>
            MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateShaOverride),
                runFocusedEvidence: (_, request) =>
                {
                    FocusedRuns++;
                    return candidateRed
                        ? CandidateRedFindingEvidence(request, CandidateSha)
                        : DualArmFindingEvidence(request, baselineDisposition, CandidateSha);
                },
                dispatchAndStart: _ => DispatchStartOutcome.Started(),
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                {
                    RetriedTaskId = taskId;
                    RetriedCause = cause;
                    RetryMessage = message;
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
                },
                writeEscalation: (_, _, message) => Escalation = message,
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));
    }

    private static FindingEvidenceRequest TesterEvidenceRequest() =>
        new([new FindingEvidenceSelection("Infrastructure.Tests", "ConductorDriverTests")]);

    private static ReviewFinding TesterFinding(
        string stableId,
        FindingCategory category,
        bool includeEvidenceRequest) =>
        new(
            stableId,
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/Test.cs", $"Test.{stableId}"),
            "The candidate requires a finding-specific response.",
            FindingSeverity.Blocking,
            category,
            includeEvidenceRequest ? TesterEvidenceRequest() : null);

    private static void AttachFindingEvidenceReceipt(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec tester,
        string receiptId,
        string candidateSha) =>
        kernel.RecordFindingEvidenceOutcome(
            goal.Id,
            tester.Id,
            "T-FINDING",
            new FindingEvidenceOutcome(
                Honoured: true,
                ReceiptId: receiptId,
                ResultReason: FindingEvidenceOutcomeReason.ValidEvidence),
            new FindingEvidenceReceipt(
                receiptId,
                candidateSha,
                TesterEvidenceRequest(),
                Accepted: true,
                Passed: true,
                $"seeded focused evidence receipt at {candidateSha}",
                RequestDispositions:
                [
                    new FindingEvidenceRequestDisposition(
                        "T-FINDING",
                        "Infrastructure.Tests:ConductorDriverTests",
                        "executed-standalone",
                        "only-request")
                ]));

    private static void RecordTesterFinding(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec tester,
        FindingCategory category,
        bool includeEvidenceRequest) =>
        RecordTesterFindings(
            kernel, goal, tester, TesterFinding("T-FINDING", category, includeEvidenceRequest));

    private static void RecordTesterFindings(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec tester,
        params ReviewFinding[] findings)
    {
        DispatchTask(kernel, goal, tester, "test");
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: inspect focused behavior",
            "tests: deferred - acceptance owns the focused execution",
            "commit: none",
            "blockers: none",
            $"findings: {JsonSerializer.Serialize(findings)}",
            "touched_anchors: []",
            "verdict: needs-work",
            "model_fit: fixture/model - adequate - source verification",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test",
            "C:\\tmp",
            0,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\tester.out.log",
            WorkerResultPresent: true));
    }
}
