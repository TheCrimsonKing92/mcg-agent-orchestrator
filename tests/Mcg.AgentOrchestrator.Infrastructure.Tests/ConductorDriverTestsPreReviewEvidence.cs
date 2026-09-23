using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Xml.Linq;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPreReviewEvidence
{
    private static string CreateTempDirectory() => ConductorDriverTests.CreateTempDirectory();

    [Xunit.Fact]
    public void PreReviewEvidence_DualArmVacuousCandidateGreen_AllowsReviewerDispatch()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var focusedRuns = 0;
        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => FocusedPreReviewContext("abc123"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                Assert.Contains("ConductorDriverTests", request, StringComparison.Ordinal);
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Green);
            },
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            dispatchAndStart: _ =>
            {
                Assert.Equal(PreReviewEvidenceDisposition.Green, reviewer.PreReviewEvidenceReceipt?.Disposition);
                dispatches++;
                return DispatchStartOutcome.Started();
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Equal(1, dispatches);
        Assert.Equal("abc123", reviewer.PreReviewEvidenceReceipt?.CandidateSha);
    }

    [Xunit.Fact]
    public void PreReviewEvidence_RejectedResultWithoutOutcomeReportsUnknown()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var retryMessages = new List<string>();
        var escalations = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => FocusedPreReviewContext("rejected-sha"),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: false,
                Passed: false,
                Summary: "focused evidence request rejected",
                Checks: []),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retryMessages.Add(message);
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => escalations.Add(message));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(
            WorkTaskStatus.Assigned,
            kernel.Goals.Single(candidate => candidate.Id == goal.Id).Tasks.Single(task => task.Id == tester.Id).Status);
        Assert.Contains("outcome=unknown", retryMessages.Single(), StringComparison.Ordinal);
        Assert.DoesNotContain("outcome=valid-evidence", retryMessages.Single(), StringComparison.Ordinal);

        var afterRetry = kernel.Goals.Single(candidate => candidate.Id == goal.Id);
        PassVerification(kernel, afterRetry, afterRetry.Tasks.Single(task => task.Id == tester.Id));
        var second = driver.AdvanceOnce(
            kernel.Goals.Single(candidate => candidate.Id == goal.Id),
            ConductorAutonomyPolicy.Permissive);

        Assert.Single(retryMessages);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(second.Outcome);
        Assert.Contains("no further paid retry was started", Assert.Single(escalations), StringComparison.Ordinal);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void PreReviewEvidence_InFlightRun_ReturnsThenReconcilesAfterRestart()
    {
        var root = CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
            {
                PassVerification(kernel, goal, task);
            }

            var completionGate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
            var attemptRoot = Path.Combine(root, "pre-review-evidence-attempts");
            var startingCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                attemptCompletionGateForTests: completionGate,
                acquireStableSlotLease: (_, _) => null);
            var focusedRuns = 0;
            var dispatches = 0;
            FocusedEvidenceRunResult RunEvidence(Goal _, string request)
            {
                focusedRuns++;
                return PassingPreReviewEvidence(request);
            }

            var startingDriver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("async-sha"),
                runFocusedEvidence: RunEvidence,
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: startingCoordinator);

            var scheduled = startingDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(scheduled.Outcome);
            var heldAttempt = completionGate.RequiredHandleForTests(goal.Id.Value);
            Assert.Equal($"pre-review-evidence:{heldAttempt.Attempt.AttemptId}", held.StableIdentity);
            Assert.Equal(0, focusedRuns);
            Assert.Equal(0, dispatches);
            Assert.Equal(1, completionGate.HeldCount);

            heldAttempt.CompleteForTests();
            Assert.Equal(1, focusedRuns);

            var restartedCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => throw new InvalidOperationException("completed attempt must be reconciled"),
                acquireStableSlotLease: (_, _) => null);
            var restartedDriver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("async-sha"),
                runFocusedEvidence: RunEvidence,
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: restartedCoordinator);

            var reconciled = restartedDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(reconciled.Outcome);
            Assert.Equal(1, focusedRuns);
            Assert.Equal(1, dispatches);
            Assert.Equal(PreReviewEvidenceDisposition.Green, reviewer.PreReviewEvidenceReceipt?.Disposition);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void PostReviewEvidence_InFlightTypedRun_ReturnsThenReconcilesAfterRestart()
    {
        var root = CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            {
                PassVerification(kernel, goal, task);
            }

            const string request = "Infrastructure.Tests: ConductorDriverTests";
            const string blocker = "Infrastructure.Tests ConductorDriverTests receipt is missing.";
            FailReviewerNeedsWork(
                kernel,
                goal,
                reviewer,
                blocker,
                evidenceRequest: request, implicitFindingCategory: FindingCategory.AcceptanceOwned);

            var completionGate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
            var attemptRoot = Path.Combine(root, "pre-review-evidence-attempts");
            var startingCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                attemptCompletionGateForTests: completionGate,
                acquireStableSlotLease: (_, _) => null);
            var focusedRuns = 0;
            var retries = 0;
            FocusedEvidenceRunResult RunEvidence(Goal _, string request)
            {
                focusedRuns++;
                Assert.Equal("Infrastructure.Tests: ConductorDriverTests", request);
                return PassingPreReviewEvidence(request);
            }

            ConductorDriver MakePostReviewDriver(ConductorParallelAcceptanceAttemptCoordinator coordinator) =>
                MakeDriver(
                    getFacts: _ => GoalLifecycleFacts.None,
                    getPreReviewEvidenceContext: _ => NoPreReviewContext("def5678"),
                    runFocusedEvidence: RunEvidence,
                    retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                    {
                        retries++;
                        return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                    },
                    recordFindingEvidenceRequest: (goalId, taskId, message) =>
                        kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                    recordFindingEvidenceRun: (goalId, taskId, message) =>
                        kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                    focusedEvidenceAttemptCoordinator: coordinator,
                    recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                        kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

            var startingDriver = MakePostReviewDriver(startingCoordinator);
            var scheduled = startingDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Held>(scheduled.Outcome);
            Assert.Equal(0, focusedRuns);
            Assert.Equal(0, retries);
            Assert.Equal(1, completionGate.HeldCount);

            completionGate.RequiredHandleForTests(goal.Id.Value).CompleteForTests();
            Assert.Equal(1, focusedRuns);

            var restartedCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => throw new InvalidOperationException("completed attempt must be reconciled"),
                acquireStableSlotLease: (_, _) => null);
            var restartedDriver = MakePostReviewDriver(restartedCoordinator);

            var reconciled = restartedDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(reconciled.Outcome);
            Assert.Equal(1, focusedRuns);
            Assert.Equal(1, retries);
            Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void PreReviewEvidence_ProcessDiesAfterRestart_RelaunchesWithoutReceipt()
    {
        var root = CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
            {
                PassVerification(kernel, goal, task);
            }

            var now = new DateTimeOffset(2026, 8, 3, 19, 0, 0, TimeSpan.Zero);
            var attemptRoot = Path.Combine(root, "pre-review-evidence-attempts");
            var launches = 0;
            var focusedRuns = 0;
            var dispatches = 0;
            var startingCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                utcNow: () => now,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ =>
                {
                    launches++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7301);
                });
            var startingDriver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("restart-sha"),
                runFocusedEvidence: (_, request) =>
                {
                    focusedRuns++;
                    return PassingPreReviewEvidence(request);
                },
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: startingCoordinator);

            var scheduled = startingDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Held>(scheduled.Outcome);
            Assert.Equal(1, launches);

            now = now.AddMinutes(1);
            var restartedCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                utcNow: () => now,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ =>
                {
                    launches++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7302);
                },
                recentHeartbeatGrace: TimeSpan.Zero);
            var restartedDriver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("restart-sha"),
                runFocusedEvidence: (_, request) =>
                {
                    focusedRuns++;
                    return PassingPreReviewEvidence(request);
                },
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: restartedCoordinator);

            var recovered = restartedDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(recovered.Outcome);
            Assert.Contains("did not run (ProcessDied)", held.Reason, StringComparison.Ordinal);
            Assert.Null(reviewer.PreReviewEvidenceReceipt);
            Assert.Equal(1, launches);
            Assert.Equal(0, focusedRuns);
            Assert.Equal(0, dispatches);

            var relaunched = restartedDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Held>(relaunched.Outcome);
            Assert.Equal(2, launches);
            Assert.Null(reviewer.PreReviewEvidenceReceipt);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void PreReviewEvidence_FaultedRun_EscalatesWithoutMappingReceipt()
    {
        var root = CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
            {
                PassVerification(kernel, goal, task);
            }

            var completionGate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "pre-review-evidence-attempts"),
                isProcessAlive: _ => true,
                attemptCompletionGateForTests: completionGate,
                acquireStableSlotLease: (_, _) => null);
            var focusedRuns = 0;
            var dispatches = 0;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("fault-sha"),
                runFocusedEvidence: (_, _) =>
                {
                    focusedRuns++;
                    throw new InvalidOperationException("simulated evidence fault");
                },
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: coordinator);

            var scheduled = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Held>(scheduled.Outcome);
            Assert.Equal(0, focusedRuns);
            completionGate.RequiredHandleForTests(goal.Id.Value).CompleteForTests();
            Assert.Equal(1, focusedRuns);

            var reconciled = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(reconciled.Outcome);
            Assert.Contains("PRE_REVIEW_EVIDENCE_FAILED", escalated.Reason, StringComparison.Ordinal);
            Assert.Contains("simulated evidence fault", escalated.Reason, StringComparison.Ordinal);
            Assert.Null(reviewer.PreReviewEvidenceReceipt);
            Assert.Equal(0, dispatches);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void PreReviewEvidence_BuildSlotsBusy_HoldsWithoutMappingReceipt()
    {
        var root = CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
            {
                PassVerification(kernel, goal, task);
            }

            var completionGate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "pre-review-evidence-attempts"),
                isProcessAlive: _ => true,
                attemptCompletionGateForTests: completionGate,
                acquireStableSlotLease: (_, _) => throw new DotnetBuildSlotsBusyException(
                    new DotnetBuildLeaseAcquisition.SlotsBusy("pre-review-evidence", [])));
            var focusedRuns = 0;
            var dispatches = 0;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("slots-busy-sha"),
                runFocusedEvidence: (_, request) =>
                {
                    focusedRuns++;
                    return PassingPreReviewEvidence(request);
                },
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: coordinator);

            var scheduled = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Held>(scheduled.Outcome);
            Assert.Equal(0, focusedRuns);
            completionGate.RequiredHandleForTests(goal.Id.Value).CompleteForTests();

            var reconciled = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(reconciled.Outcome);
            Assert.Contains("did not run (BlockedBuildSlot)", held.Reason, StringComparison.Ordinal);
            Assert.Null(reviewer.PreReviewEvidenceReceipt);
            Assert.Equal(0, focusedRuns);
            Assert.Equal(0, dispatches);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_stale_sha_reruns_and_same_sha_green_is_idempotent")]
    public void ConductorDriverPreReviewStaleShaRerunsAndSameShaGreenIsIdempotent()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        kernel.RecordPreReviewEvidence(
            goal.Id,
            reviewer.Id,
            GreenPreReviewReceipt(goal, "old-sha"));
        var focusedRuns = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => FocusedPreReviewContext("new-sha"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return PassingPreReviewEvidence(request);
            },
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Equal("new-sha", reviewer.PreReviewEvidenceReceipt?.CandidateSha);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_red_routes_developer_with_exact_failing_test_ids")]
    public void ConductorDriverPreReviewRedRoutesDeveloperWithExactFailingTestIds()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        string? retryMessage = null;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "red-sha",
                [
                    "dotnet test Core.Tests --filter FullyQualifiedName~CoreFailure",
                    "dotnet test Infrastructure.Tests --filter FullyQualifiedName~InfrastructureFailure"
                ],
                "Core.Tests: FullyQualifiedName~CoreFailure; Infrastructure.Tests: FullyQualifiedName~InfrastructureFailure",
                "Cross-project change mapped to two focused checks.",
                NoApplicableTests: false,
                MappingNeedsInput: false),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: false,
                Summary: "one focused test failed",
                Checks:
                [
                    new AcceptanceCheckResult(
                        "pre-review focused evidence",
                        false,
                        1,
                        "[FAIL] this output tail is not an evidence contract",
                        ArtifactsPath: "C:\\receipts\\red",
                        TestResultPaths: ["C:\\receipts\\red\\result.trx"],
                        FailingTestIdentities: ["Mcg.Tests.ConductorDriverBlocksReview(value: 42)"])
                ]),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Contains("Mcg.Tests.ConductorDriverBlocksReview(value: 42)", retryMessage, StringComparison.Ordinal);
        Assert.Equal(developer.Id, retriedTaskId);
        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Equal(PreReviewEvidenceDisposition.Red, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Equal(
            ["Mcg.Tests.ConductorDriverBlocksReview(value: 42)"],
            reviewer.PreReviewEvidenceReceipt?.FailingTestIdentities);
    }

    [Xunit.Fact]
    public void PreReview_RedWithoutTrx_RoutesDeveloperWithDiagnostic()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var retriedTaskIds = new List<TaskId>();
        string? retryMessage = null;
        string? escalation = null;
        const string diagnostic = "error CS0019: Operator '??=' cannot be applied to operands of type 'Func<GoalId, Goal?>' and 'lambda expression'";
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => FocusedPreReviewContext("plumbing-red-sha"),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: false,
                Summary: "build failed before tests executed",
                Checks: [new AcceptanceCheckResult("focused build", false, 1, diagnostic)]),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskIds.Add(taskId);
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            writeEscalation: (_, _, message) => escalation = message);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal([developer.Id], retriedTaskIds);
        Assert.DoesNotContain(tester.Id, retriedTaskIds);
        Assert.Contains(diagnostic, retryMessage, StringComparison.Ordinal);
        Assert.Contains("plumbing-red-sha", retryMessage, StringComparison.Ordinal);
        Assert.Null(escalation);
        Assert.Equal(PreReviewEvidenceDisposition.Red, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Empty(reviewer.PreReviewEvidenceReceipt?.FailingTestIdentities ?? []);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_no_applicable_tests_is_explicit_green_path")]
    public void ConductorDriverPreReviewNoApplicableTestsIsExplicitGreenPath()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var focusedRuns = 0;
        var dispatched = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "docs-sha",
                [],
                null,
                "Documentation-only change; no build or test command is required.",
                NoApplicableTests: true,
                MappingNeedsInput: false),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return PassingPreReviewEvidence(request);
            },
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            dispatchAndStart: _ =>
            {
                dispatched = true;
                return DispatchStartOutcome.Started();
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.True(dispatched);
        Assert.Equal(PreReviewEvidenceDisposition.NoApplicableTests, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Contains("Documentation-only", reviewer.PreReviewEvidenceReceipt?.MappingReason, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_mapping_needs_input_blocks_paid_reviewer_start")]
    public void ConductorDriverPreReviewMappingNeedsInputBlocksPaidReviewerStart()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var dispatched = false;
        string? escalation = null;
        TaskId? retriedTaskId = null;
        PreReviewEvidenceReceipt? recordedReceipt = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "unmapped-sha",
                ["dotnet test broad"],
                null,
                "Changed files do not map to a focused target.",
                NoApplicableTests: false,
                MappingNeedsInput: true),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
            {
                recordedReceipt = receipt;
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ =>
            {
                dispatched = true;
                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(dispatched);
        Assert.Null(escalation);
        Assert.Equal(tester.Id, retriedTaskId);
        Assert.Equal(PreReviewEvidenceDisposition.MappingNeedsInput, recordedReceipt?.Disposition);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact]
    public void ConductorDriverGeneratedArtifactMappingRetriesWritableDeveloperInsteadOfTester()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var retriedTaskIds = new List<TaskId>();
        string? retryMessage = null;
        var escalations = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => ConductorDriver.BuildPreReviewEvidenceContext(
                "generated-sha",
                ["src/Mcg.AgentOrchestrator.Core/bin/Debug/generated.dll"]),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskIds.Add(taskId);
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            writeEscalation: (_, _, message) => escalations.Add(message));

        var first = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal([developer.Id], retriedTaskIds);
        Assert.Contains("src/Mcg.AgentOrchestrator.Core/bin/Debug/generated.dll", retryMessage, StringComparison.Ordinal);
        var afterRetry = kernel.Goals.Single(candidate => candidate.Id == goal.Id);
        Assert.Equal(WorkTaskStatus.Assigned, afterRetry.Tasks.Single(task => task.RequiredRole == AgentRole.Tester).Status);
        Assert.True(first.Outcome is ConductorAdvanceOutcome.Executed);

        PassVerification(kernel, afterRetry, afterRetry.Tasks.Single(task => task.RequiredRole == AgentRole.Developer));
        var afterDeveloper = kernel.Goals.Single(candidate => candidate.Id == goal.Id);
        PassVerification(kernel, afterDeveloper, afterDeveloper.Tasks.Single(task => task.RequiredRole == AgentRole.Tester));
        var readyAgain = kernel.Goals.Single(candidate => candidate.Id == goal.Id);

        var second = driver.AdvanceOnce(readyAgain, ConductorAutonomyPolicy.Permissive);

        Assert.Equal([developer.Id], retriedTaskIds);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(second.Outcome);
        Assert.Single(escalations);
        Assert.Contains("no further paid retry was started", escalations[0], StringComparison.Ordinal);
        Assert.Contains("src/Mcg.AgentOrchestrator.Core/bin/Debug/generated.dll", escalations[0], StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ConductorDriverPreReviewUsesEmptyCandidateDiffInsteadOfGeneratedPathsFromBrief()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Review a clean candidate after historical src/App/bin/Debug/generated.dll evidence");
        _ = GoalWorktrees.Ensure(root, goal.Id);

        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        var context = driver.GetPreReviewEvidenceContext(goal);

        Assert.NotNull(context.CandidateSha);
        Assert.True(context.NoApplicableTests);
        Assert.False(context.MappingNeedsInput);
        Assert.False(context.RequiresSourceCleanup);
        Assert.Empty(context.SourceCleanupPaths ?? []);
        Assert.Contains("No changed files detected", context.MappingReason, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_mapper_routes_project_and_exclusion_checks_to_bounded_evidence")]
    public void ConductorDriverPreReviewMapperRoutesProjectAndExclusionChecksToBoundedEvidence()
    {
        var cases = new[]
        {
            (
                Name: "core",
                Files: new[] { "src/Mcg.AgentOrchestrator.Core/Domain/TaskSpec.cs" },
                MappingNeedsInput: false,
                NoApplicableTests: true,
                RequestFragment: (string?)null),
            (
                Name: "infrastructure",
                Files: new[] { "src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerResultParser.cs" },
                MappingNeedsInput: false,
                NoApplicableTests: true,
                RequestFragment: (string?)null),
            (
                Name: "dashboard",
                Files: new[] { "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.OperatorShell.cs" },
                MappingNeedsInput: false,
                NoApplicableTests: false,
                RequestFragment: "Category!=HostIntegration"),
            (
                Name: "orchestration",
                Files: new[] { "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs" },
                MappingNeedsInput: false,
                NoApplicableTests: false,
                RequestFragment: "Infrastructure.Tests:"),
            (
                Name: "docs",
                Files: new[] { "docs/operator-runbook.md" },
                MappingNeedsInput: false,
                NoApplicableTests: true,
                RequestFragment: (string?)null),
            (
                Name: "more-than-five-source-files",
                Files: Enumerable.Range(1, 6)
                    .Select(index => $"src/Mcg.AgentOrchestrator.Core/Domain/Changed{index}.cs")
                    .ToArray(),
                MappingNeedsInput: false,
                NoApplicableTests: true,
                RequestFragment: (string?)null),
            (
                Name: "full-suite",
                Files: new[] { "Directory.Build.props" },
                MappingNeedsInput: false,
                NoApplicableTests: true,
                RequestFragment: (string?)null),
            (
                Name: "generated-unmapped",
                Files: new[] { "src/Mcg.AgentOrchestrator.Core/bin/Debug/generated.dll" },
                MappingNeedsInput: true,
                NoApplicableTests: false,
                RequestFragment: (string?)null)
        };

        foreach (var testCase in cases)
        {
            var context = ConductorDriver.BuildPreReviewEvidenceContext(
                $"{testCase.Name}-sha",
                testCase.Files);

            Assert.Equal(testCase.MappingNeedsInput, context.MappingNeedsInput);
            Assert.Equal(testCase.NoApplicableTests, context.NoApplicableTests);
            if (testCase.RequestFragment is null)
            {
                Assert.Null(context.FocusedRequest);
            }
            else
            {
                Assert.Contains(testCase.RequestFragment, context.FocusedRequest, StringComparison.Ordinal);
            }
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_respects_worker_admission_before_running_focused_evidence")]
    public void ConductorDriverPreReviewRespectsWorkerAdmissionBeforeRunningFocusedEvidence()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var focusedRuns = 0;
        var policy = ConductorAutonomyPolicy.Conservative;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => policy.MaxConcurrentPaidWorkers,
            getPreReviewEvidenceContext: _ => FocusedPreReviewContext("admission-sha"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return PassingPreReviewEvidence(request);
            });

        var result = driver.AdvanceOnce(goal, policy);

        Assert.Equal(0, focusedRuns);
        Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_mapping_gap_retries_tester_instead_of_escalating")]
    public void ConductorDriverPreReviewMappingGapRetriesTesterInsteadOfEscalating()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        TaskId? retriedTaskId = null;
        var dispatched = false;
        var escalated = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "mapping-gap-sha",
                [],
                null,
                "unmapped test impact",
                NoApplicableTests: false,
                MappingNeedsInput: true),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ =>
            {
                dispatched = true;
                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, _) => escalated = true);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
        Assert.True(dispatched);
        Assert.False(escalated);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_cardinality_mismatch_is_typed_tester_fallback")]
    public void ConductorDriverPreReviewCardinalityMismatchIsTypedTesterFallback()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        TaskId? retriedTaskId = null;
        PreReviewEvidenceReceipt? recordedReceipt = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => FocusedPreReviewContext("cardinality-sha"),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "broker returned two checks for one planned command",
                Checks:
                [
                    new AcceptanceCheckResult("first", true, 0, null),
                    new AcceptanceCheckResult("second", true, 0, null)
                ]),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
            {
                recordedReceipt = receipt;
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started());

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
        Assert.All(
            recordedReceipt?.Checks ?? [],
            check => Assert.Equal("(unmapped: check/command cardinality mismatch)", check.Command));
    }

    [Xunit.Fact]
    public void PreReview_MultipleFocusedChecks_PreserveOneToOneReceiptMapping()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        string[] targets =
        [
            "Core.Tests: DispatchOutcomeClassifyTests",
            "Infrastructure.Tests: WorkerDispatchTestsWorkerResultClassification"
        ];
        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "multi-focused-sha",
                targets,
                string.Join("; ", targets),
                "Two focused targets map to two checks.",
                NoApplicableTests: false,
                MappingNeedsInput: false),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "two focused checks passed",
                Checks:
                [
                    new AcceptanceCheckResult("core focused check", true, 0, null),
                    new AcceptanceCheckResult("infrastructure focused check", true, 0, null)
                ]),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            dispatchAndStart: _ =>
            {
                dispatches++;
                return DispatchStartOutcome.Started();
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, dispatches);
        Assert.Equal(PreReviewEvidenceDisposition.Green, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Equal(targets, reviewer.PreReviewEvidenceReceipt?.Checks.Select(check => check.Command));
    }

    [Xunit.Fact]
    public void PreReview_PartialFocusedEvidence_RetriesTester()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        string[] targets =
        [
            "Core.Tests: DispatchOutcomeClassifyTests",
            "Infrastructure.Tests: WorkerDispatchTestsWorkerResultClassification"
        ];
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "partial-focused-sha",
                targets,
                string.Join("; ", targets),
                "Two focused targets require two checks.",
                NoApplicableTests: false,
                MappingNeedsInput: false),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "only one focused check returned",
                Checks: [new AcceptanceCheckResult("partial focused check", true, 0, null)]),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started());

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
        Assert.Equal(PreReviewEvidenceDisposition.MappingNeedsInput, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Equal(
            "(unmapped: check/command cardinality mismatch)",
            reviewer.PreReviewEvidenceReceipt?.Checks.Single().Command);
    }

    [Xunit.Fact]
    public void PreReview_ZeroTestApparatusFailure_RetriesTesterWithoutDeveloperFailure()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var retriedTaskIds = new List<TaskId>();
        var escalations = new List<string>();
        var check = new AcceptanceCheckResult(
            "focused selection",
            false,
            8,
            "executed 0 tests",
            FailureClassification: AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
            ExecutedTestCount: 0);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => FocusedPreReviewContext("zero-test-sha"),
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
                        "zero-test-sha",
                        FindingEvidenceArmDisposition.ApparatusFailure,
                        true,
                        false,
                        "executed=0",
                        [check])
                ],
                OutcomeReason: FindingEvidenceOutcomeReason.ApparatusFailure),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskIds.Add(taskId);
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            writeEscalation: (_, _, message) => escalations.Add(message));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal([tester.Id], retriedTaskIds);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(PreReviewEvidenceDisposition.MappingNeedsInput, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Empty(reviewer.PreReviewEvidenceReceipt?.FailingTestIdentities ?? []);

        var afterRetry = kernel.Goals.Single(candidate => candidate.Id == goal.Id);
        PassVerification(kernel, afterRetry, afterRetry.Tasks.Single(task => task.Id == tester.Id));
        var second = driver.AdvanceOnce(
            kernel.Goals.Single(candidate => candidate.Id == goal.Id),
            ConductorAutonomyPolicy.Permissive);

        Assert.Equal([tester.Id], retriedTaskIds);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(second.Outcome);
        Assert.Contains("no further paid retry was started", Assert.Single(escalations), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PreReview_EmptyFocusedEvidence_RetriesTester()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var target = "Infrastructure.Tests: MissingTests";
        TaskId? retriedTask = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "empty-coverage-sha",
                [target],
                target,
                "Focused target should map to a lane.",
                NoApplicableTests: false,
                MappingNeedsInput: false),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "focused evidence returned no checks",
                Checks: []),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTask = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started());

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTask);
    }

    [Xunit.Fact]
    public void PreReview_NoTester_DeduplicatesEscalationPerSha()
    {
        var (kernel, goal) = ReviewGoalWithoutTester();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        PassVerification(kernel, goal, developer);
        var target = "Infrastructure.Tests: MissingTests";
        var candidateSha = "d8061f95";
        var escalations = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                candidateSha,
                [target],
                target,
                "Focused target should map to a lane.",
                NoApplicableTests: false,
                MappingNeedsInput: false),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "focused evidence returned no checks",
                Checks: []),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            recordPreReviewMappingEscalationSuppressed: (goalId, taskId, sha, count) =>
                kernel.RecordPreReviewMappingEscalationSuppressed(goalId, taskId, sha, count),
            writeEscalation: (_, _, message) => escalations.Add(message));

        var first = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var second = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        candidateSha = "c48b3be5";
        var third = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(first.Outcome);
        Assert.IsType<ConductorAdvanceOutcome.Held>(second.Outcome);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(third.Outcome);
        Assert.Equal(2, escalations.Count);
        var suppressed = Assert.Single(goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.PreReviewMappingEscalationSuppressed));
        Assert.Contains("candidate_sha=d8061f95", suppressed.Message, StringComparison.Ordinal);
        Assert.Contains("suppressed_count=1", suppressed.Message, StringComparison.Ordinal);

        const string commandMarker = "Add one with: ";
        var commandStart = escalations[0].IndexOf(commandMarker, StringComparison.Ordinal);
        Assert.True(commandStart >= 0);
        var command = escalations[0][(commandStart + commandMarker.Length)..];
        Assert.Equal(
            [
                "add-task",
                "--goal",
                goal.Id.Value[..8],
                "Tester",
                "Resolve pre-review mapping for candidate d8061f95",
                "--before-role",
                "Reviewer"
            ],
            CliArgumentParser.SplitCommand(command));
    }

    [Xunit.Fact]
    public void BuildPreReviewEvidenceContext_SplitsOversizedFocusedFilterAtClauseBoundaries()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        string[] changedFiles =
        [
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs",
            "tests/Mcg.AgentOrchestrator.Core.Tests/DispatchOutcomeClassifyTests.cs"
        ];
        var plan = RepositoryTestImpactPlanner.Plan(changedFiles, root);
        var infrastructureCheck = Assert.Single(
            plan.Checks,
            check => check.TestProject == RepositoryTestProject.Infrastructure);
        var originalFilter = infrastructureCheck.Command[^1];

        var context = ConductorDriver.BuildPreReviewEvidenceContext("wide-impact-sha", changedFiles, root);
        var emitted = context.SelectedFocusedTests
            .Where(item => item.StartsWith("Infrastructure.Tests: ", StringComparison.Ordinal))
            .ToArray();

        Assert.False(context.MappingNeedsInput);
        Assert.True(emitted.Length >= 2);
        Assert.All(emitted, item => Assert.True(
            item.Length <= PreReviewFocusedRequestSplitter.MaxFocusedEvidenceFilterLength,
            $"Focused request item was {item.Length} characters."));
        Assert.Equal(
            originalFilter.Split('|'),
            emitted.SelectMany(item => item[(item.IndexOf(": ", StringComparison.Ordinal) + 2)..].Split('|')));
    }

    [Xunit.Fact]
    public void PremiseInfrastructureSelection_SplitItemsPassBrokerValidation()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var plan = RepositoryTestImpactPlanner.Plan(
            ["src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs"],
            root);
        var classNames = Assert.Single(
            plan.Checks,
            check => check.TestProject == RepositoryTestProject.Infrastructure).TestClassSelections!;
        Assert.Equal(36, classNames.Count);
        var filter = string.Join('|', classNames.Select(name => $"FullyQualifiedName~{name}"));
        Assert.True(PreReviewFocusedRequestSplitter.TrySplitRequestItems(
            "Infrastructure.Tests", filter, out var items));

        var accepted = GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(
            string.Join("; ", items),
            AcceptanceGateEngineSettings.Load(root),
            root,
            out var checks,
            out _,
            out var rejection);

        Assert.True(accepted, $"{rejection.Code}: {rejection.Detail}");
        Assert.NotEqual(FocusedEvidenceRejectionCode.OversizedFilter, rejection.Code);
        Assert.Equal(items.Count, checks.Count);
    }

    [Xunit.Fact]
    public void SplitFocusedItems_RequireOneGreenEvidenceCheckPerItem()
    {
        var filter = string.Join('|', Enumerable.Range(1, 60)
            .Select(index => $"FullyQualifiedName~SplitCoverageClass{index:D2}"));
        Assert.True(PreReviewFocusedRequestSplitter.TrySplitRequestItems(
            "Infrastructure.Tests", filter, out var items));
        var context = new PreReviewEvidenceContext(
            "coverage-sha",
            items,
            string.Join("; ", items),
            "Split focused coverage.",
            NoApplicableTests: false,
            MappingNeedsInput: false);
        var allChecks = items
            .Select(item => new AcceptanceCheckResult(item, true, 0, null))
            .ToArray();
        var complete = new FocusedEvidenceRunResult(
            context.FocusedRequest!, true, true, "all split checks passed", allChecks);
        var partial = complete with { Checks = allChecks[..^1] };

        Assert.True(PreReviewEvidenceReceipts.ValidateCoverage(context, complete, out var completeFailure));
        Assert.Equal(string.Empty, completeFailure);
        Assert.False(PreReviewEvidenceReceipts.ValidateCoverage(context, partial, out var partialFailure));
        Assert.Equal($"cardinality mismatch: planned={items.Count} actual={items.Count - 1}", partialFailure);
    }

    [Xunit.Fact]
    public void FocusedRequestSplitter_PreservesUnderLimitItemAndRefusesUnsafeOversizedShapes()
    {
        const string underLimit = "FullyQualifiedName~ConductorDriverTestsPreReviewEvidence";
        Assert.True(PreReviewFocusedRequestSplitter.TrySplitRequestItems(
            "Infrastructure.Tests", underLimit, out var unchanged));
        Assert.Equal(["Infrastructure.Tests: " + underLimit], unchanged);

        var oversizedClause = "FullyQualifiedName~" + new string('A', 1100);
        var withFittingSibling = $"FullyQualifiedName~SmallTests|{oversizedClause}";
        Assert.False(PreReviewFocusedRequestSplitter.TrySplitRequestItems(
            "Infrastructure.Tests", withFittingSibling, out var unsplittable));
        Assert.Equal(["Infrastructure.Tests: " + withFittingSibling], unsplittable);

        var positivePrefix = string.Join('|', Enumerable.Range(1, 50)
            .Select(index => $"FullyQualifiedName~ShapeClass{index:D2}"));
        Assert.False(PreReviewFocusedRequestSplitter.TrySplitRequestItems(
            "Infrastructure.Tests", $"{positivePrefix}&FullyQualifiedName~Required", out _));
        Assert.False(PreReviewFocusedRequestSplitter.TrySplitRequestItems(
            "Infrastructure.Tests", $"{positivePrefix}|FullyQualifiedName!~Excluded", out _));
    }

    [Xunit.Fact]
    public void BuildPreReviewEvidenceContext_OversizedSingleClauseNeedsInputWithoutPartialEmission()
    {
        var root = CreateTempDirectory();
        const string relativePath =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OversizedSelectionTests.cs";
        try
        {
            var absolutePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            File.WriteAllText(absolutePath, $"public sealed class {new string('A', 1100)} {{ }}");

            var context = ConductorDriver.BuildPreReviewEvidenceContext(
                "oversized-clause-sha", [relativePath], root);

            Assert.True(context.MappingNeedsInput);
            Assert.False(context.NoApplicableTests);
            Assert.Null(context.FocusedRequest);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

}
