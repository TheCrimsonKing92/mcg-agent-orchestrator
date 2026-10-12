using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: isolated repository/database fixtures and controlled process/gate boundaries.
public sealed class ConductorFallbackBuildResponsivenessTests : ConductorBatchLoopTests
{
    public ConductorFallbackBuildResponsivenessTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task PendingBuildReturnsFromRefreshAndTickWhileIntentWorkerAndGateProgress()
    {
        var pending = new CompletionBuildEvidenceFixture();
        var completed = new CompletionBuildEvidenceFixture();
        completed.WriteReceipt(["src/Feature/Feature.csproj"]);
        pending.Kernel.ReplaceGoalWithSnapshot(completed.Kernel.ExportSnapshot().Goals.Single());
        var cancel = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.RunningTask();
        pending.Kernel.ReplaceGoalWithSnapshot(cancel.Kernel.ExportSnapshot().Goals.Single());
        var store = new CancelDispatchIntentTestStore();
        var intent = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.Intent(cancel.Goal, cancel.Task);
        await store.EnqueueAsync(intent);
        var cancellationRunner = new BackgroundDispatchRunner(isStillRunning: _ => false, tryKillOwnedProcess: _ => false);
        var intents = new OperatorIntentCoordinator(store)
        { CancelLatestProcess = cancellationRunner.CancelLatestProcess };
        var runner = new BackgroundDispatchRunner(pending.Clock, isStillRunning: _ => false,
            completionBuildEvidence: pending.Coordinator(),
            resolveWorkerBuildReceiptPath: id => id == pending.GoalId ? pending.ReceiptPath : completed.ReceiptPath);
        var operations = new GoalDispatchOperations();
        var first = operations.RefreshDispatches(pending.Kernel, pending.Goal, runner);
        Assert.True(Assert.Single(first.RefreshOutcomes!).PendingBuildEvidence!.IsPending);
        var originalOutput = File.ReadAllText(pending.Task.LastProcess!.StandardOutputPath);
        var originalDispatch = pending.Task.LastDispatch;

        var gate = CreateVerifiedSimpleGoal(pending.Kernel, "Unrelated gate completion");
        const string branchSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string mainSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        string[] gateScope = ["src/UnrelatedGate.cs"];
        ConductorParallelAcceptanceOwnedProcessLaunch? gateLaunch = null;
        var gates = new ConductorParallelAcceptanceAttemptCoordinator(Path.Combine(pending.Root, "gate-attempts"),
            Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, isProcessAlive: _ => true,
            acquireStableSlotLease: (_, _) => null,
            launchOwnedProcess: launch => { gateLaunch = launch; return new(74); });
        var gateCandidate = ConductorParallelAcceptanceCandidate.Create(gate, 0, gateScope, branchSha, mainSha);
        var startedGate = gates.Evaluate(gateCandidate, ConductorAutonomyPolicy.Conservative,
            (candidate, _, _, _, _) => ConductorParallelAcceptanceRunResult.Accepted(candidate,
                new AcceptanceVerificationSummary(true, [], BranchHeadSha: branchSha, MainHeadSha: mainSha)));
        Assert.NotNull(gateLaunch);
        gateLaunch.ExecuteInCurrentProcess(startedGate.Attempt.OwnerProcessId);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
            File.ReadAllText(startedGate.Attempt.MetadataPath), new System.Text.Json.JsonSerializerOptions
            { PropertyNameCaseInsensitive = true })!.ReconciledAt);
        var developerStarts = 0;
        var landings = 0;
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { developerStarts++; return DispatchStartOutcome.EmptyBatch("No new worker required."); },
            runAcceptance: _ => throw new InvalidOperationException("The completed gate must reconcile without another gate run."),
            runAcceptanceWithSlot: (_, _) => throw new InvalidOperationException("The completed gate must reconcile without another gate run."),
            getLandingFileScopes: goal => goal.Id == gate.Id ? gateScope : ["src/Feature/Feature.cs"],
            parallelAcceptanceAttemptCoordinator: gates,
            resolveAcceptanceHeads: _ => (branchSha, mainSha),
            land: goal => { if (goal.Id == pending.GoalId) landings++; return new LandingResult(
                goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); });
        Assert.True(driver.ParallelAcceptanceEnabled);
        new ConductorBatchLoop(operatorIntents: intents,
            refreshGoalDispatchesBeforeAdvance: (kernel, goal) =>
                goal.Id == pending.GoalId || goal.Id == completed.GoalId ? operations.RefreshDispatches(kernel, goal, runner) : null)
            .Run(pending.Kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 1, persistGoalTick: (_, _) => { });

        Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
        Assert.Equal(WorkTaskStatus.Cancelled, pending.Kernel.GetTask(cancel.Goal.Id, cancel.Task.Id).Status);
        Assert.Equal(WorkTaskStatus.Completed, pending.Kernel.GetTask(completed.GoalId, completed.TaskId).Status);
        Assert.NotNull(System.Text.Json.JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
            File.ReadAllText(startedGate.Attempt.MetadataPath), new System.Text.Json.JsonSerializerOptions
            { PropertyNameCaseInsensitive = true })!.ReconciledAt);
        Assert.Equal(0, developerStarts);
        Assert.Equal(0, landings);
        Assert.Equal(WorkTaskStatus.Running, pending.Task.Status);
        Assert.Equal(GoalLifecycleState.Running, GoalLifecycle.ResolveState(pending.Goal));
        Assert.Null(pending.Task.LastVerification);
        Assert.Null(pending.Task.LastDispatch!.ResultCommit);
        Assert.Same(originalDispatch, pending.Task.LastDispatch);
        Assert.Equal(originalOutput, File.ReadAllText(pending.Task.LastProcess.StandardOutputPath));
        Assert.Equal(1, pending.Launches);
        Assert.True(pending.Live);

        pending.Release();
        operations.RefreshDispatches(pending.Kernel, pending.Goal, runner);
        Assert.Equal(WorkTaskStatus.Completed, pending.Task.Status);
        Assert.True(pending.Task.CompletionBuildEvidence!.Applied);
        Assert.Single(pending.Task.VerificationHistory);
    }
}
