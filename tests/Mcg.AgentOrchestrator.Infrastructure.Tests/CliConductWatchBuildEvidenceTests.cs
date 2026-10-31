using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliConductWatchBuildEvidenceTests : CliCommandTestBase
{
    [Fact]
    public void WatchLaunchesMissingBuildAndResumesPersistedPendingCompletionExactlyOnce()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var workspace = CreateWatchWorkspace(fixture);
        var stdout = File.ReadAllText(fixture.Task.LastProcess!.StandardOutputPath);
        var dispatchAt = fixture.Task.LastDispatch!.DispatchedAt;
        var receiptPath = Path.Combine(DotnetBuildStorageLayout.GoalArtifactsPath(fixture.GoalId), WorkerBuildReceipt.FileName);
        try
        {
            RunOneWatchTick(fixture, workspace);
            Assert.Equal(1, fixture.Launches);
            Assert.Equal(1, fixture.Authorizations);
            Assert.Equal(CompletionBuildEvidencePhase.Pending, fixture.Task.CompletionBuildEvidence!.Phase);
            Assert.True(fixture.Live);
            Assert.Equal(0, fixture.Stops);
            Assert.Equal(WorkTaskStatus.Running, fixture.Task.Status);
            Assert.False(fixture.Task.LastProcess!.IsRunning);
            Assert.Equal(GoalLifecycleState.Running, GoalLifecycle.ResolveState(fixture.Goal));
            Assert.Null(fixture.Task.LastVerification);
            Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
            var operationId = fixture.Task.CompletionBuildEvidence.Id;

            fixture.Reload();
            Assert.Equal(0, RunOneWatchTick(fixture, workspace));
            Assert.Equal(operationId, fixture.Task.CompletionBuildEvidence!.Id);
            Assert.Equal(1, fixture.Launches);
            Assert.True(fixture.Live);
            Assert.Equal(0, fixture.Stops);
            Assert.Equal(GoalLifecycleState.Running, GoalLifecycle.ResolveState(fixture.Goal));
            Assert.Null(fixture.Task.LastVerification);
            Assert.Null(fixture.Task.LastDispatch!.ResultCommit);

            fixture.Release();
            Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
            File.Copy(fixture.ReceiptPath, receiptPath);
            fixture.Reload();
            RunOneWatchTick(fixture, workspace);
            Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
            Assert.True(fixture.Task.CompletionBuildEvidence!.Applied);
            Assert.True(Assert.Single(fixture.Task.VerificationHistory).Succeeded);
            Assert.NotNull(fixture.Task.LastDispatch!.ResultCommit);
            Assert.Equal(dispatchAt, fixture.Task.LastDispatch.DispatchedAt);
            Assert.Equal(stdout, File.ReadAllText(fixture.Task.LastProcess!.StandardOutputPath));
            Assert.Equal(1, fixture.Launches);
            Assert.Equal(1, fixture.Authorizations);
            Assert.Equal(0, fixture.Stops);

            var resultCommit = fixture.Task.LastDispatch.ResultCommit;
            fixture.Reload();
            RunOneWatchTick(fixture, workspace);
            Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
            Assert.Equal(resultCommit, fixture.Task.LastDispatch!.ResultCommit);
            Assert.Single(fixture.Task.VerificationHistory);
            Assert.Equal(1, fixture.Launches);
            Assert.Equal(1, fixture.Authorizations);
            Assert.Equal(0, fixture.Stops);
        }
        finally
        {
            if (File.Exists(receiptPath)) File.Delete(receiptPath);
            File.Delete(workspace.ConductorStopFilePath);
        }
    }

    [Fact]
    public void WatchPendingHold_CheckpointStopProtocol_FailsBeforeSleep()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var workspace = CreateWatchWorkspace(fixture);
        try
        {
            RunOneWatchTick(fixture, workspace);
            fixture.Reload();
            var operationId = fixture.Task.CompletionBuildEvidence!.Id;

            // Negative control: reproduce the original stop protocol. The tick observer
            // rejects its missing signal synchronously, before the watch loop can sleep.
            var failure = Assert.Throws<InvalidOperationException>(() =>
                RunOneWatchTick(fixture, workspace, stopFromCheckpoint: true));
            Assert.Equal("Checkpoint stop protocol missed unchanged tick; checkpoints=0.", failure.Message);
            Assert.False(File.Exists(workspace.ConductorStopFilePath));
            Assert.Equal(operationId, fixture.Task.CompletionBuildEvidence!.Id);
            Assert.Equal(CompletionBuildEvidencePhase.Pending, fixture.Task.CompletionBuildEvidence.Phase);
            Assert.Equal(1, fixture.Launches);
            Assert.Equal(1, fixture.Authorizations);
            Assert.True(fixture.Live);
            Assert.Equal(0, fixture.Stops);
            Assert.Null(fixture.Task.LastVerification);
            Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        }
        finally
        {
            File.Delete(workspace.ConductorStopFilePath);
        }
    }

    private OrchestratorWorkspace CreateWatchWorkspace(CompletionBuildEvidenceFixture fixture)
    {
        var workspace = CreateRefinedWorkspace(fixture.Root) with { SqliteStatePath = fixture.StatePath };
        // Manual escalates Running and reaps the pending build. Allow only the running hold;
        // later stages still require operator action, so this fixture cannot start a worker or gate.
        var transitions = ConductorAutonomyPolicy.Manual.TransitionMap.ToDictionary(pair => pair.Key, pair => pair.Value);
        transitions[GoalLifecycleState.Running] = ConductorTransitionDecision.Auto;
        var policy = ConductorAutonomyPolicy.Manual with { Name = "WatchBuildEvidence", TransitionMap = transitions };
        File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json"), policy.ToJson());
        return workspace;
    }

    private int RunOneWatchTick(CompletionBuildEvidenceFixture fixture, OrchestratorWorkspace workspace,
        bool stopFromCheckpoint = false)
    {
        var policy = CliConductLoopStartup.ResolveConductorPolicy(null, workspace.OrchestratorDirectory).Policy;
        Assert.Equal(ConductorTransitionDecision.Auto, policy.GetTransitionDecision(GoalLifecycleState.Running));
        Assert.All(policy.TransitionMap.Where(pair => pair.Key != GoalLifecycleState.Running),
            pair => Assert.Equal(ConductorTransitionDecision.Escalate, pair.Value));
        File.Delete(workspace.ConductorStopFilePath);
        var checkpoints = 0;
        int? checkpointsAtTick = null;
        void Checkpoint(AgentOrchestratorKernel kernel)
        {
            new SqliteOrchestratorStateRepository(fixture.StatePath).SaveAsync(kernel).GetAwaiter().GetResult();
            checkpoints++;
            if (stopFromCheckpoint)
                File.WriteAllText(workspace.ConductorStopFilePath, "checkpoint stop protocol");
        }
        var context = new CliExecutionContext(fixture.Kernel, workspace, new InMemoryModelProviderRegistry([]),
            [], WorkerProfileCatalog.Default(), fixture.Goal,
            persistKernel: Checkpoint, persistGoalKernel: (kernel, _) => Checkpoint(kernel))
        {
            CompletionBuildEvidence = fixture.Coordinator(),
            ConductorTickObserver = tick =>
            {
                Assert.Equal(1, tick.Tick);
                Assert.Null(checkpointsAtTick);
                checkpointsAtTick = checkpoints;
                if (stopFromCheckpoint)
                {
                    if (!File.Exists(workspace.ConductorStopFilePath))
                        throw new InvalidOperationException(
                            $"Checkpoint stop protocol missed unchanged tick; checkpoints={checkpoints}.");
                }
                else
                {
                    // Every tick signals completion, including a hold with no state changes.
                    File.WriteAllText(workspace.ConductorStopFilePath, "controlled watch tick complete");
                }
            },
            CleanupContext = CreateIsolatedCleanupContext(workspace)
        };
        CaptureConsole(() => CliCommandHandlers.Execute(
            ["conduct", fixture.GoalId.Value, "--watch"], context));
        Assert.NotNull(checkpointsAtTick);
        return checkpointsAtTick.Value;
    }
}
