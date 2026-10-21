using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its native dispatch, build, state and git repository.
public sealed class ExitReconcileBuildDispositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingConsumer_NativeSuccess_PreservesExitAndDeniesCompletion(bool unscopedRecovery)
    {
        using var fixture = new ExitReconcileBuildFixture();
        var invocation = await fixture.CompleteNativeDispatch();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        var exitPath = fixture.Task.LastProcess!.ExitCodePath;
        var nativeArtifact = File.ReadAllBytes(exitPath);
        if (unscopedRecovery)
            Assert.Equal(1, Mcg.AgentOrchestrator.App.Orchestration.StaleDispatchProcessReconciler.Reconcile(fixture.Kernel));
        else
            new BackgroundDispatchRunner().RefreshLatestProcessWithOutcome(fixture.Kernel,
                fixture.Build.GoalId, fixture.Build.TaskId);

        var verification = Assert.IsType<TaskVerificationRecord>(fixture.Task.LastVerification);
        Assert.Equal("worker-build-consumer-unavailable", verification.OrchestratorFailureReason);
        Assert.Equal(0, verification.ExitCode);
        Assert.Equal(0, verification.ObservedRootExitCode);
        Assert.Equal(0, verification.ChildExitCode);
        Assert.False(verification.Succeeded);
        Assert.False(verification.CompletionVerdictVerifiedSuccess);
        Assert.Contains("worker_build_host=unavailable; enrolled dispatch cannot use legacy worker receipts",
            verification.StandardError);
        Assert.Equal(0, fixture.Task.LastProcess.ExitCode);
        Assert.Equal(DispatchExitArtifactOrigin.Native, fixture.Task.LastProcess.ExitArtifactOrigin);
        Assert.Equal(nativeArtifact, File.ReadAllBytes(exitPath));
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        Assert.False(fixture.StoredInvocation().Consumed);
        Assert.Equal(invocation.Version, fixture.StoredInvocation().Version);
        Assert.Single(fixture.Task.VerificationHistory);
        fixture.Save();
        fixture.Reload();
        Assert.Equal("worker-build-consumer-unavailable", fixture.Task.LastVerification!.OrchestratorFailureReason);
        Assert.False(fixture.Task.LastVerification.Succeeded);
    }
}
