using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsStaleFindingRouting
{
    [Xunit.Fact(DisplayName = "Completed Developer repair reconciles a retained failed Reviewer across reload")]
    public void CompletedDeveloperRepairReconcilesRetainedFailedReviewerAcrossReload()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var priorCandidate = new string('a', 40);
        var repairedCandidate = new string('b', 40);

        foreach (var task in goal.Tasks.Where(task =>
                     task.RequiredRole is not AgentRole.Developer and
                     not AgentRole.Tester and
                     not AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        DispatchTask(kernel, goal, developer, "develop-1", baseCommit: new string('0', 40));
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, priorCandidate);
        kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
            "develop-1",
            "C:\\tmp",
            0,
            "ok",
            string.Empty,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            WorkerResultPresent: true,
            HasCommittedChanges: true));
        PassVerification(kernel, goal, tester);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "Historical repair obligation.",
            findings:
            [
                new ReviewFinding(
                    "historical-repair-obligation",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Repair.cs", "Repair.Run"),
                    "Historical repair obligation.")
            ],
            reviewedCommit: priorCandidate);

        kernel.RetryTask(
            goal.Id,
            developer.Id,
            "Repair the historical Reviewer finding.",
            RetryCause.CriterionEvidenceOwnerMismatch,
            invalidateDownstream: false);
        kernel.RetryTask(
            goal.Id,
            tester.Id,
            "Re-admit Tester after the repair.",
            RetryCause.CriterionEvidenceOwnerMismatch,
            invalidateDownstream: false);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.NotNull(reviewer.LastVerification?.MergedReviewFindings);

        DispatchTask(kernel, goal, developer, "develop-2", baseCommit: priorCandidate);
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, repairedCandidate);
        kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
            "develop-2",
            "C:\\tmp",
            0,
            "ok",
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true));

        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Null(reviewer.LastVerification);
        Assert.Contains(reviewer.VerificationHistory, verification =>
            verification.MergedReviewFindings?.Any(finding =>
                finding.StableId == "historical-repair-obligation") is true);

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restoredReviewer = restored.GetTask(goal.Id, reviewer.Id);
        Assert.Equal(WorkTaskStatus.Assigned, restoredReviewer.Status);
        Assert.Null(restoredReviewer.LastVerification);
        Assert.Contains(restoredReviewer.VerificationHistory, verification =>
            verification.MergedReviewFindings?.Any(finding =>
                finding.StableId == "historical-repair-obligation") is true);

        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = Assert.Single(snapshot.Goals);
        var reviewerSnapshot = goalSnapshot.Tasks.Single(task => task.Id == reviewer.Id.Value);
        var historicalReview = reviewerSnapshot.VerificationHistory!.Single(verification =>
            verification.MergedReviewFindings?.Any(finding =>
                finding.StableId == "historical-repair-obligation") is true);
        var retainedFailureSnapshot = snapshot with
        {
            Goals =
            [
                goalSnapshot with
                {
                    Status = GoalStatus.Failed,
                    Tasks = goalSnapshot.Tasks
                        .Select(task => task.Id == reviewer.Id.Value
                            ? task with
                            {
                                Status = WorkTaskStatus.Failed,
                                LastVerification = historicalReview
                            }
                            : task)
                        .ToArray()
                }
            ]
        };
        var reloadedKernel = AgentOrchestratorKernel.FromSnapshot(retainedFailureSnapshot);
        var reloadedGoal = reloadedKernel.GetGoal(goal.Id);
        Assert.Equal(WorkTaskStatus.Assigned, reloadedKernel.GetTask(goal.Id, tester.Id).Status);
        Assert.Equal(WorkTaskStatus.Failed, reloadedKernel.GetTask(goal.Id, reviewer.Id).Status);
        Assert.NotNull(reloadedKernel.GetTask(goal.Id, reviewer.Id).LastVerification?.MergedReviewFindings);
        var scheduledRoles = new List<AgentRole>();
        var developerRetryCount = 0;
        var escalations = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: currentGoal =>
            {
                var nextTask = currentGoal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned);
                scheduledRoles.Add(nextTask.RequiredRole);
                DispatchTask(
                    reloadedKernel,
                    currentGoal,
                    nextTask,
                    $"fresh-{nextTask.RequiredRole}",
                    baseCommit: repairedCandidate);
                return DispatchStartOutcome.Started();
            },
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
            {
                if (taskId == developer.Id)
                {
                    developerRetryCount++;
                }

                return reloadedKernel.RetryTask(goalId, taskId, message, cause, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => escalations.Add(message),
            normalizeLifecycleState: (currentGoal, reason) =>
                reloadedKernel.NormalizeGoalLifecycleState(currentGoal.Id, reason));

        var testerAdvance = driver.AdvanceOnce(reloadedGoal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(testerAdvance.Outcome);
        Assert.Equal([AgentRole.Tester], scheduledRoles);
        var reloadedTester = reloadedKernel.GetTask(goal.Id, tester.Id);
        reloadedKernel.RecordTaskVerification(goal.Id, tester.Id, new TaskVerificationRecord(
            reloadedTester.LastDispatch!.Command,
            reloadedTester.LastDispatch.WorkingDirectory,
            0,
            "ok",
            string.Empty,
            DateTimeOffset.UtcNow));
        var freshReviewerBrief = reloadedKernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;
        Assert.Contains("historical-repair-obligation", freshReviewerBrief, StringComparison.Ordinal);

        var reviewerAdvance = driver.AdvanceOnce(reloadedGoal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(reviewerAdvance.Outcome);
        Assert.Equal([AgentRole.Tester, AgentRole.Reviewer], scheduledRoles);
        Assert.Equal(0, developerRetryCount);
        Assert.Empty(escalations);
        Assert.Equal(WorkTaskStatus.Running, reloadedKernel.GetTask(goal.Id, reviewer.Id).Status);
    }

    [Xunit.Theory(DisplayName = "Reviewer finding currency requires a proven changed completed repair")]
    [Xunit.InlineData(false, true, true)]
    [Xunit.InlineData(true, false, true)]
    [Xunit.InlineData(true, true, false)]
    public void ReviewerFindingCurrencyRequiresProvenChangedCompletedRepair(
        bool candidateChanged,
        bool reviewedCandidateKnown,
        bool developerCompleted)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var priorCandidate = new string('c', 40);
        var resultCandidate = candidateChanged ? new string('d', 40) : priorCandidate;

        DispatchTask(kernel, goal, developer, "develop-initial", baseCommit: new string('0', 40));
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, priorCandidate);
        kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
            "develop-initial", "C:\\tmp", 0, "ok", string.Empty, DateTimeOffset.UtcNow.AddMinutes(-10),
            WorkerResultPresent: true,
            HasCommittedChanges: true));
        PassVerification(kernel, goal, tester);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "Current obligation must remain actionable.",
            reviewedCommit: reviewedCandidateKnown ? priorCandidate : null);
        kernel.RetryTask(
            goal.Id,
            developer.Id,
            "Attempt repair.",
            RetryCause.CriterionEvidenceOwnerMismatch,
            invalidateDownstream: false);
        kernel.RetryTask(
            goal.Id,
            tester.Id,
            "Re-admit Tester.",
            RetryCause.CriterionEvidenceOwnerMismatch,
            invalidateDownstream: false);
        DispatchTask(kernel, goal, developer, "develop-current", baseCommit: priorCandidate);
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, resultCandidate);
        if (developerCompleted)
        {
            kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
                "develop-current", "C:\\tmp", 0, "ok", string.Empty, DateTimeOffset.UtcNow,
                WorkerResultPresent: true,
                HasCommittedChanges: candidateChanged));
        }
        else
        {
            kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Failed, "Repair failed.");
        }

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.NotNull(reviewer.LastVerification);
        Assert.Equal(
            VerifyingFindingDisposition.Current,
            VerifyingFindingCurrency.Classify(goal, reviewer, reviewer.LastVerification!));

        kernel.NormalizeGoalLifecycleState(goal.Id, "Conductor tick normalization.");

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.NotNull(reviewer.LastVerification?.MergedReviewFindings);
    }

    // Before the currency fix, the first two fixtures retried the Developer because the
    // convergence brief resurrected the Tester's superseded findings from verification history.
    [Xunit.Fact]
    public void StaleTesterFindingAfterOperatorCloseDoesNotReopenDeveloper()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var startedAt = new DateTimeOffset(2026, 9, 4, 21, 30, 0, TimeSpan.Zero);

        RecordCommittedDeveloperPass(kernel, goal, developer, startedAt);
        RecordTesterFindings(kernel, goal, tester, startedAt.AddMinutes(3));
        kernel.RetryTask(goal.Id, developer.Id, "Developer fixes resolved the Tester findings.");
        RecordCommittedDeveloperPass(kernel, goal, developer, startedAt.AddMinutes(12));
        kernel.ReportTaskProgress(goal.Id, tester.Id, WorkTaskStatus.Completed, "Operator closed the Tester after confirming the fixes.");
        kernel.RecordTaskVerification(
            goal.Id,
            tester.Id,
            ManualVerificationRecorder.Create(
                passed: true,
                "Operator manual pass confirmed the Tester findings are resolved.",
                "C:\\tmp",
                startedAt.AddMinutes(13)));
        RecordUnparseableReviewerNeedsWork(kernel, goal, reviewer, startedAt.AddMinutes(20));

        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Null(reviewer.LastVerification!.MergedReviewFindings);
        Assert.Empty(AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(
            goal,
            AgentRole.Tester,
            reviewer.LastVerification.CompletedAt));

        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        var timelineCountBeforeAdvance = goal.Timeline.Count;
        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(reviewer.Id, retriedTaskId);
        Assert.DoesNotContain(goal.Timeline.Skip(timelineCountBeforeAdvance), evt =>
            evt.TaskId == developer.Id && evt.Kind == ProgressKind.TaskRetried);
        Assert.DoesNotContain("owned-exit-200x-saturation-test-missing", retryMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("owned-exit-disposed-handle-probe-test-missing", retryMessage, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact]
    public void StaleTesterFindingAfterInvalidationDispatchesReviewerNotDeveloper()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var startedAt = new DateTimeOffset(2026, 9, 4, 22, 45, 0, TimeSpan.Zero);

        var reviewedCandidate = RecordCommittedDeveloperPass(kernel, goal, developer, startedAt);
        RecordTesterFindings(kernel, goal, tester, startedAt.AddMinutes(3), reviewedCandidate);
        kernel.RetryTask(goal.Id, developer.Id, "Reviewer requested an upstream correction.");
        Assert.Null(tester.LastVerification);
        RecordCommittedDeveloperPass(
            kernel,
            goal,
            developer,
            startedAt.AddMinutes(28),
            baseCommit: reviewedCandidate);
        RecordUnparseableReviewerNeedsWork(kernel, goal, reviewer, startedAt.AddMinutes(38));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Null(reviewer.LastVerification!.MergedReviewFindings);
        Assert.Empty(AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(
            goal,
            AgentRole.Tester,
            reviewer.LastVerification.CompletedAt));

        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(reviewer.Id, retriedTaskId);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == developer.Id && evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("auto-review-retry", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("owned-exit-200x-saturation-test-missing", retryMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("owned-exit-disposed-handle-probe-test-missing", retryMessage, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact]
    public void ManualPassSupersedesTesterFindingWithoutNewDeveloperCompletion()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var startedAt = new DateTimeOffset(2026, 9, 4, 21, 30, 0, TimeSpan.Zero);

        RecordCommittedDeveloperPass(kernel, goal, developer, startedAt);
        RecordTesterFindings(kernel, goal, tester, startedAt.AddMinutes(3));
        kernel.ReportTaskProgress(goal.Id, tester.Id, WorkTaskStatus.Completed, "Operator closed the Tester.");
        kernel.RecordTaskVerification(
            goal.Id,
            tester.Id,
            ManualVerificationRecorder.Create(
                passed: true,
                "Operator confirmed the Tester finding is resolved.",
                "C:\\tmp",
                startedAt.AddMinutes(4)));

        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Empty(AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(goal, tester));
    }

    [Xunit.Fact]
    public void ApparatusFailedTesterRerunDoesNotResolveItsCurrentFinding()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var startedAt = new DateTimeOffset(2026, 9, 4, 21, 30, 0, TimeSpan.Zero);

        RecordCommittedDeveloperPass(kernel, goal, developer, startedAt);
        RecordTesterFindings(kernel, goal, tester, startedAt.AddMinutes(3));
        kernel.RetryTask(goal.Id, tester.Id, "Retry Tester after an apparatus failure.");
        RecordUnparseableNeedsWork(kernel, goal, tester, startedAt.AddMinutes(4), "test");

        Assert.Equal(WorkTaskStatus.Failed, tester.Status);
        Assert.Null(tester.LastVerification!.MergedReviewFindings);
        var findings = AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(
            goal,
            AgentRole.Tester,
            tester.LastVerification.CompletedAt);
        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, finding =>
            finding.StableId == "owned-exit-200x-saturation-test-missing");
    }

    [Xunit.Fact]
    public void RetriedVerifierDoesNotResurrectClearedFinding()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var startedAt = new DateTimeOffset(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
        var candidate = RecordCommittedDeveloperPass(kernel, goal, developer, startedAt);
        RecordTesterFindings(kernel, goal, tester, startedAt.AddMinutes(1), candidate);
        var historicalFinding = tester.LastVerification!;

        kernel.RetryTask(
            goal.Id,
            tester.Id,
            "Retry the invalidated Tester.",
            RetryCause.CriterionEvidenceOwnerMismatch,
            invalidateDownstream: false);

        Assert.Null(tester.LastVerification);
        Assert.Equal(
            VerifyingFindingDisposition.InvalidatedByTaskRetry,
            VerifyingFindingCurrency.Classify(goal, tester, historicalFinding));
        Assert.Empty(AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(
            goal,
            AgentRole.Tester,
            historicalFinding.CompletedAt));
    }

    [Xunit.Fact]
    public void InterveningMergeCandidateDoesNotHideCompletedRepair()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var reviewedCandidate = RecordCommittedDeveloperPass(kernel, goal, developer, startedAt);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "Repair this current finding.",
            findings:
            [
                new ReviewFinding(
                    "merge-intervened-repair",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Repair.cs", "Repair.Run"),
                    "Repair this current finding.",
                    FindingSeverity.Blocking,
                    FindingCategory.Correctness)
            ],
            reviewedCommit: reviewedCandidate);
        var historicalReview = reviewer.LastVerification!;

        kernel.RetryTask(
            goal.Id,
            developer.Id,
            "Repair after merging current main.",
            RetryCause.CriterionEvidenceOwnerMismatch,
            invalidateDownstream: false);
        RecordCommittedDeveloperPass(
            kernel,
            goal,
            developer,
            DateTimeOffset.UtcNow.AddMinutes(1),
            baseCommit: "intervening-merge-candidate",
            resultCommit: "repaired-candidate");

        Assert.Equal(
            VerifyingFindingDisposition.InvalidatedByTaskRetry,
            VerifyingFindingCurrency.Classify(goal, reviewer, historicalReview));
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Null(reviewer.LastVerification);
    }

    [Xunit.Fact]
    public void OtherManualDeveloperDoesNotBreakRepairReconciliation()
    {
        var (kernel, goal) = SoftwareGoal();
        var repairingDeveloper = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var manualDeveloper = kernel.AddTask(
            goal.Id,
            AgentRole.Developer,
            "Manually verified unrelated Developer task",
            beforeRole: AgentRole.Developer);
        kernel.RetryTask(goal.Id, manualDeveloper.Id, "Record a manual completion.");
        kernel.RecordTaskVerification(
            goal.Id,
            manualDeveloper.Id,
            ManualVerificationRecorder.Create(true, "Operator verified unrelated work.", "C:\\tmp", DateTimeOffset.UtcNow));
        var reviewedCandidate = RecordCommittedDeveloperPass(
            kernel,
            goal,
            repairingDeveloper,
            DateTimeOffset.UtcNow.AddMinutes(-2));
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "Repair the reviewed candidate.",
            reviewedCommit: reviewedCandidate);
        var historicalReview = reviewer.LastVerification!;
        kernel.RetryTask(
            goal.Id,
            repairingDeveloper.Id,
            "Repair the reviewed candidate.",
            RetryCause.CriterionEvidenceOwnerMismatch,
            invalidateDownstream: false);
        RecordCommittedDeveloperPass(
            kernel,
            goal,
            repairingDeveloper,
            DateTimeOffset.UtcNow.AddMinutes(1),
            baseCommit: reviewedCandidate,
            resultCommit: "repaired-candidate");

        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = Assert.Single(snapshot.Goals);
        var historicalReviewSnapshot = goalSnapshot.Tasks
            .Single(task => task.Id == reviewer.Id.Value)
            .VerificationHistory!
            .Single(verification => verification.CompletedAt == historicalReview.CompletedAt);
        var retainedFailureSnapshot = snapshot with
        {
            Goals =
            [
                goalSnapshot with
                {
                    Status = GoalStatus.Failed,
                    Tasks = goalSnapshot.Tasks.Select(task => task.Id == reviewer.Id.Value
                        ? task with
                        {
                            Status = WorkTaskStatus.Failed,
                            LastVerification = historicalReviewSnapshot
                        }
                        : task).ToArray()
                }
            ]
        };
        var restored = AgentOrchestratorKernel.FromSnapshot(retainedFailureSnapshot);
        var restoredReviewer = restored.GetTask(goal.Id, reviewer.Id);
        var repairDecision = VerifyingFindingCurrency.Evaluate(
            restored.GetGoal(goal.Id),
            restoredReviewer,
            restoredReviewer.LastVerification!);

        Assert.Equal(VerifyingFindingDisposition.SupersededByCompletedRepair, repairDecision.Disposition);
        Assert.Equal(repairingDeveloper.Id, repairDecision.RepairTaskId);

        var exception = Record.Exception(() =>
            restored.NormalizeGoalLifecycleState(goal.Id, "Reconcile persisted lifecycle state."));

        Assert.Null(exception);
        Assert.Equal(WorkTaskStatus.Assigned, restored.GetTask(goal.Id, reviewer.Id).Status);
    }

    [Xunit.Fact]
    public void FreshReviewerReaffirmationRoutesToDeveloper()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var candidate = RecordCommittedDeveloperPass(
            kernel,
            goal,
            developer,
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var reaffirmed = new ReviewFinding(
            "reaffirmed-correctness-finding",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/Repair.cs", "Repair.Run"),
            "The fresh candidate still contains the defect.",
            FindingSeverity.Blocking,
            FindingCategory.Correctness);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            reaffirmed.Description,
            findings: [reaffirmed],
            reviewedCommit: candidate);
        TaskId? retriedTask = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTask = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(developer.Id, retriedTask);
        Assert.Contains(reaffirmed.StableId, retryMessage, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Parked)]
    [Xunit.InlineData(GoalStatus.WaitingForHuman)]
    public void ParkedGoalDoesNotReconcileFailedReviewer(GoalStatus parkedStatus)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var candidate = RecordCommittedDeveloperPass(kernel, goal, developer, DateTimeOffset.UtcNow.AddMinutes(-2));
        FailReviewerNeedsWork(kernel, goal, reviewer, "Repair this finding.", reviewedCommit: candidate);
        var historicalReview = reviewer.LastVerification!;
        kernel.RetryTask(
            goal.Id,
            developer.Id,
            "Repair the finding.",
            RetryCause.CriterionEvidenceOwnerMismatch,
            invalidateDownstream: false);
        RecordCommittedDeveloperPass(
            kernel,
            goal,
            developer,
            DateTimeOffset.UtcNow.AddMinutes(1),
            baseCommit: candidate,
            resultCommit: "repaired-candidate");
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = Assert.Single(snapshot.Goals);
        var historicalReviewSnapshot = goalSnapshot.Tasks
            .Single(task => task.Id == reviewer.Id.Value)
            .VerificationHistory!
            .Single(verification => verification.CompletedAt == historicalReview.CompletedAt);
        var parkedSnapshot = snapshot with
        {
            Goals =
            [
                goalSnapshot with
                {
                    Status = parkedStatus,
                    Tasks = goalSnapshot.Tasks.Select(task => task.Id == reviewer.Id.Value
                        ? task with
                        {
                            Status = WorkTaskStatus.Failed,
                            LastVerification = historicalReviewSnapshot
                        }
                        : task).ToArray()
                }
            ]
        };
        var restored = AgentOrchestratorKernel.FromSnapshot(parkedSnapshot);

        var changed = restored.NormalizeGoalLifecycleState(goal.Id, "Conductor normalization.");

        Assert.False(changed);
        Assert.Equal(parkedStatus, restored.GetGoal(goal.Id).Status);
        Assert.Equal(WorkTaskStatus.Failed, restored.GetTask(goal.Id, reviewer.Id).Status);
    }

    [Xunit.Fact]
    public void LaterVerificationReAdmitsRetainedFailedReviewer()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var candidate = RecordCommittedDeveloperPass(kernel, goal, developer, DateTimeOffset.UtcNow.AddMinutes(-2));
        FailReviewerNeedsWork(kernel, goal, reviewer, "First review finding.", reviewedCommit: candidate);
        var earlierReview = reviewer.LastVerification!;
        kernel.RetryTask(
            goal.Id,
            reviewer.Id,
            "Run a later review.",
            RetryCause.CriterionEvidenceOwnerMismatch,
            invalidateDownstream: false);
        FailReviewerNeedsWork(kernel, goal, reviewer, "Later review finding.", reviewedCommit: candidate);

        kernel.RetryTask(
            goal.Id,
            developer.Id,
            "Verified no-change Developer round.",
            RetryCause.CriterionEvidenceOwnerMismatch,
            invalidateDownstream: false);
        DispatchTask(kernel, goal, developer, "develop-no-change", baseCommit: candidate);
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, candidate);
        kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
            "develop-no-change",
            "C:\\tmp",
            0,
            "ok",
            string.Empty,
            DateTimeOffset.UtcNow.AddMinutes(1),
            WorkerResultPresent: true,
            HasCommittedChanges: false));

        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = Assert.Single(snapshot.Goals);
        var reviewerSnapshot = goalSnapshot.Tasks.Single(task => task.Id == reviewer.Id.Value);
        var earlierReviewSnapshot = reviewerSnapshot.VerificationHistory!
            .Single(verification => verification.CompletedAt == earlierReview.CompletedAt);
        var staleSnapshot = snapshot with
        {
            Goals =
            [
                goalSnapshot with
                {
                    Status = GoalStatus.Failed,
                    Tasks = goalSnapshot.Tasks.Select(task => task.Id == reviewer.Id.Value
                        ? task with
                        {
                            Status = WorkTaskStatus.Failed,
                            LastVerification = earlierReviewSnapshot
                        }
                        : task).ToArray()
                }
            ]
        };
        var restored = AgentOrchestratorKernel.FromSnapshot(staleSnapshot);

        var changed = restored.NormalizeGoalLifecycleState(goal.Id, "Conductor normalization.");

        Assert.True(changed);
        Assert.Equal(WorkTaskStatus.Assigned, restored.GetTask(goal.Id, reviewer.Id).Status);
        Assert.Contains(restored.GetTask(goal.Id, reviewer.Id).VerificationHistory, verification =>
            verification.CompletedAt > earlierReview.CompletedAt);
    }

    private static string RecordCommittedDeveloperPass(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec developer,
        DateTimeOffset completedAt,
        string? baseCommit = null,
        string? resultCommit = null)
    {
        var effectiveResultCommit = resultCommit ?? $"result-{completedAt.ToUnixTimeSeconds()}";
        DispatchTask(kernel, goal, developer, baseCommit: baseCommit ?? "base-commit");
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, effectiveResultCommit);
        kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
            "develop",
            "C:\\tmp",
            0,
            "ok",
            string.Empty,
            completedAt,
            WorkerResultPresent: true,
            HasCommittedChanges: true));
        return effectiveResultCommit;
    }

    private static void RecordTesterFindings(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec tester,
        DateTimeOffset completedAt,
        string? reviewedCommit = null)
    {
        var findings = new[]
        {
            new ReviewFinding(
                "owned-exit-200x-saturation-test-missing",
                ReviewFindingState.Open,
                new ReviewFindingLocation("tests/OwnedProcessExitObservationTests.cs", "Saturation"),
                "The saturation regression test is missing.",
                FindingSeverity.Blocking,
                FindingCategory.TestCoverage),
            new ReviewFinding(
                "owned-exit-disposed-handle-probe-test-missing",
                ReviewFindingState.Open,
                new ReviewFindingLocation("tests/OwnedProcessExitObservationTests.cs", "DisposedHandle"),
                "The disposed-handle probe regression test is missing.",
                FindingSeverity.Blocking,
                FindingCategory.TestCoverage)
        };
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: inspect focused behavior",
            "tests: fail - two regression cases are missing",
            "commit: none",
            "blockers: missing regression coverage",
            $"findings: {JsonSerializer.Serialize(findings)}",
            "touched_anchors: []",
            "verdict: needs-work",
            "model_fit: fixture/model - adequate - source verification",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");

        DispatchTask(kernel, goal, tester, "test", baseCommit: reviewedCommit);
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test",
            "C:\\tmp",
            1,
            stdout,
            string.Empty,
            completedAt,
            StandardOutputPath: "C:\\tmp\\tester.out.log",
            WorkerResultPresent: true,
            ReviewedCommit: reviewedCommit));
        Assert.Equal(2, tester.LastVerification!.MergedReviewFindings?.Count);
    }

    private static void RecordUnparseableReviewerNeedsWork(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        DateTimeOffset completedAt) =>
        RecordUnparseableNeedsWork(kernel, goal, reviewer, completedAt, "review");

    private static void RecordUnparseableNeedsWork(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        DateTimeOffset completedAt,
        string command)
    {
        DispatchTask(kernel, goal, task, command);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            command,
            "C:\\tmp",
            1,
            string.Join(
                Environment.NewLine,
                "blockers: src/Target.cs:1 - Reviewer could not validate the candidate.",
                "verdict: needs-work"),
            string.Empty,
            completedAt,
            StandardOutputPath: "C:\\tmp\\reviewer.out.log",
            WorkerResultPresent: false));
    }
}
