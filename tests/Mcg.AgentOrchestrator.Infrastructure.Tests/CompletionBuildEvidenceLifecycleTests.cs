using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: injected child lifecycle/clock and unique SQLite/journal roots.
public sealed class CompletionBuildEvidenceLifecycleTests
{
    [Fact]
    public void Protocol1QueueDeadlineBuildsSameCandidateWithoutWorkerRetry()
    {
        var fixture = InfrastructureFixture();
        var dispatches = fixture.Task.DispatchHistory.ToArray();
        var runner = InfrastructureRunner(fixture);

        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        Assert.True(fixture.Task.CompletionBuildEvidence!.IsPending);
        Assert.Equal(fixture.Task.LastDispatch!.DispatchedAt, fixture.Task.CompletionBuildEvidence.DispatchAt);
        Assert.Equal(1, fixture.Launches);
        Assert.NotEqual(WorkTaskStatus.Failed, fixture.Task.Status);
        Assert.Equal(dispatches, fixture.Task.DispatchHistory.ToArray());
        Assert.Equal(0, fixture.Task.WorkerBuildCheckRecoveryCount);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Null(fixture.Task.LastVerification);

        fixture.Release(CompletionBuildEvidencePhase.Success);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.True(fixture.Task.LastVerification!.Succeeded);
        Assert.Equal(CompletionBuildEvidencePhase.Success, fixture.Task.CompletionBuildEvidence.Phase);
        Assert.Equal(dispatches.Length, fixture.Task.DispatchHistory.Count);
        Assert.Equal(dispatches.Select(dispatch => dispatch.DispatchedAt),
            fixture.Task.DispatchHistory.Select(dispatch => dispatch.DispatchedAt));
        Assert.Equal(1, fixture.Launches);
        Assert.Equal(0, fixture.Task.WorkerBuildCheckRecoveryCount);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Contains("worker_build_infrastructure=QueueDeadline", fixture.Task.LastVerification.StandardError);
    }

    [Theory]
    [InlineData(CompletionBuildEvidencePhase.CompilerFailure, DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed)]
    [InlineData(CompletionBuildEvidencePhase.TimedOut, DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing)]
    [InlineData(CompletionBuildEvidencePhase.ApparatusFailure, DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing)]
    public void Protocol1InfrastructureBuildTerminalOutcomePreservesFailureRoute(CompletionBuildEvidencePhase phase, string marker)
    {
        var fixture = InfrastructureFixture();
        var runner = InfrastructureRunner(fixture);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.True(fixture.Task.CompletionBuildEvidence!.IsPending);

        fixture.Release(phase);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
        Assert.Equal(phase, fixture.Task.CompletionBuildEvidence.Phase);
        Assert.Contains(DispatchFailureDiagnosticMarker.Format(marker), fixture.Task.LastVerification!.StandardError);
        Assert.Contains("worker_build_infrastructure=QueueDeadline", fixture.Task.LastVerification.StandardError);
        Assert.Single(fixture.Task.DispatchHistory);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal(0, fixture.Task.WorkerBuildCheckRecoveryCount);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
    }

    [Fact]
    public void Protocol1IncompleteScopeDoesNotLaunchCompletionBuild()
    {
        var fixture = InfrastructureFixture(scopeComplete: false);

        InfrastructureRunner(fixture).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        Assert.Equal(0, fixture.Launches);
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
        Assert.False(fixture.Task.LastVerification!.AssignedScopeComplete);
    }

    [Fact]
    public void Protocol1CompilerFailureVetoesQueueRequeueAndMatchesCompilerOnlyOutcome()
    {
        var fixture = InfrastructureFixture();
        InfrastructureBuildBlockerClassifierTests.RecordInvocation(fixture.Kernel, fixture.Goal, fixture.Task, WorkerBuildOutcome.CompilerFailure);
        var control = InfrastructureFixture(outcome: WorkerBuildOutcome.CompilerFailure);

        InfrastructureRunner(fixture).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        InfrastructureRunner(control).RefreshLatestProcess(control.Kernel, control.GoalId, control.TaskId);

        Assert.Equal(0, fixture.Launches);
        Assert.Equal(0, control.Launches);
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        Assert.Null(control.Task.CompletionBuildEvidence);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
        Assert.Equal(control.Task.Status, fixture.Task.Status);
        Assert.Equal(control.Task.LastVerification!.ExitCode, fixture.Task.LastVerification!.ExitCode);
        Assert.Equal(control.Task.LastVerification.Succeeded, fixture.Task.LastVerification.Succeeded);
        var actual = DispatchFailureClassifier.Classify(fixture.Task, fixture.Task.LastVerification);
        var expected = DispatchFailureClassifier.Classify(control.Task, control.Task.LastVerification);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(TaskOutcomeClassifier.TryExtractRule(expected.ClassifierReceipt), TaskOutcomeClassifier.TryExtractRule(actual.ClassifierReceipt));
        Assert.Contains(DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed),
            fixture.Task.LastVerification.StandardError);
    }

    [Fact]
    public void Protocol1ReceiptAloneCannotCertifyInfrastructureRound()
    {
        var fixture = InfrastructureFixture();
        fixture.WriteReceipt(["src/Feature/Feature.csproj"]);

        InfrastructureRunner(fixture).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        Assert.Equal(0, fixture.Launches);
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
        Assert.Contains(DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing),
            fixture.Task.LastVerification!.StandardError);
    }

    [Theory]
    [InlineData(WorkerBuildOutcome.Interrupted)]
    [InlineData(WorkerBuildOutcome.ApparatusFailure)]
    [InlineData(WorkerBuildOutcome.TimeoutAfterStart)]
    public void Protocol1OtherInfrastructureOutcomesAlsoUseCompletionBuild(WorkerBuildOutcome outcome)
    {
        var fixture = InfrastructureFixture(outcome: outcome);
        var runner = InfrastructureRunner(fixture);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.True(fixture.Task.CompletionBuildEvidence!.IsPending);
        fixture.Release();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.Single(fixture.Task.DispatchHistory);
        Assert.Equal(1, fixture.Launches);
    }

    private static CompletionBuildEvidenceFixture InfrastructureFixture(bool scopeComplete = true,
        WorkerBuildOutcome outcome = WorkerBuildOutcome.CapacityBeforeStart)
    {
        var fixture = new CompletionBuildEvidenceFixture(workerBuildProtocol1: true);
        var outputPath = fixture.Task.LastProcess!.StandardOutputPath!;
        File.WriteAllText(outputPath, File.ReadAllText(outputPath).Replace("END_WORKER_RESULT",
            $"assigned_scope_complete: {scopeComplete.ToString().ToLowerInvariant()}{Environment.NewLine}END_WORKER_RESULT", StringComparison.Ordinal));
        InfrastructureBuildBlockerClassifierTests.RecordInvocation(fixture.Kernel, fixture.Goal, fixture.Task, outcome);
        new SqliteOrchestratorStateRepository(fixture.StatePath).SaveAsync(fixture.Kernel).GetAwaiter().GetResult();
        return fixture;
    }

    private static BackgroundDispatchRunner InfrastructureRunner(CompletionBuildEvidenceFixture fixture) => new(fixture.Clock,
        isStillRunning: _ => false, completionBuildEvidence: fixture.Coordinator(), resolveWorkerBuildReceiptPath: _ => fixture.ReceiptPath)
        { WorkerBuildInvocations = new MissingInfrastructureBuildEvidence() };

    private sealed class MissingInfrastructureBuildEvidence : IWorkerBuildInvocationOperations
    {
        public WorkerBuildHostReference Prepare(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId) =>
            throw new InvalidOperationException("Completion must not dispatch another worker.");
        public bool ReconcileOutstanding(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, bool cancelled = false) => true;
        public WorkerBuildConsumption Consume(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId,
            IReadOnlyList<string> changedPaths, IReadOnlyList<string> dirtyPaths)
        {
            var task = kernel.GetTask(goalId, taskId);
            var compilerFailed = task.WorkerBuildInvocations.Any(invocation =>
                invocation.DispatchAt == task.LastDispatch!.DispatchedAt && invocation.Outcome == WorkerBuildOutcome.CompilerFailure);
            return new(!compilerFailed, compilerFailed, "worker_build_host=missing-current-candidate-outcome");
        }
    }

    [Fact]
    public void UnknownBuildChildIdentityCannotSilentlyCancelCurrentWorker()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var runner = fixture.Runner();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        fixture.Unknown = true;
        Assert.Contains("custody is retained", Assert.Throws<InvalidOperationException>(() =>
            runner.CancelLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId)).Message);
        Assert.Equal(WorkTaskStatus.Running, fixture.Task.Status);
        Assert.False(fixture.Task.LastProcess!.WasCancelled);
        Assert.True(fixture.Task.CompletionBuildEvidence!.IsPending);
        Assert.Equal(fixture.Task.CompletionBuildEvidence.LeaseOwner,
            new ReconcileSweepRemediationStore(fixture.StatePath).TryGetAcceptanceLeaseOwner(fixture.GoalId.Value));
        Assert.Equal(0, fixture.Stops);
    }

    [Theory]
    [InlineData(CompletionBuildEvidencePhase.Launching)]
    [InlineData(CompletionBuildEvidencePhase.WaitingForLease)]
    [InlineData(CompletionBuildEvidencePhase.Pending)]
    public void PendingCompletionBuildKeepsExitedWorkerInRunningLifecycle(CompletionBuildEvidencePhase phase)
    {
        var fixture = new CompletionBuildEvidenceFixture();
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        fixture.Kernel.RecordCompletionBuildEvidence(fixture.GoalId, fixture.TaskId, operation with { Phase = phase });

        Assert.False(fixture.Task.LastProcess!.IsRunning);
        Assert.Equal(WorkTaskStatus.Running, fixture.Task.Status);
        Assert.Equal(GoalLifecycleState.Running, GoalLifecycle.ResolveState(fixture.Goal));
        Assert.Null(fixture.Task.LastVerification);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);

        fixture.Kernel.RecordCompletionBuildEvidence(fixture.GoalId, fixture.TaskId,
            operation with { Phase = CompletionBuildEvidencePhase.Cancelled });
        Assert.Equal(GoalLifecycleState.Dispatched, GoalLifecycle.ResolveState(fixture.Goal));
    }

    [Fact]
    public void DeadlineStopsOwnedChildAndReleasesLeaseWithoutProductTestVerdict()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var runner = fixture.Runner();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        fixture.Clock.Advance(OrchestratorBuildEvidenceCheck.DefaultTimeout + TimeSpan.FromMinutes(1));
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.TimedOut, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Equal(1, fixture.Stops);
        Assert.Null(new ReconcileSweepRemediationStore(fixture.StatePath).TryGetAcceptanceLeaseOwner(fixture.GoalId.Value));
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
    }

    [Theory]
    [InlineData(CompletionBuildEvidencePhase.CompilerFailure)]
    [InlineData(CompletionBuildEvidencePhase.ApparatusFailure)]
    [InlineData(CompletionBuildEvidencePhase.TimedOut)]
    public void TerminalBuildOutcomePreservesTypedReasonAndDiagnostics(CompletionBuildEvidencePhase phase)
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var runner = fixture.Runner();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        fixture.Release(phase, receipt: false);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(phase, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Contains("controlled owned build result", fixture.Task.LastVerification!.StandardError);
        Assert.False(fixture.Task.LastVerification.Succeeded);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
    }

    [Fact]
    public void WorkerCancellationPreservesAlreadyTerminalBuildSuccessAfterReload()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var runner = fixture.Runner();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        fixture.Release();
        runner.CancelLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.Success, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.True(fixture.Task.CompletionBuildEvidence.CustodyReleased);
        Assert.Equal(WorkTaskStatus.Cancelled, fixture.Task.Status);
        Assert.True(fixture.Task.LastProcess!.WasCancelled);
        Assert.Null(new ReconcileSweepRemediationStore(fixture.StatePath).TryGetAcceptanceLeaseOwner(fixture.GoalId.Value));
        // The caller persists worker cancellation; the coordinator persists only its operation.
        new SqliteOrchestratorStateRepository(fixture.StatePath).SaveAsync(fixture.Kernel).GetAwaiter().GetResult();
        fixture.Reload();
        Assert.Equal(CompletionBuildEvidencePhase.Success, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.True(fixture.Task.CompletionBuildEvidence.CustodyReleased);
        Assert.True(fixture.Task.LastProcess!.WasCancelled);
        Assert.Null(fixture.Task.LastVerification);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        Assert.Equal(0, fixture.Stops);
        Assert.Equal(WorkTaskStatus.Cancelled, fixture.Task.Status);
    }

    [Theory]
    [InlineData("launch")]
    [InlineData("checkpoint")]
    public void FailedLaunchAuthorizationCannotExecuteBuild(string failure)
    {
        var fixture = new CompletionBuildEvidenceFixture
        { FailLaunch = failure == "launch", FailChildCheckpoint = failure == "checkpoint", BusyLease = failure == "lease" };
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(0, fixture.Authorizations);
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        if (failure != "launch") Assert.Equal(1, fixture.Stops);
    }

    [Fact]
    public void UnknownLivenessHoldsAndDeadChildWithoutReceiptFailsWithoutRelaunch()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        fixture.Reload();
        fixture.Unknown = true;
        var outcome = fixture.Runner().RefreshLatestProcessWithOutcome(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.True(outcome.PendingBuildEvidence!.IsPending);
        Assert.Null(outcome.Verification);
        Assert.Equal(1, fixture.Launches);
        fixture.Unknown = false;
        fixture.Live = false;
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Equal(1, fixture.Launches);
        Assert.Null(new ReconcileSweepRemediationStore(fixture.StatePath).TryGetAcceptanceLeaseOwner(fixture.GoalId.Value));
    }

    [Fact]
    public void BusyLeaseHoldsCompletionWithoutExecutingBuildOrSpendingWorkerRound()
    {
        var fixture = new CompletionBuildEvidenceFixture { BusyLease = true };
        var outcome = fixture.Runner().RefreshLatestProcessWithOutcome(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.WaitingForLease, outcome.PendingBuildEvidence!.Phase);
        Assert.Null(outcome.Verification);
        Assert.Equal(0, fixture.Authorizations);
        Assert.Equal(WorkTaskStatus.Running, fixture.Task.Status);
        fixture.BusyLease = false;
        fixture.Live = true;
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(1, fixture.Authorizations);
        Assert.Equal(CompletionBuildEvidencePhase.Pending, fixture.Task.CompletionBuildEvidence!.Phase);
    }

    [Fact]
    public void FailedLaunchIntentPersistenceReturnsApparatusOutcomeWithoutLaunchingChild()
    {
        var fixture = new CompletionBuildEvidenceFixture { FailLaunchIntent = true };
        var outcome = fixture.Runner().RefreshLatestProcessWithOutcome(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Equal(0, fixture.Launches);
        Assert.Equal(0, fixture.Authorizations);
        Assert.Contains("launch intent could not be persisted", outcome.Verification!.StandardError);
        Assert.False(outcome.Verification.Succeeded);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
    }
}
