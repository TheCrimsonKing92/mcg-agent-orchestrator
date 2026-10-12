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
        var workspace = CreateRefinedWorkspace(fixture.Root) with { SqliteStatePath = fixture.StatePath };
        // Manual escalates Running and reaps the pending build. Allow only the running hold;
        // later stages still require operator action, so this fixture cannot start a worker or gate.
        var transitions = ConductorAutonomyPolicy.Manual.TransitionMap.ToDictionary(pair => pair.Key, pair => pair.Value);
        transitions[GoalLifecycleState.Running] = ConductorTransitionDecision.Auto;
        var policy = ConductorAutonomyPolicy.Manual with { Name = "WatchBuildEvidence", TransitionMap = transitions };
        var policyPath = Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json");
        File.WriteAllText(policyPath, policy.ToJson());
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
            Assert.Null(fixture.Task.LastVerification);
            Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
            var operationId = fixture.Task.CompletionBuildEvidence.Id;

            fixture.Reload();
            RunOneWatchTick(fixture, workspace);
            Assert.Equal(operationId, fixture.Task.CompletionBuildEvidence!.Id);
            Assert.Equal(1, fixture.Launches);
            Assert.True(fixture.Live);
            Assert.Equal(0, fixture.Stops);
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
        }
        finally
        {
            if (File.Exists(receiptPath)) File.Delete(receiptPath);
            File.Delete(workspace.ConductorStopFilePath);
        }
    }

    private static void RunOneWatchTick(CompletionBuildEvidenceFixture fixture, OrchestratorWorkspace workspace)
    {
        var policy = CliConductLoopStartup.ResolveConductorPolicy(null, workspace.OrchestratorDirectory).Policy;
        Assert.Equal(ConductorTransitionDecision.Auto, policy.GetTransitionDecision(GoalLifecycleState.Running));
        Assert.All(policy.TransitionMap.Where(pair => pair.Key != GoalLifecycleState.Running),
            pair => Assert.Equal(ConductorTransitionDecision.Escalate, pair.Value));
        File.Delete(workspace.ConductorStopFilePath);
        var checkpoints = 0;
        void Checkpoint(AgentOrchestratorKernel kernel)
        {
            new SqliteOrchestratorStateRepository(fixture.StatePath).SaveAsync(kernel).GetAwaiter().GetResult();
            checkpoints++;
            // The checkpoint is the release signal; no elapsed-time pacing or live workers.
            File.WriteAllText(workspace.ConductorStopFilePath, "controlled watch tick complete");
        }
        var context = new CliExecutionContext(fixture.Kernel, workspace, new InMemoryModelProviderRegistry([]),
            [], WorkerProfileCatalog.Default(), fixture.Goal,
            persistKernel: Checkpoint, persistGoalKernel: (kernel, _) => Checkpoint(kernel))
        {
            CompletionBuildEvidence = fixture.Coordinator(),
            CleanupContext = CreateIsolatedCleanupContext(workspace)
        };
        CaptureConsole(() => CliCommandHandlers.Execute(
            ["conduct", fixture.GoalId.Value, "--watch"], context));
        Assert.True(checkpoints > 0);
    }
}
