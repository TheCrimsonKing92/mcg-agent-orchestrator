using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliConductWatchBuildEvidenceTests : CliCommandTestBase
{
    [Fact]
    public void WatchLaunchesMissingBuildAndResumesPersistedPendingCompletionExactlyOnce()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var workspace = CreateRefinedWorkspace(fixture.Root) with { SqliteStatePath = fixture.StatePath };
        var stdout = File.ReadAllText(fixture.Task.LastProcess!.StandardOutputPath);
        var dispatchAt = fixture.Task.LastDispatch!.DispatchedAt;
        var receiptPath = Path.Combine(DotnetBuildStorageLayout.GoalArtifactsPath(fixture.GoalId), WorkerBuildReceipt.FileName);
        try
        {
            RunOneWatchTick(fixture, workspace);
            Assert.Equal(1, fixture.Launches);
            Assert.Equal(1, fixture.Authorizations);
            Assert.Equal(CompletionBuildEvidencePhase.Pending, fixture.Task.CompletionBuildEvidence!.Phase);
            Assert.Equal(WorkTaskStatus.Running, fixture.Task.Status);
            Assert.Null(fixture.Task.LastVerification);
            Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
            var operationId = fixture.Task.CompletionBuildEvidence.Id;

            fixture.Reload();
            RunOneWatchTick(fixture, workspace);
            Assert.Equal(operationId, fixture.Task.CompletionBuildEvidence!.Id);
            Assert.Equal(1, fixture.Launches);
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
        }
        finally
        {
            if (File.Exists(receiptPath)) File.Delete(receiptPath);
            File.Delete(workspace.ConductorStopFilePath);
        }
    }

    private static void RunOneWatchTick(CompletionBuildEvidenceFixture fixture, OrchestratorWorkspace workspace)
    {
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
            ["conduct", fixture.GoalId.Value, "--watch", "--policy", "Manual"], context));
        Assert.True(checkpoints > 0);
    }
}
