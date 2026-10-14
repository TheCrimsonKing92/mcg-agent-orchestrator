using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: disposable repositories/stores, identity-bound children and explicit readiness gates.
public sealed class CompletionBuildEvidenceRecoveryTests
{
    [Theory]
    [InlineData(CompletionBuildEvidencePhase.Success)]
    [InlineData(CompletionBuildEvidencePhase.CompilerFailure)]
    public void LateTerminalResultAfterResetAndRestartPreservesFactWithoutCompletingTask(CompletionBuildEvidencePhase phase)
    {
        var f = Start();
        var operation = f.Task.CompletionBuildEvidence!;
        f.Release(phase, receipt: false);
        var owner = ParseOwner(operation);
        if (phase == CompletionBuildEvidencePhase.Success)
            GoalOperationJournal.Completed(f.Root, f.Goal, owner.Operation, "original completed fact", operationInstanceId: owner.InstanceId);
        else
            GoalOperationJournal.Failed(f.Root, f.Goal, owner.Operation, "original failed fact", operationInstanceId: owner.InstanceId);
        var originalReceipt = File.ReadAllText(operation.ResultPath);
        f.ResetWorkerMetadata();
        f.Clock.Advance(TimeSpan.FromMinutes(20));
        f.Reload();
        var state = f.Task.Status;

        var result = f.Runner().ReconcileCompletionBuildEvidence(f.Kernel, f.GoalId, f.TaskId, CompletionBuildEvidenceTarget.From(operation));

        Assert.Equal(phase, result.Phase);
        Assert.True(result.CustodyReleased);
        Assert.False(result.Applied);
        Assert.Equal(state, f.Task.Status);
        Assert.Null(f.Task.LastProcess);
        Assert.Null(f.Task.LastDispatch);
        Assert.Empty(f.Task.VerificationHistory);
        Assert.Null(f.Task.DispatchHistory.Single().ResultCommit);
        Assert.Equal(originalReceipt, File.ReadAllText(operation.ResultPath));
        Assert.Null(Lease(f));
        Assert.DoesNotContain(Journal(f, operation), e => e.Status == GoalOperationStatus.Aborted);
        Assert.Equal(GoalStatus.Active, f.Goal.Status);
        f.Reload();
        var repeated = f.Runner().ReconcileCompletionBuildEvidence(f.Kernel, f.GoalId, f.TaskId, CompletionBuildEvidenceTarget.From(operation));
        Assert.Equal(result.Phase, repeated.Phase);
        Assert.True(repeated.CustodyReleased);
        Assert.Equal(1, f.Launches);
        Assert.Equal(0, f.Stops);
        Assert.Single(Journal(f, operation).Where(e => e.Status is GoalOperationStatus.Completed or GoalOperationStatus.Failed));
    }

    [Fact]
    public void ResetCancellationPersistsExactDispositionAndIsIdempotentAfterRestart()
    {
        var f = Start();
        var operation = f.Task.CompletionBuildEvidence!;
        f.ResetWorkerMetadata();
        f.Reload();
        var target = CompletionBuildEvidenceTarget.From(operation);
        var result = f.Runner().CancelCompletionBuildEvidence(f.Kernel, f.GoalId, f.TaskId, target);
        Assert.Equal(CompletionBuildEvidencePhase.Cancelled, result.Phase);
        Assert.True(result.CustodyReleased);
        Assert.Null(Lease(f));
        Assert.Null(f.Task.LastProcess);
        Assert.Null(f.Task.LastVerification);
        f.Reload();
        f.Runner().CancelCompletionBuildEvidence(f.Kernel, f.GoalId, f.TaskId, target);
        Assert.Equal(1, f.Stops);
        Assert.Equal(1, f.Launches);
        Assert.Single(Journal(f, operation).Where(e => e.Status == GoalOperationStatus.Aborted));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownLivenessOrUnconfirmedTerminationRetainsPendingOperationAndLease(bool unknown)
    {
        var f = Start();
        var operation = f.Task.CompletionBuildEvidence!;
        f.ResetWorkerMetadata();
        f.Unknown = unknown;
        f.FailStop = !unknown;
        if (unknown)
            Assert.True(f.Runner().CancelCompletionBuildEvidence(f.Kernel, f.GoalId, f.TaskId,
                CompletionBuildEvidenceTarget.From(operation)).IsPending);
        else
            Assert.Contains("termination unconfirmed", Assert.Throws<InvalidOperationException>(() =>
                f.Runner().CancelCompletionBuildEvidence(f.Kernel, f.GoalId, f.TaskId,
                    CompletionBuildEvidenceTarget.From(operation))).Message);
        f.Reload();
        Assert.True(f.Task.CompletionBuildEvidence!.IsPending);
        Assert.False(f.Task.CompletionBuildEvidence.CustodyReleased);
        Assert.Equal(operation.LeaseOwner, Lease(f));
        Assert.DoesNotContain(Journal(f, operation), e => e.Status == GoalOperationStatus.Aborted);
        Assert.Null(f.Task.LastProcess);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("dispatch")]
    [InlineData("tree")]
    [InlineData("result")]
    [InlineData("child")]
    [InlineData("lease")]
    public void StaleSelectorCannotTerminateOrReleaseAttachedOperation(string mismatch)
    {
        var f = Start();
        var operation = f.Task.CompletionBuildEvidence!;
        f.ResetWorkerMetadata();
        var target = CompletionBuildEvidenceTarget.From(operation);
        target = mismatch switch
        {
            "operation" => target with { OperationId = "foreign" },
            "dispatch" => target with { DispatchId = "foreign" },
            "tree" => target with { TreeDigest = "foreign" },
            "result" => target with { ResultPath = "foreign" },
            "child" => target with { ChildStartedAt = operation.ChildStartedAt!.Value.AddSeconds(1) },
            _ => target with { LeaseOwner = "foreign" }
        };
        Assert.Throws<InvalidOperationException>(() => f.Runner().CancelCompletionBuildEvidence(f.Kernel, f.GoalId, f.TaskId, target));
        Assert.Equal(0, f.Stops);
        Assert.Equal(operation.LeaseOwner, Lease(f));
        Assert.True(f.Task.CompletionBuildEvidence!.IsPending);
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("task")]
    [InlineData("dispatch")]
    [InlineData("result")]
    [InlineData("worker")]
    [InlineData("lease")]
    public void ForeignAttachedOriginFailsClosedBeforeAnyChildOrLeaseAction(string mismatch)
    {
        var f = Start();
        var operation = f.Task.CompletionBuildEvidence!;
        f.ResetWorkerMetadata();
        var foreign = mismatch switch
        {
            "goal" => operation with { GoalId = GoalId.New().Value },
            "task" => operation with { TaskId = TaskId.New().Value },
            "dispatch" => operation with { DispatchId = "foreign" },
            "result" => operation with { ResultPath = Path.Combine(f.Root, "foreign-result.json") },
            "worker" => operation with { WorkerProcess = operation.WorkerProcess with { Command = "foreign" } },
            _ => operation with { LeaseOwner = "foreign" }
        };
        var snapshot = f.Kernel.ExportSnapshot();
        var goal = snapshot.Goals.Single();
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [goal with
        { Tasks = goal.Tasks.Select(t => t.Id == f.TaskId.Value ? t with { CompletionBuildEvidence = foreign } : t).ToArray() }] });
        Assert.Throws<InvalidOperationException>(() => f.Runner().CancelCompletionBuildEvidence(f.Kernel, f.GoalId, f.TaskId,
            CompletionBuildEvidenceTarget.From(foreign)));
        Assert.Equal(0, f.Stops);
        Assert.Equal(operation.LeaseOwner, Lease(f));
        Assert.DoesNotContain(Journal(f, operation), e => e.Status == GoalOperationStatus.Aborted);
    }

    [Fact]
    public void TerminalDispositionPersistedBeforeLeaseFailureCanFinishAfterRestart()
    {
        var f = Start();
        var operation = f.Task.CompletionBuildEvidence!;
        f.Release(receipt: false);
        var owner = ParseOwner(operation);
        GoalOperationJournal.Completed(f.Root, f.Goal, owner.Operation, "original completed fact", operationInstanceId: owner.InstanceId);
        f.ResetWorkerMetadata();
        var coordinator = new CompletionBuildEvidenceCoordinator(f.StatePath, f.Root,
            utcNow: () => f.Clock.UtcNow, probe: _ => CompletionBuildChildLiveness.Dead,
            finish: (_, _) => throw new IOException("controlled lease-release failure"));
        Assert.Throws<IOException>(() => coordinator.ReconcileAttached(f.Kernel, f.GoalId, f.TaskId, CompletionBuildEvidenceTarget.From(operation)));
        f.Reload();
        Assert.Equal(CompletionBuildEvidencePhase.Success, f.Task.CompletionBuildEvidence!.Phase);
        Assert.False(f.Task.CompletionBuildEvidence.CustodyReleased);
        Assert.Equal(operation.LeaseOwner, Lease(f));
        var nextDispatch = f.Task.DispatchHistory.Single() with { DispatchedAt = operation.DispatchAt.AddMinutes(1) };
        Assert.Contains("custody must be released", Assert.Throws<InvalidOperationException>(() =>
            f.Kernel.RecordTaskDispatch(f.GoalId, f.TaskId, nextDispatch)).Message);
        Assert.Null(f.Task.LastDispatch);
        Assert.Single(f.Task.DispatchHistory);
        new SqliteOrchestratorStateRepository(f.StatePath).SaveAsync(f.Kernel).GetAwaiter().GetResult();
        f.Reload();
        Assert.Contains("custody must be released", Assert.Throws<InvalidOperationException>(() =>
            f.Kernel.RecordTaskDispatch(f.GoalId, f.TaskId, nextDispatch)).Message);
        Assert.Equal(operation.Id, f.Task.CompletionBuildEvidence!.Id);
        Assert.Equal(operation.LeaseOwner, Lease(f));
        var terminal = f.Runner().ReconcileCompletionBuildEvidence(f.Kernel, f.GoalId, f.TaskId, CompletionBuildEvidenceTarget.From(operation));
        Assert.Equal(CompletionBuildEvidencePhase.Success, terminal.Phase);
        Assert.True(terminal.CustodyReleased);
        Assert.Null(Lease(f));
        Assert.Null(f.Task.LastProcess);
        f.Kernel.RecordTaskDispatch(f.GoalId, f.TaskId, nextDispatch);
        new SqliteOrchestratorStateRepository(f.StatePath).SaveAsync(f.Kernel).GetAwaiter().GetResult();
        f.Reload();
        Assert.Null(f.Task.CompletionBuildEvidence);
        Assert.Equal(2, f.Task.DispatchHistory.Count);
        Assert.Equal(nextDispatch.DispatchedAt, f.Task.LastDispatch!.DispatchedAt);
        Assert.True(File.Exists(operation.ResultPath));
        Assert.Single(Journal(f, operation).Where(e => e.Status == GoalOperationStatus.Completed));
    }

    [Fact]
    public void ConductorDiscoveryReachesResetOperationThroughLifecycleOnlyRouting()
    {
        var f = Start();
        f.Release(receipt: false);
        f.ResetWorkerMetadata();
        var calls = 0;
        ExitedDispatchReconciliationExecutor.Execute(f.Goal, (goal, taskId) =>
        {
            calls++;
            var operation = f.Kernel.GetTask(goal.Id, taskId).CompletionBuildEvidence!;
            return !f.Runner().ReconcileCompletionBuildEvidence(f.Kernel, goal.Id, taskId,
                CompletionBuildEvidenceTarget.From(operation)).IsPending;
        }, _ => f.Goal);
        Assert.Equal(1, calls);
        Assert.Equal(CompletionBuildEvidencePhase.Success, f.Task.CompletionBuildEvidence!.Phase);
        Assert.Null(f.Task.LastProcess);
        Assert.Null(f.Task.LastVerification);
    }

    [Fact]
    public void SupersededOperationCannotOverwriteNewerWorkerDispatchOrVerification()
    {
        var f = Start();
        var operation = f.Task.CompletionBuildEvidence!;
        f.Release(receipt: false);
        f.ResetWorkerMetadata();
        var snapshot = f.Kernel.ExportSnapshot();
        var goal = snapshot.Goals.Single();
        var task = goal.Tasks.Single(t => t.Id == f.TaskId.Value);
        var original = task.DispatchHistory!.Single();
        var newer = original with { DispatchedAt = operation.DispatchAt.AddMinutes(30), ResultCommit = "newer-result" };
        var worker = new TaskProcessSnapshot(789, newer.Command, newer.WorkingDirectory, "new-output", "new-error", "new-exit",
            newer.DispatchedAt, newer.DispatchedAt.AddMinutes(1), 1);
        var verification = new TaskVerificationSnapshot(newer.Command, newer.WorkingDirectory, 1, "new evidence", "new failure",
            newer.DispatchedAt.AddMinutes(1));
        var changed = task with { LastDispatch = newer, LastProcess = worker, DispatchHistory = [original, newer],
            VerificationHistory = [verification], Status = WorkTaskStatus.Failed };
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [goal with
        { Tasks = goal.Tasks.Select(t => t.Id == changed.Id ? changed : t).ToArray() }] });
        new SqliteOrchestratorStateRepository(f.StatePath).SaveAsync(f.Kernel).GetAwaiter().GetResult();
        f.Reload();
        var currentProcess = f.Task.LastProcess;
        var currentDispatch = f.Task.LastDispatch;
        var currentVerification = f.Task.LastVerification;

        var result = f.Runner().ReconcileCompletionBuildEvidence(f.Kernel, f.GoalId, f.TaskId, CompletionBuildEvidenceTarget.From(operation));

        Assert.Equal(CompletionBuildEvidencePhase.Success, result.Phase);
        Assert.True(result.CustodyReleased);
        Assert.Same(currentProcess, f.Task.LastProcess);
        Assert.Same(currentDispatch, f.Task.LastDispatch);
        Assert.Same(currentVerification, f.Task.LastVerification);
        Assert.Single(f.Task.VerificationHistory);
        Assert.Equal("newer-result", f.Task.LastDispatch!.ResultCommit);
        Assert.Equal(WorkTaskStatus.Failed, f.Task.Status);
        Assert.Throws<InvalidOperationException>(() => f.Coordinator().Reconcile(f.Kernel, f.GoalId, f.TaskId,
            new(operation.WorktreeRoot, operation.Projects, TimeSpan.FromMinutes(8)), f.Task.LastProcess!));
        Assert.Equal(CompletionBuildEvidencePhase.Success, f.Task.CompletionBuildEvidence!.Phase);
        Assert.Same(currentVerification, f.Task.LastVerification);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProductionReconciliationAppliesNewerWorkerWithoutConsumingHistoricalBuild(bool currentReceipt)
    {
        var f = Start();
        var operation = f.Task.CompletionBuildEvidence!;
        f.Release(receipt: false);
        var owner = ParseOwner(operation);
        GoalOperationJournal.Completed(f.Root, f.Goal, owner.Operation, "original completed fact", operationInstanceId: owner.InstanceId);
        f.ResetWorkerMetadata();
        AttachNewerExitedWorker(f, operation);
        if (currentReceipt) f.WriteReceipt(operation.Projects);
        var currentProcess = f.Task.LastProcess!;
        var currentDispatch = f.Task.LastDispatch!;
        var originalResult = File.ReadAllText(operation.ResultPath);

        ExitedDispatchReconciliationExecutor.Execute(f.Goal,
            (goal, taskId) => ExitedDispatchReconciliationExecutor.ReconcileTask(f.Kernel, goal.Id, taskId, f.Runner()),
            _ => f.Goal);

        Assert.True(DispatchProcessCompletionState.HasAlreadyBeenApplied(f.Task, f.Task.LastProcess!));
        Assert.Equal(currentProcess.ProcessId, f.Task.LastProcess!.ProcessId);
        Assert.Equal(currentProcess.StartedAt, f.Task.LastProcess.StartedAt);
        Assert.Equal(currentDispatch.DispatchedAt, f.Task.LastDispatch!.DispatchedAt);
        Assert.Equal(currentReceipt, Assert.Single(f.Task.VerificationHistory).Succeeded);
        Assert.Equal(currentReceipt ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, f.Task.Status);
        Assert.Equal(CompletionBuildEvidencePhase.Success, f.Task.CompletionBuildEvidence!.Phase);
        Assert.True(f.Task.CompletionBuildEvidence.CustodyReleased);
        Assert.False(f.Task.CompletionBuildEvidence.Applied);
        Assert.Null(f.Task.DispatchHistory.Single(d => d.DispatchedAt == operation.DispatchAt).ResultCommit);
        if (currentReceipt) Assert.NotNull(f.Task.LastDispatch.ResultCommit);
        else
        {
            Assert.Null(f.Task.LastDispatch.ResultCommit);
            Assert.Contains("Historical completion build", f.Task.LastVerification!.StandardError);
        }
        Assert.Equal(originalResult, File.ReadAllText(operation.ResultPath));
        Assert.Single(Journal(f, operation).Where(e => e.Status == GoalOperationStatus.Completed));
        Assert.Null(Lease(f));
        Assert.Equal(1, f.Launches);
        Assert.Equal(GoalStatus.Active, f.Goal.Status);
        new SqliteOrchestratorStateRepository(f.StatePath).SaveAsync(f.Kernel).GetAwaiter().GetResult();
        f.Reload();
        Assert.False(f.Task.CompletionBuildEvidence!.Applied);
        var calls = 0;
        ExitedDispatchReconciliationExecutor.Execute(f.Goal, (_, _) => { calls++; return false; }, _ => f.Goal);
        Assert.Equal(0, calls);
        Assert.Single(f.Task.VerificationHistory);
    }

    [Fact]
    public void HistoricalUnknownChildHoldsNewerWorkerWithoutChangingItsEvidence()
    {
        var f = Start();
        var operation = f.Task.CompletionBuildEvidence!;
        f.ResetWorkerMetadata();
        AttachNewerExitedWorker(f, operation);
        f.Unknown = true;
        f.WriteReceipt(operation.Projects);

        Assert.False(ExitedDispatchReconciliationExecutor.ReconcileTask(f.Kernel, f.GoalId, f.TaskId, f.Runner()));
        Assert.Null(f.Runner().RefreshLatestProcessWithOutcome(f.Kernel, f.GoalId, f.TaskId).Verification);
        Assert.True(f.Task.CompletionBuildEvidence!.IsPending);
        Assert.False(f.Task.CompletionBuildEvidence.CustodyReleased);
        Assert.False(f.Task.CompletionBuildEvidence.Applied);
        Assert.Null(f.Task.LastVerification);
        Assert.Null(f.Task.LastDispatch!.ResultCommit);
        Assert.Equal(WorkTaskStatus.Running, f.Task.Status);
        Assert.Equal(operation.LeaseOwner, Lease(f));
        Assert.Equal(0, f.Stops);
        Assert.Equal(1, f.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealOwnedChildAfterResetSupportsCancellationButRejectsReusedIdentity(bool reusedIdentity)
    {
        var f = new CompletionBuildEvidenceFixture();
        using var child = new CompletionBuildEvidenceLaunchTests.SacrificialChild(holdAfterPermit: true);
        var coordinator = new CompletionBuildEvidenceCoordinator(f.StatePath, f.Root,
            utcNow: () => f.Clock.UtcNow, startProcess: child.Start);
        f.Runner(coordinator).RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        var operation = f.Task.CompletionBuildEvidence!;
        f.ResetWorkerMetadata();
        if (reusedIdentity)
        {
            var snapshot = f.Kernel.ExportSnapshot();
            var goal = snapshot.Goals.Single();
            var forged = operation with { ChildStartedAt = operation.ChildStartedAt!.Value.AddDays(-1) };
            f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [goal with
            { Tasks = goal.Tasks.Select(t => t.Id == f.TaskId.Value ? t with { CompletionBuildEvidence = forged } : t).ToArray() }] });
            new SqliteOrchestratorStateRepository(f.StatePath).SaveAsync(f.Kernel).GetAwaiter().GetResult();
            f.Reload();
            // Probe reads the still-held live OS process and refuses the mismatching start identity.
            Assert.Equal(CompletionBuildChildLiveness.Unknown, CompletionBuildEvidenceChildIdentity.Probe(forged));
            var held = new CompletionBuildEvidenceCoordinator(f.StatePath, f.Root).CancelAttached(f.Kernel, f.GoalId, f.TaskId,
                CompletionBuildEvidenceTarget.From(forged));
            Assert.True(held.IsPending);
            Assert.Equal(operation.LeaseOwner, Lease(f));
            Assert.False(child.Observer!.HasExited);
            // Restore the fixture's trusted original identity solely for owned teardown;
            // the negative control above must leave the real child and lease untouched.
            var cleanup = f.Kernel.ExportSnapshot();
            var cleanupGoal = cleanup.Goals.Single();
            f.Kernel = AgentOrchestratorKernel.FromSnapshot(cleanup with { Goals = [cleanupGoal with
            { Tasks = cleanupGoal.Tasks.Select(t => t.Id == f.TaskId.Value ? t with { CompletionBuildEvidence = operation } : t).ToArray() }] });
            new SqliteOrchestratorStateRepository(f.StatePath).SaveAsync(f.Kernel).GetAwaiter().GetResult();
            coordinator.CancelAttached(f.Kernel, f.GoalId, f.TaskId, CompletionBuildEvidenceTarget.From(operation));
        }
        else
        {
            f.Reload();
            var restarted = new CompletionBuildEvidenceCoordinator(f.StatePath, f.Root);
            var result = restarted.CancelAttached(f.Kernel, f.GoalId, f.TaskId, CompletionBuildEvidenceTarget.From(operation));
            Assert.True(child.Observer!.HasExited);
            Assert.Equal(CompletionBuildEvidencePhase.Cancelled, result.Phase);
            Assert.True(result.CustodyReleased);
            Assert.Null(Lease(f));
            coordinator.CancelAttached(f.Kernel, f.GoalId, f.TaskId, CompletionBuildEvidenceTarget.From(operation));
            Assert.Null(f.Task.LastProcess);
        }
    }

    private static CompletionBuildEvidenceFixture Start()
    {
        var f = new CompletionBuildEvidenceFixture();
        f.Runner().RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.Pending, f.Task.CompletionBuildEvidence!.Phase);
        return f;
    }

    private static void AttachNewerExitedWorker(CompletionBuildEvidenceFixture f, CompletionBuildEvidenceOperation operation)
    {
        var snapshot = f.Kernel.ExportSnapshot();
        var goal = snapshot.Goals.Single();
        var task = goal.Tasks.Single(t => t.Id == f.TaskId.Value);
        var original = task.DispatchHistory!.Single();
        var newer = original with { DispatchedAt = operation.DispatchAt.AddMinutes(30), ResultCommit = null };
        var worker = operation.WorkerProcess with
        {
            ProcessId = operation.WorkerProcess.ProcessId + 1, StartedAt = newer.DispatchedAt,
            CompletedAt = newer.DispatchedAt.AddMinutes(1), ExitCode = 0,
            StandardOutputPath = Path.Combine(f.Root, "logs", "newer.out.log"),
            StandardErrorPath = Path.Combine(f.Root, "logs", "newer.err.log"),
            ExitCodePath = Path.Combine(f.Root, "logs", "newer.exit.txt")
        };
        File.Copy(operation.WorkerProcess.StandardOutputPath, worker.StandardOutputPath);
        File.Copy(operation.WorkerProcess.StandardErrorPath, worker.StandardErrorPath);
        File.WriteAllText(worker.ExitCodePath, "0");
        var changed = task with { LastDispatch = newer, LastProcess = new TaskProcessSnapshot(
            worker.ProcessId, worker.Command, worker.WorkingDirectory, worker.StandardOutputPath,
            worker.StandardErrorPath, worker.ExitCodePath, worker.StartedAt, worker.CompletedAt, worker.ExitCode),
            DispatchHistory = [original, newer], VerificationHistory = [], Status = WorkTaskStatus.Running };
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [goal with
        { Tasks = goal.Tasks.Select(t => t.Id == changed.Id ? changed : t).ToArray() }] });
        f.Clock.Advance(TimeSpan.FromMinutes(31));
        new SqliteOrchestratorStateRepository(f.StatePath).SaveAsync(f.Kernel).GetAwaiter().GetResult();
        f.Reload();
    }

    private static string? Lease(CompletionBuildEvidenceFixture f) =>
        new ReconcileSweepRemediationStore(f.StatePath).TryGetAcceptanceLeaseOwner(f.GoalId.Value);

    private static GoalEvidenceOperationOwner ParseOwner(CompletionBuildEvidenceOperation operation)
    {
        Assert.True(GoalEvidenceOperationOwner.TryParse(operation.LeaseOwner, out var owner));
        return owner!;
    }

    private static IReadOnlyList<GoalOperationJournalEntry> Journal(CompletionBuildEvidenceFixture f, CompletionBuildEvidenceOperation operation)
    {
        var owner = ParseOwner(operation);
        return GoalOperationJournal.ReadStrict(f.Root, f.GoalId).Entries.Where(e => e.OperationInstanceId == owner.InstanceId).ToArray();
    }
}
