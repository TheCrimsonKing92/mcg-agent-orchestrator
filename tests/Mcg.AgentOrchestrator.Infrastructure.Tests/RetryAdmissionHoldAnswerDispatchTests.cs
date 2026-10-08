using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RetryAdmissionHoldAnswerDispatchTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void Answered_retry_admission_hold_starts_worker_on_next_tick_without_second_recovery_choice()
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        var workspace = OrchestratorWorkspace.ForDirectory(root, workingDirectory);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        WriteSkill(workingDirectory, "orchestrator-dogfood");
        var firstAt = DateTimeOffset.Parse("2026-07-07T12:00:00Z");
        var kernel = new AgentOrchestratorKernel(new TestClock(firstAt));
        var planner = new TaskSpec(TaskId.New(), "Plan the recovered retry.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Resume answered retry admission", [planner]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Resume answered retry admission", ["An answered hold starts one retry."],
            VerificationClass.TestVerifiable, [], []));
        var agents = new[] { SubscriptionPlannerAgent("planner", "Planner") };
        var profiles = DispatchTestProfiles();
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RetryTask(goal.Id, planner.Id, "Retry after the unsuccessful attempt.",
            retryCause: RetryCause.ProviderInterruption);
        var first = WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
            kernel, goal, agents, profiles, workspace.PromptDirectory, workingDirectory,
            firstAt, commandExists: _ => true,
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli);
        var firstDispatch = Xunit.Assert.Single(first.Dispatches);
        var fingerprint = Xunit.Assert.IsType<RetryContextFingerprint>(firstDispatch.Task.LastDispatch!.RetryContextFingerprint);
        var firstAdmission = kernel.RecordPreparedRetryAdmission(
            goal.Id, planner.Id, fingerprint, PaidRouteClassification.Paid, firstAt);
        Xunit.Assert.Equal(RetryAdmissionDecision.Allowed, firstAdmission.Decision);
        var recorded = Xunit.Assert.IsType<TaskDispatchRecord>(planner.LastDispatch);
        kernel.RecordTaskProcessStarted(goal.Id, planner.Id, new TaskProcessRecord(
            4101, recorded.Command, recorded.WorkingDirectory,
            Path.Combine(root, "first.out"), Path.Combine(root, "first.err"), Path.Combine(root, "first.exit"),
            firstAt, firstAt.AddSeconds(1), 1));
        kernel.RecordTaskVerification(goal.Id, planner.Id, new TaskVerificationRecord(
            "worker", workingDirectory, 1, "", "First attempt failed.", firstAt,
            DispatchStartedAt: firstAt));
        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Failed, "First attempt failed.");
        kernel.RetryTask(goal.Id, planner.Id, "Retry after the unsuccessful attempt.",
            retryCause: RetryCause.ProviderInterruption);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        repository.SaveAsync(kernel).GetAwaiter().GetResult();
        var sandbox = new WorkerSandboxOptions(false,
            WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
        var operations = new GoalDispatchOperations();
        var firstTick = operations.StartSubscriptionReadyTasks(
            kernel, workspace, goal, agents, profiles,
            checkpointBeforeWorkerStart: (_, _, _, _) => { },
            runner: new BackgroundDispatchRunner(disableProcessStart: true),
            sandboxOptions: sandbox,
            commandExists: DispatcherProviderProbeFakes.ProviderCommandsPresent,
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli);
        Xunit.Assert.Empty(firstTick.Processes.Tasks);
        var heldTask = kernel.GetTask(goal.Id, planner.Id);
        var prevented = Xunit.Assert.Single(heldTask.RetryAdmissionHistory,
            receipt => receipt.Decision == RetryAdmissionDecision.Prevented);
        Xunit.Assert.Equal(RetryCause.UnchangedContextRepeat, prevented.Cause);
        Xunit.Assert.Equal(RetryAdmissionRoute.EnvironmentalHold, prevented.Route);
        var request = Xunit.Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Xunit.Assert.Equal(HumanWaitKind.RecoveryChoice, request.Kind);
        Xunit.Assert.Equal(fingerprint.Value, request.BlockerFingerprint);

        kernel.SubmitHumanInput(request.Id, "Provider recovered.");
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, planner.Id).Status);
        Xunit.Assert.Contains(kernel.GetTask(goal.Id, planner.Id).RetryAdmissionHistory,
            receipt => receipt.Decision == RetryAdmissionDecision.RecoveryEvidence);
        repository.SaveAsync(kernel).GetAwaiter().GetResult();
        var processStartCheckpoints = 0;
        var startRefusal = Xunit.Assert.Throws<InvalidOperationException>(() => operations.StartSubscriptionReadyTasks(
            kernel, workspace, kernel.GetGoal(goal.Id), agents, profiles,
            checkpointBeforeWorkerStart: (_, _, _, phase) =>
            {
                if (phase == DispatchRecordCheckpointPhase.BeforeProcessStart) processStartCheckpoints++;
            },
            runner: new BackgroundDispatchRunner(disableProcessStart: true),
            sandboxOptions: sandbox,
            commandExists: DispatcherProviderProbeFakes.ProviderCommandsPresent,
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli));

        Xunit.Assert.Contains("Background dispatch process start is disabled", startRefusal.Message);
        Xunit.Assert.True(processStartCheckpoints > 0);
        var creditedTask = kernel.GetTask(goal.Id, planner.Id);
        Xunit.Assert.Contains(creditedTask.RetryAdmissionHistory, receipt =>
            receipt.Decision == RetryAdmissionDecision.Allowed &&
            receipt.Fingerprint == fingerprint &&
            receipt.LinkedDispatchAt == creditedTask.LastDispatch?.DispatchedAt);
        Xunit.Assert.Equal(new HumanInputRequestCounts(1, 0),
            kernel.GetHumanInputRequestCounts(goal.Id, planner.Id));
        Xunit.Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        Xunit.Assert.Equal(1, kernel.GetGoal(goal.Id).Timeline.Count(item =>
            item.Kind == ProgressKind.NoProgressRedispatchPrevented && item.TaskId == planner.Id));
    }
}
