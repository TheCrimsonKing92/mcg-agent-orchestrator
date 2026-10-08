using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Collections.Concurrent;

public sealed class WorkerDispatchTestsDispatchPreparationHostCommandProbe : WorkerDispatchTestSupport
{
    private static readonly WorkerSandboxOptions DisabledSandbox = new(
        Enabled: false,
        WorkerSandboxOptions.DefaultAccount,
        WorkerSandboxOptions.DefaultCredentialTarget);

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void IdenticalPaidRetry_SameLauncherProbe_PreventsProcessStart(bool claudePresent)
    {
        Func<string, bool> commandExists = name =>
            claudePresent || !name.Equals("claude", StringComparison.OrdinalIgnoreCase);

        AssertIdenticalPaidRetryPrevented(commandExists, commandExists);
    }

    [Xunit.Fact]
    public void IdenticalPaidRetry_RecordingStartProbe_ObservesLauncherEvaluation()
    {
        var recordedNames = new ConcurrentQueue<string>();

        AssertIdenticalPaidRetryPrevented(_ => true, name =>
        {
            recordedNames.Enqueue(name);
            return true;
        });

        Assert.NotEmpty(recordedNames);
        Assert.Contains(recordedNames, name => name.Equals("claude", StringComparison.OrdinalIgnoreCase));
        Assert.All(recordedNames, name => Assert.True(
            name.Equals("codex", StringComparison.OrdinalIgnoreCase)
            || name.Equals("claude", StringComparison.OrdinalIgnoreCase),
            $"Unexpected launcher probe: {name}"));
    }

    private static void AssertIdenticalPaidRetryPrevented(
        Func<string, bool> preparationProbe,
        Func<string, bool> startProbe)
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        var workspace = OrchestratorWorkspace.ForDirectory(root, workingDirectory);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        WriteSkill(workingDirectory, "orchestrator-dogfood");
        var firstAt = DateTimeOffset.Parse("2026-07-07T12:00:00Z");
        var kernel = new AgentOrchestratorKernel(new TestClock(firstAt));
        var planner = new TaskSpec(TaskId.New(), "Plan the identical retry guard.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Prevent identical paid retry", [planner]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Prevent identical paid retry",
            ["An unchanged paid retry starts no process."],
            VerificationClass.TestVerifiable,
            [],
            []));
        var agents = new[] { SubscriptionPlannerAgent("planner", "Planner") };
        var profiles = DispatchTestProfiles();
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RetryTask(
            goal.Id,
            planner.Id,
            "Retry after the unsuccessful paid attempt.",
            retryCause: RetryCause.ProviderInterruption);

        var first = WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
            kernel,
            goal,
            agents,
            profiles,
            workspace.PromptDirectory,
            workingDirectory,
            firstAt,
            commandExists: preparationProbe,
            sandboxOptions: DisabledSandbox,
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli);
        var firstDispatch = Assert.Single(first.Dispatches);
        var firstFingerprint = Assert.IsType<RetryContextFingerprint>(firstDispatch.Task.LastDispatch!.RetryContextFingerprint);
        var firstAdmission = kernel.RecordPreparedRetryAdmission(
            goal.Id,
            planner.Id,
            firstFingerprint,
            PaidRouteClassification.Paid,
            firstAt);
        Assert.Equal(RetryAdmissionDecision.Allowed, firstAdmission.Decision);
        var recordedFirstDispatch = Assert.IsType<TaskDispatchRecord>(planner.LastDispatch);
        kernel.RecordTaskProcessStarted(
            goal.Id,
            planner.Id,
            new TaskProcessRecord(
                4101,
                recordedFirstDispatch.Command,
                recordedFirstDispatch.WorkingDirectory,
                Path.Combine(root, "first.out.log"),
                Path.Combine(root, "first.err.log"),
                Path.Combine(root, "first.exit"),
                firstAt,
                firstAt.AddSeconds(1),
                1));
        var startedAdmission = Assert.Single(
            planner.RetryAdmissionHistory,
            receipt => receipt.Decision == RetryAdmissionDecision.Allowed);
        Assert.Equal(firstAt, startedAdmission.WorkerStartedAt);
        kernel.RecordTaskVerification(
            goal.Id,
            planner.Id,
            new TaskVerificationRecord(
                "worker",
                workingDirectory,
                1,
                "",
                "The first paid attempt did not complete.",
                firstAt,
                DispatchStartedAt: firstAt));
        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Failed, "The first paid attempt did not complete.");
        kernel.RetryTask(
            goal.Id,
            planner.Id,
            "Retry after the unsuccessful paid attempt.",
            retryCause: RetryCause.ProviderInterruption);
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
        var checkpointCalls = 0;

        var result = new GoalDispatchOperations().StartSubscriptionReadyTasks(
            kernel,
            workspace,
            goal,
            agents,
            profiles,
            checkpointBeforeWorkerStart: (_, _, _, _) => checkpointCalls++,
            runner: new BackgroundDispatchRunner(disableProcessStart: true),
            sandboxOptions: DisabledSandbox,
            commandExists: startProbe,
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli);

        Assert.Empty(result.Processes.Tasks);
        Assert.Equal(1, checkpointCalls);
        var persistedGoal = kernel.GetGoal(goal.Id);
        var persistedPlanner = persistedGoal.Tasks.Single(candidate => candidate.Id == planner.Id);
        Assert.Null(persistedPlanner.LastProcess);
        var prevention = Assert.Single(
            persistedPlanner.RetryAdmissionHistory,
            receipt => receipt.Decision == RetryAdmissionDecision.Prevented);
        Assert.Equal(RetryCause.UnchangedContextRepeat, prevention.Cause);
        Assert.Equal(RetryAdmissionRoute.EnvironmentalHold, prevention.Route);
        Assert.Equal(firstAt, prevention.PriorAttemptAt);
        Assert.Contains(
            persistedGoal.Timeline,
            item => item.Kind == ProgressKind.NoProgressRedispatchPrevented && item.TaskId == planner.Id);
    }
}
