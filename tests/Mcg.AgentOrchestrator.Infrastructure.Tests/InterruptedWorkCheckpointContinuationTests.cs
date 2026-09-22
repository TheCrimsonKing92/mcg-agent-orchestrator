using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class InterruptedWorkCheckpointContinuationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T12:00:00Z");

    [Xunit.Fact]
    public void HeldCheckpointDispositionReportsPreservedWorkAndTheTypedHold()
    {
        const string holdToken = "checkpoint-hold-untyped-cause";
        var disposition = DispatchAutoRequeueDisposition.FromInterruptedWorkCheckpoint(
            new InterruptedWorkCheckpointDisposition(
                InterruptedWorkCheckpointDispositionKind.Hold,
                holdToken,
                holdToken),
            new DispatchRecoveryDecision(
                DispatchRecoveryAction.PreserveInterruptedWork,
                "preserve-interrupted-work",
                "worker.exit.txt",
                "provider interruption"));

        Xunit.Assert.NotNull(disposition);
        Xunit.Assert.Equal("InterruptedDispatchWorkPreserved", disposition.EventName);
        Xunit.Assert.Contains(holdToken, disposition.Message, StringComparison.Ordinal);
        Xunit.Assert.False(disposition.ShouldRequeue);
        Xunit.Assert.Null(disposition.Checkpoint);
    }

    [Xunit.Fact]
    public void ProjectorProducesDistinctDeveloperOnlyEarlyConvergenceReceipt()
    {
        var kernel = new AgentOrchestratorKernel(new TestClock(Now));
        var goal = kernel.CreateGoal("Resume implementation", [new TaskSpec(TaskId.New(), "Resume implementation", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "codex exec",
                "C:\\repo",
                Now.AddMinutes(-1),
                BaseCommit: new string('a', 40)));
        var checkpoint = CreateCheckpoint(task.Id, goal.Id);
        kernel.RequeueInterruptedDispatch(
            goal.Id,
            task.Id,
            "checkpoint continuation",
            RetryCause.ProviderInterruption,
            checkpoint.DispatchId,
            checkpoint);

        var projection = Xunit.Assert.IsType<InterruptedWorkCheckpointContextProjection>(
            InterruptedWorkCheckpointContextProjector.Project(task));
        var artifact = WorkerContextArtifact.Create(
            new LogicalArtifactIdentity(projection.LogicalIdentity),
            ContextArtifactKind.RegisteredContext,
            projection.Bytes,
            [AgentRole.Developer],
            ContextDeliveryMode.OnDemandFile,
            ContextContractVersion.V1,
            ".orchestrator-context/checkpoint.json");
        var receipt = WorkerContextPackageBuilder.CreateReceipt(new WorkerContextPackage(
            "ctxpkg-checkpoint",
            ContextContractVersion.V1,
            AgentRole.Developer,
            [artifact],
            InterruptedWorkCheckpointProjection: projection.Metrics));

        Xunit.Assert.Equal(EarlyConvergenceEvidenceKind.InterruptedWorkCheckpoint, receipt.EarlyConvergenceEvidenceSource);
        Xunit.Assert.True(receipt.HasEarlyConvergenceEvidenceFor(checkpoint.CheckpointSha));
        Xunit.Assert.False(receipt.HasEarlyConvergenceEvidenceFor(checkpoint.ParentCommit));
        Xunit.Assert.Contains(checkpoint.CheckpointSha, System.Text.Encoding.UTF8.GetString(projection.Bytes), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void CheckpointDispositionRequeuesSameTaskAndPersistsContinuationWithoutSuccess()
    {
        var clock = new TestClock(Now);
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Resume interrupted Developer", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        var dispatch = new TaskDispatchRecord(
            "codex-cli",
            "codex exec",
            "C:\\repo",
            Now.AddMinutes(-1),
            BaseCommit: new string('a', 40));
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
        var process = new TaskProcessRecord(
            12345,
            dispatch.Command,
            dispatch.WorkingDirectory,
            "out.log",
            "err.log",
            "exit.txt",
            dispatch.DispatchedAt,
            Now,
            1);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process with { CompletedAt = null, ExitCode = null });
        var checkpoint = CreateCheckpoint(task.Id, goal.Id);
        var verification = new TaskVerificationRecord(
            dispatch.Command,
            dispatch.WorkingDirectory,
            1,
            string.Empty,
            "typed provider interruption",
            Now,
            ProviderFailureKind: ProviderFailureKind.Connectivity);
        var outcome = new DispatchRefreshOutcome(
            process,
            verification,
            RecoveryDecision: new DispatchRecoveryDecision(
                DispatchRecoveryAction.PreserveInterruptedWork,
                "preserve-interrupted-work",
                process.ExitCodePath,
                "provider interruption"),
            ProviderFailureKind: ProviderFailureKind.Connectivity,
            AutoRequeueDisposition: new DispatchAutoRequeueDisposition(
                "InterruptedDispatchWorkCheckpointed",
                "checkpoint continuation",
                Checkpoint: checkpoint,
                InterruptedDispatchId: checkpoint.DispatchId));

        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);

        Xunit.Assert.Equal(checkpoint, task.PendingInterruptedWorkCheckpoint);
        Xunit.Assert.Equal(checkpoint.DispatchId, task.InterruptedDispatchRecoveryId);
        Xunit.Assert.Equal(RetryCause.ProviderInterruption, task.PendingRetryCause);
        Xunit.Assert.NotNull(task.LatestRetryAt);
        Xunit.Assert.Null(task.LastDispatch);
        Xunit.Assert.NotEqual(WorkTaskStatus.Completed, task.Status);
    }

    [Xunit.Fact]
    public void CheckpointAutoRequeueDoesNotBypassExhaustedConnectivityBudget()
    {
        var kernel = new AgentOrchestratorKernel(new TestClock(Now));
        var goal = kernel.CreateGoal("Bound interrupted Developer recovery", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        var dispatch = new TaskDispatchRecord(
            "codex-cli",
            "codex exec",
            "C:\\repo",
            Now.AddMinutes(-1),
            BaseCommit: new string('a', 40));
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
        var process = new TaskProcessRecord(
            12345,
            dispatch.Command,
            dispatch.WorkingDirectory,
            "out.log",
            "err.log",
            "exit.txt",
            dispatch.DispatchedAt,
            Now,
            1);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process with { CompletedAt = null, ExitCode = null });
        var verification = new TaskVerificationRecord(
            dispatch.Command,
            dispatch.WorkingDirectory,
            1,
            string.Empty,
            "typed provider interruption",
            Now,
            ProviderFailureKind: ProviderFailureKind.Connectivity);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                verification with { CompletedAt = Now.AddMinutes(-(attempt + 1)) });
        }

        var checkpoint = CreateCheckpoint(task.Id, goal.Id);
        var outcome = new DispatchRefreshOutcome(
            process,
            verification,
            RecoveryDecision: new DispatchRecoveryDecision(
                DispatchRecoveryAction.PreserveInterruptedWork,
                "preserve-interrupted-work",
                process.ExitCodePath,
                "provider interruption"),
            ProviderFailureKind: ProviderFailureKind.Connectivity,
            AutoRequeueDisposition: new DispatchAutoRequeueDisposition(
                "InterruptedDispatchWorkCheckpointed",
                "checkpoint continuation",
                Checkpoint: checkpoint,
                InterruptedDispatchId: checkpoint.DispatchId));

        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.Null(task.PendingInterruptedWorkCheckpoint);
        Xunit.Assert.DoesNotContain(
            kernel.GetTimeline(goal.Id),
            evt => evt.Kind == ProgressKind.TaskRetried && evt.Message == outcome.AutoRequeueDisposition.Message);
        Xunit.Assert.Contains(
            kernel.GetTimeline(goal.Id),
            evt => evt.Message.Contains("checkpoint-retry-budget-exhausted", StringComparison.Ordinal));
    }

    [Xunit.Theory]
    [Xunit.InlineData("goal", "checkpoint-goal-mismatch")]
    [Xunit.InlineData("task", "checkpoint-task-mismatch")]
    [Xunit.InlineData("role", "checkpoint-role-mismatch")]
    [Xunit.InlineData("dispatch", "checkpoint-dispatch-mismatch")]
    [Xunit.InlineData("parent", "checkpoint-parent-mismatch")]
    [Xunit.InlineData("worktree", "checkpoint-worktree-mismatch")]
    [Xunit.InlineData("sha", "checkpoint-sha-missing")]
    [Xunit.InlineData("cause", "checkpoint-cause-mismatch")]
    public void RequeueInterruptedDispatchRejectsMismatchedCheckpoint(string scenario, string expectedToken)
    {
        var kernel = new AgentOrchestratorKernel(new TestClock(Now));
        var goal = kernel.CreateGoal("Resume interrupted Developer", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        var dispatch = new TaskDispatchRecord(
            "codex-cli",
            "codex exec",
            "C:\\repo",
            Now.AddMinutes(-1),
            BaseCommit: new string('a', 40));
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
        var checkpoint = CreateCheckpoint(task.Id, goal.Id) with
        {
            GoalId = scenario == "goal" ? GoalId.New().Value : goal.Id.Value,
            TaskId = scenario == "task" ? TaskId.New().Value : task.Id.Value,
            Role = scenario == "role" ? AgentRole.Tester : AgentRole.Developer,
            DispatchId = scenario == "dispatch" ? "different-attempt" : "dispatch-attempt",
            ParentCommit = scenario == "parent" ? new string('c', 40) : dispatch.BaseCommit!,
            WorktreePath = scenario == "worktree" ? "C:\\other" : dispatch.WorkingDirectory,
            CheckpointSha = scenario == "sha" ? string.Empty : new string('b', 40),
            Cause = scenario == "cause" ? ProviderFailureKind.Unknown : ProviderFailureKind.Connectivity
        };

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => kernel.RequeueInterruptedDispatch(
            goal.Id,
            task.Id,
            "checkpoint continuation",
            RetryCause.ProviderInterruption,
            "dispatch-attempt",
            checkpoint));

        Xunit.Assert.Contains(expectedToken, error.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(dispatch, task.LastDispatch);
        Xunit.Assert.Null(task.PendingInterruptedWorkCheckpoint);
    }

    [Xunit.Fact]
    public void RunnerCheckpointDrivesIdentityGuardsDuplicateSuppressionAndCooldown()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "mcg-checkpoint-runner-" + Guid.NewGuid().ToString("N"));
        Process? worker = null;
        try
        {
            Directory.CreateDirectory(repositoryRoot);
            Xunit.Assert.True(Git(repositoryRoot, "init").Succeeded);
            Xunit.Assert.True(Git(repositoryRoot, "config", "user.email", "checkpoint@example.test").Succeeded);
            Xunit.Assert.True(Git(repositoryRoot, "config", "user.name", "Checkpoint Test").Succeeded);
            File.WriteAllText(Path.Combine(repositoryRoot, "seed.txt"), "seed");
            Xunit.Assert.True(Git(repositoryRoot, "add", "seed.txt").Succeeded);
            Xunit.Assert.True(Git(repositoryRoot, "commit", "-m", "seed").Succeeded);

            var kernel = new AgentOrchestratorKernel(new TestClock(Now));
            var goal = kernel.CreateGoal("Resume interrupted Developer", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            var worktree = GoalWorktrees.Ensure(repositoryRoot, goal.Id);
            var baseCommit = Git(worktree, "rev-parse", "HEAD").Output.Trim();
            var sourcePath = Path.Combine(worktree, "src", "Feature.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, "implementation");

            worker = WorkerProcessJobs.StartRegisteredOrThrow(new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                UseShellExecute = false,
                CreateNoWindow = true
            }.WithArguments(WorkerShell.BaseArguments().Concat(["Start-Sleep -Seconds 9999"])),
                "interrupted-work-checkpoint-continuation-test");
            Xunit.Assert.True(SpawnProcessIdentityReader.TryReadForRegistration(worker, out var identity));
            var runnerNow = identity.StartedAt.AddMinutes(2);
            var dispatchedAt = identity.StartedAt.AddMinutes(-1);
            var receipt = WorkerContextPackageBuilder.CreateReceipt(new WorkerContextPackage(
                "ctxpkg-checkpoint-runner",
                ContextContractVersion.V1,
                AgentRole.Developer,
                []));
            var dispatch = new TaskDispatchRecord(
                "codex-cli",
                "codex exec prompt",
                worktree,
                dispatchedAt,
                WorkerProviderKind: ProviderKind.OpenAICodexCli,
                BaseCommit: baseCommit,
                ContextPackageReceipt: receipt);
            kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
            var artifacts = Path.Combine(worktree, ".scratch", "dispatch");
            Directory.CreateDirectory(artifacts);
            var process = new TaskProcessRecord(
                worker.Id,
                dispatch.Command,
                worktree,
                Path.Combine(artifacts, "out.log"),
                Path.Combine(artifacts, "err.log"),
                Path.Combine(artifacts, "worker.exit.txt"),
                identity.StartedAt,
                null,
                null,
                OwnedProcessIds: [worker.Id]);
            File.WriteAllText(process.StandardOutputPath, "partial worker output");
            File.WriteAllText(
                process.StandardErrorPath,
                "ERROR: Falling back from WebSockets to HTTPS transport failed. stream disconnected");
            WriteHeartbeat(process, identity, runnerNow);
            DispatchExitArtifacts.Write(
                process.ExitCodePath,
                DispatchExitArtifacts.Synthetic(1, "startup sweep interrupted worker", runnerNow));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
            worker.Kill(entireProcessTree: true);
            worker.WaitForExit();
            Xunit.Assert.True(WorkerProcessJobs.HasRegisteredJob(process.ProcessId));

            var runner = new BackgroundDispatchRunner(
                new TestClock(runnerNow),
                isStillRunning: _ => false,
                readProcessIdentity: _ => (identity.StartedAt.AddMinutes(1), identity.ImagePath));
            var outcome = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);

            Xunit.Assert.True(
                outcome.AutoRequeueDisposition?.EventName == "InterruptedDispatchWorkCheckpointed",
                $"{outcome.AutoRequeueDisposition?.EventName}: {outcome.AutoRequeueDisposition?.Message}");
            Xunit.Assert.NotNull(outcome.AutoRequeueDisposition?.Checkpoint);
            Xunit.Assert.Equal(dispatch.DispatchedAt, outcome.DispatchAttemptAt);
            Xunit.Assert.False(WorkerProcessJobs.HasRegisteredJob(process.ProcessId));
            var checkpoint = outcome.AutoRequeueDisposition!.Checkpoint!;
            foreach (var (candidate, expectedToken) in new[]
                     {
                         (checkpoint with { GoalId = GoalId.New().Value }, "checkpoint-goal-mismatch"),
                         (checkpoint with { TaskId = TaskId.New().Value }, "checkpoint-task-mismatch"),
                         (checkpoint with { DispatchId = "different-attempt" }, "checkpoint-dispatch-mismatch")
                     })
            {
                var error = Xunit.Assert.Throws<InvalidOperationException>(() => kernel.RequeueInterruptedDispatch(
                    goal.Id,
                    task.Id,
                    "reject cross-identity checkpoint",
                    RetryCause.ProviderInterruption,
                    checkpoint.DispatchId,
                    candidate));
                Xunit.Assert.Contains(expectedToken, error.Message, StringComparison.Ordinal);
                Xunit.Assert.Equal(dispatch, task.LastDispatch);
                Xunit.Assert.Null(task.PendingInterruptedWorkCheckpoint);
            }

            BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);
            var retryEventsAfterFirstApply = kernel.GetTimeline(goal.Id).Count(evt =>
                evt.Kind == ProgressKind.TaskRetried &&
                evt.Message == outcome.AutoRequeueDisposition.Message);
            BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);

            Xunit.Assert.True(retryEventsAfterFirstApply > 0);
            Xunit.Assert.Equal(
                retryEventsAfterFirstApply,
                kernel.GetTimeline(goal.Id).Count(evt =>
                    evt.Kind == ProgressKind.TaskRetried &&
                    evt.Message == outcome.AutoRequeueDisposition.Message));
            Xunit.Assert.NotNull(task.PendingInterruptedWorkCheckpoint);
            Xunit.Assert.True(DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, Now, out var retryAfter));
            Xunit.Assert.Equal(task.SubscriptionRetryAfter, retryAfter);
            Xunit.Assert.NotEqual(WorkTaskStatus.Completed, task.Status);
        }
        finally
        {
            if (worker is not null)
            {
                try { WorkerProcessJobs.Release(worker.Id); } catch { }
                try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); } catch { }
                worker.Dispose();
            }
            try { Directory.Delete(repositoryRoot, recursive: true); } catch { }
        }
    }

    private static void WriteHeartbeat(
        TaskProcessRecord process,
        SpawnProcessIdentity identity,
        DateTimeOffset observedAt)
    {
        var payload = new
        {
            pid = process.ProcessId,
            childPid = (int?)null,
            ownedPids = new[] { process.ProcessId },
            state = "exited",
            lastObservedAt = observedAt,
            lastProgressAt = observedAt,
            stdoutBytes = new FileInfo(process.StandardOutputPath).Length,
            stderrBytes = new FileInfo(process.StandardErrorPath).Length,
            ownedCpuMs = 100,
            ownedProcessIdentities = new[]
            {
                new { processId = identity.ProcessId, startedAt = identity.StartedAt, imagePath = identity.ImagePath }
            }
        };
        File.WriteAllText(BackgroundDispatchRunner.GetHeartbeatPath(process), JsonSerializer.Serialize(payload));
    }

    private static GitCli.GitResult Git(string root, params string[] args) => GitCli.Run(root, args);

    private static InterruptedWorkCheckpoint CreateCheckpoint(TaskId taskId, GoalId? goalId = null) =>
        InterruptedWorkCheckpoint.Create(
            "dispatch-attempt",
            (goalId ?? GoalId.New()).Value,
            taskId.Value,
            AgentRole.Developer,
            "goal/12345678",
            "C:\\repo",
            new string('a', 40),
            ProviderFailureKind.Connectivity,
            Now,
            new string('b', 40));

    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
