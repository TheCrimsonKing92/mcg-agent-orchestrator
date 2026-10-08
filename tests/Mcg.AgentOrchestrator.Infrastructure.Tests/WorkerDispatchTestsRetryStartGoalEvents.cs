using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private workspace and an explicitly owned, stdin-gated process.
public sealed class WorkerDispatchTestsRetryStartGoalEvents : WorkerDispatchTestSupport
{
    private static readonly WorkerSandboxOptions DisabledSandbox = new(
        Enabled: false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    [Fact]
    public void PaidRetry_DurableStartClaim_RecordsOneDispatchInStateAndLog()
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        WriteSkill(workingDirectory, "orchestrator-dogfood");
        var workspace = OrchestratorWorkspace.ForDirectory(root, workingDirectory);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        var firstAt = DateTimeOffset.Parse("2026-07-07T12:00:00Z");
        var clock = new MutableClock(firstAt);
        var kernel = new AgentOrchestratorKernel(clock);
        var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, kernel: kernel);
        kernel.SetEventWriter(writer);
        var planner = new TaskSpec(TaskId.New(), "Plan a retry with logged dispatch evidence.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Start a paid retry and mirror its dispatch once", [planner]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Start a paid retry and mirror its dispatch once",
            ["One dispatch record reaches both stores."], VerificationClass.TestVerifiable, [], []));
        var agents = new[] { SubscriptionPlannerAgent("planner", "Planner") };
        var profiles = DispatchTestProfiles();
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RetryTask(goal.Id, planner.Id, "First paid retry context.", retryCause: RetryCause.ProviderInterruption);
        var first = WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
            kernel, goal, agents, profiles, workspace.PromptDirectory, workingDirectory,
            firstAt, commandExists: _ => true,
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli);
        var firstDispatch = Assert.Single(first.Dispatches).Task.LastDispatch!;
        Assert.Equal(RetryAdmissionDecision.Allowed, kernel.RecordPreparedRetryAdmission(
            goal.Id, planner.Id, Assert.IsType<RetryContextFingerprint>(firstDispatch.RetryContextFingerprint),
            PaidRouteClassification.Paid, firstAt).Decision);
        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Failed, "First retry failed.");
        clock.Advance();
        kernel.RetryTask(goal.Id, planner.Id, "Changed actionable retry context.", retryCause: RetryCause.ContractClarification);
        repository.SaveAsync(kernel).GetAwaiter().GetResult();
        var storedBefore = kernel.GetGoal(goal.Id).Timeline.Count(entry =>
            entry.Kind == ProgressKind.TaskDispatchRecorded && entry.TaskId == planner.Id);
        var loggedBefore = ReadDispatches(writer, goal.Id, planner.Id).Length;
        Assert.Equal(1, storedBefore);
        Assert.Equal(storedBefore, loggedBefore);

        Process? spawned = null;
        Task<string>? stdoutDrain = null;
        Task<string>? stderrDrain = null;
        var checkpoints = new List<DispatchRecordCheckpointPhase>();
        var runner = new BackgroundDispatchRunner(
            disableProcessStart: false,
            startProcess: startInfo =>
            {
                Assert.Null(spawned);
                var gatedStart = new ProcessStartInfo
                {
                    FileName = WorkerShell.Executable,
                    WorkingDirectory = startInfo.WorkingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                // Only OS startup inputs are inherited; provider and conductor switches cannot affect the fixture.
                gatedStart.Environment.Clear();
                foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP" })
                    if (Environment.GetEnvironmentVariable(name) is { } value)
                        gatedStart.Environment[name] = value;
                foreach (var argument in WorkerShell.BaseArguments())
                    gatedStart.ArgumentList.Add(argument);
                gatedStart.ArgumentList.Add("$null = [Console]::ReadLine()");
                spawned = Process.Start(gatedStart) ?? throw new InvalidOperationException("Fixture process did not start.");
                stdoutDrain = spawned.StandardOutput.ReadToEndAsync();
                stderrDrain = spawned.StandardError.ReadToEndAsync();
                Assert.False(spawned.HasExited);
                return spawned;
            });

        try
        {
            var result = new GoalDispatchOperations().StartSubscriptionReadyTasks(
                kernel, workspace, goal, agents, profiles,
                checkpointBeforeWorkerStart: (checkpointKernel, _, _, phase) =>
                {
                    checkpoints.Add(phase);
                    repository.SaveAsync(checkpointKernel).GetAwaiter().GetResult();
                },
                runner: runner, sandboxOptions: DisabledSandbox, plannerSampleCount: 1);

            Assert.Single(result.Dispatches);
            Assert.Single(result.Processes.Tasks);
            Assert.NotNull(spawned);
            Assert.Contains(DispatchRecordCheckpointPhase.BeforeRetryAdmission, checkpoints);
            Assert.Contains(DispatchRecordCheckpointPhase.ProcessMayHaveStarted, checkpoints);
            var stored = repository.LoadAsync().GetAwaiter().GetResult().GetGoal(goal.Id);
            var task = stored.Tasks.Single(item => item.Id == planner.Id);
            Assert.Equal(PaidRouteClassification.Paid, task.LastDispatch!.PaidRoute);
            var receipt = Assert.Single(task.RetryAdmissionHistory, item =>
                item.LinkedDispatchAt == task.LastDispatch.DispatchedAt);
            Assert.NotNull(receipt.WorkerStartClaimedAt);
            Assert.NotNull(receipt.WorkerStartedAt);
            var dispatches = stored.Timeline.Where(entry =>
                entry.Kind == ProgressKind.TaskDispatchRecorded && entry.TaskId == planner.Id).ToArray();
            var logged = ReadDispatches(writer, goal.Id, planner.Id);
            Assert.Equal(1, dispatches.Length - storedBefore);
            Assert.Equal(1, logged.Length - loggedBefore);
            foreach (var dispatch in dispatches)
                Assert.Single(logged, entry => entry == StateLogEntry.FromProgressEvent(dispatch));
        }
        finally
        {
            if (spawned is not null)
            {
                try
                {
                    if (!spawned.HasExited)
                        spawned.StandardInput.WriteLine("release");
                    Assert.True(spawned.WaitForExit(60_000), "Fixture process did not exit after the stdin release signal.");
                    Assert.Equal(0, spawned.ExitCode);
                    Assert.Equal(string.Empty, stderrDrain!.GetAwaiter().GetResult());
                    _ = stdoutDrain!.GetAwaiter().GetResult();
                }
                finally
                {
                    WorkerProcessJobs.Release(spawned.Id);
                    if (!spawned.HasExited)
                        spawned.Kill(entireProcessTree: true);
                    spawned.Dispose();
                }
            }
        }
    }

    [Fact]
    public void UnchangedPaidRetry_DeniedReservation_MirrorsPreventionOnce()
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        WriteSkill(workingDirectory, "orchestrator-dogfood");
        var workspace = OrchestratorWorkspace.ForDirectory(root, workingDirectory);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        var firstAt = DateTimeOffset.Parse("2026-07-07T12:00:00Z");
        var clock = new MutableClock(firstAt);
        var kernel = new AgentOrchestratorKernel(clock);
        var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, kernel: kernel);
        kernel.SetEventWriter(writer);
        var planner = new TaskSpec(TaskId.New(), "Plan a retry with prevention evidence.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Mirror a denied paid retry", [planner]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Mirror a denied paid retry", ["The prevention event is logged once."],
            VerificationClass.TestVerifiable, [], []));
        var agents = new[] { SubscriptionPlannerAgent("planner", "Planner") };
        var profiles = DispatchTestProfiles();
        kernel.ActivateGoal(goal.Id, agents);
        const string retryMessage = "Retry after the unsuccessful paid attempt.";
        kernel.RetryTask(goal.Id, planner.Id, retryMessage, retryCause: RetryCause.ProviderInterruption);
        var first = WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
            kernel, goal, agents, profiles, workspace.PromptDirectory, workingDirectory,
            firstAt, commandExists: _ => true,
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli);
        var dispatch = Assert.Single(first.Dispatches).Task.LastDispatch!;
        Assert.Equal(RetryAdmissionDecision.Allowed, kernel.RecordPreparedRetryAdmission(
            goal.Id, planner.Id, Assert.IsType<RetryContextFingerprint>(dispatch.RetryContextFingerprint),
            PaidRouteClassification.Paid, firstAt).Decision);
        kernel.RecordTaskProcessStarted(goal.Id, planner.Id, new TaskProcessRecord(
            4101, dispatch.Command, workingDirectory, Path.Combine(root, "first.out.log"),
            Path.Combine(root, "first.err.log"), Path.Combine(root, "first.exit"),
            firstAt, firstAt.AddSeconds(1), 1));
        kernel.RecordTaskVerification(goal.Id, planner.Id, new TaskVerificationRecord(
            "worker", workingDirectory, 1, "", "The first paid attempt did not complete.",
            firstAt, DispatchStartedAt: firstAt));
        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Failed, "The first paid attempt did not complete.");
        clock.Advance();
        kernel.RetryTask(goal.Id, planner.Id, retryMessage, retryCause: RetryCause.ProviderInterruption);
        repository.SaveAsync(kernel).GetAwaiter().GetResult();

        var result = new GoalDispatchOperations().StartSubscriptionReadyTasks(
            kernel, workspace, goal, agents, profiles,
            checkpointBeforeWorkerStart: (checkpointKernel, _, _, _) =>
                repository.SaveAsync(checkpointKernel).GetAwaiter().GetResult(),
            runner: new BackgroundDispatchRunner(disableProcessStart: true,
                startProcess: _ => throw new InvalidOperationException("Denied retry must not spawn a process.")),
            sandboxOptions: DisabledSandbox);

        Assert.Empty(result.Processes.Tasks);
        var stored = repository.LoadAsync().GetAwaiter().GetResult().GetGoal(goal.Id);
        var heldTask = stored.Tasks.Single(task => task.Id == planner.Id);
        var prevention = Assert.Single(heldTask.RetryAdmissionHistory, receipt => receipt.Decision == RetryAdmissionDecision.Prevented);
        Assert.Equal(RetryCause.UnchangedContextRepeat, prevention.Cause);
        Assert.Null(heldTask.LastProcess);
        var entry = Assert.Single(stored.Timeline, item =>
            item.Kind == ProgressKind.NoProgressRedispatchPrevented && item.TaskId == planner.Id);
        var expected = StateLogEntry.FromProgressEvent(entry);
        var logged = File.ReadAllLines(writer.EventFilePath(goal.Id)).Select(StateLogDivergenceComparer.ParseLine)
            .Where(line => line is not null).Select(line => line!.Entry).ToArray();
        Assert.Single(logged, item => item == expected);
    }

    private static StateLogEntry[] ReadDispatches(GoalLifecycleEventWriter writer, GoalId goalId, TaskId taskId) =>
        File.ReadAllLines(writer.EventFilePath(goalId)).Select(StateLogDivergenceComparer.ParseLine)
            .Where(line => line?.Entry.Kind == nameof(ProgressKind.TaskDispatchRecorded) && line.Entry.TaskId == taskId.Value)
            .Select(line => line!.Entry).ToArray();
}
