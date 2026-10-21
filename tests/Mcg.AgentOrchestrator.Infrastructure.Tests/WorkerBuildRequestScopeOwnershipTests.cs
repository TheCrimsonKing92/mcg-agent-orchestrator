using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its repository, canonical stores, build storage and native children.
public sealed class WorkerBuildRequestScopeOwnershipTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SuccessorPreparation_PreservesNativeCompletion(bool abortStart, bool reload)
    {
        using var fixture = new WorkerBuildRequestFixture(goalBranch: true);
        var original = await fixture.RunHelper();
        Assert.Equal(WorkerBuildOutcome.Success, original.Outcome);
        Assert.Equal(0, original.CompilerExitCode);
        Assert.Single(original.Children!);
        Assert.True(original.CustodyReleased);
        var before = WorkerBuildRequestScopeSupport.Load(fixture);
        var dispatch = before.GetTask(fixture.GoalId, fixture.TaskId).LastDispatch!;
        Assert.Equal(1, dispatch.WorkerBuildProtocolVersion);
        Assert.Equal(dispatch.DispatchedAt, dispatch.WorkerBuildScope!.CapturedDispatchAt);
        Assert.NotNull(dispatch.WorkerBuildScope.RetainedMetadataBase64);
        WorkerBuildRequestScopeSupport.PrepareSuccessor(fixture, abortStart);
        var successorBytes = File.ReadAllBytes(WorkerBuildProjectScope.MetadataPath(fixture.RepositoryRoot));
        Assert.NotEqual(dispatch.WorkerBuildScope.MetadataDigest,
            WorkerBuildProjectScope.ReadDispatchIdentity(fixture.RepositoryRoot).MetadataDigest);
        var kernel = reload ? JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(WorkerBuildRequestScopeSupport.Load(fixture).ExportSnapshot()))! : before.ExportSnapshot();
        var reloaded = AgentOrchestratorKernel.FromSnapshot(kernel);
        Assert.Equal(dispatch.WorkerBuildScope, reloaded.GetTask(fixture.GoalId, fixture.TaskId).LastDispatch!.WorkerBuildScope);
        var consumer = reload ? new WorkerBuildInvocationOperations(fixture.StatePath, fixture.Root, fixture.Storage) : fixture.Operations;

        var result = consumer.Consume(reloaded, fixture.GoalId, fixture.TaskId,
            [WorkerBuildRequestFixture.Source], [WorkerBuildRequestFixture.Source]);

        Assert.False(result.MissingEvidence, result.Diagnostic);
        Assert.False(result.CompilerFailed, result.Diagnostic);
        var consumed = WorkerBuildRequestScopeSupport.Invocation(fixture);
        Assert.True(consumed.Consumed);
        Assert.True(consumed.CustodyReleased);
        Assert.True(original.SameOrigin(consumed));
        Assert.Equal(original.Children, consumed.Children);
        Assert.Equal(original.Observations, consumed.Observations);
        Assert.Equal(original.TreeDigest, consumed.TreeDigest);
        Assert.Single(WorkerBuildRequestScopeSupport.Load(fixture).GetTask(fixture.GoalId, fixture.TaskId).DispatchHistory);
        Assert.Equal(successorBytes, File.ReadAllBytes(WorkerBuildProjectScope.MetadataPath(fixture.RepositoryRoot)));
    }

    [Fact]
    public async Task SuccessorCompletion_UsesItsOwnNativeDispatchScope()
    {
        using var fixture = new WorkerBuildRequestFixture(goalBranch: true);
        Assert.Equal(WorkerBuildOutcome.Success, (await fixture.RunHelper()).Outcome);
        // Give the successor a different dispatch base while keeping the built tree unchanged.
        fixture.Git("add", WorkerBuildRequestFixture.Source);
        fixture.Git("commit", "-m", "built candidate");
        var successorId = WorkerBuildRequestScopeSupport.PrepareSuccessor(fixture, false);
        Assert.False(fixture.Consume().MissingEvidence);
        var kernel = WorkerBuildRequestScopeSupport.Load(fixture);
        var successor = kernel.GetTask(fixture.GoalId, successorId);
        var predecessor = kernel.GetTask(fixture.GoalId, fixture.TaskId);
        Assert.NotEqual(predecessor.LastDispatch!.BaseCommit, successor.LastDispatch!.BaseCommit);
        Assert.NotEqual(predecessor.LastDispatch.WorkerBuildScope, successor.LastDispatch.WorkerBuildScope);
        var reference = fixture.Operations.Prepare(kernel, fixture.GoalId, successorId);
        using var process = Process.GetCurrentProcess();
        var identity = WorkerBuildRequestChannel.ReadIdentity(process);
        kernel.RecordTaskProcessStarted(fixture.GoalId, successorId, new TaskProcessRecord(process.Id,
            successor.LastDispatch.Command, fixture.RepositoryRoot, Path.Combine(fixture.Root, "successor.out"),
            Path.Combine(fixture.Root, "successor.err"), Path.Combine(fixture.Root, "successor.exit"),
            successor.LastDispatch.DispatchedAt, null, null, ProcessIdentityStartedAt: identity.StartedAt,
            ProcessIdentityImagePath: identity.ImagePath));
        var intents = new SqliteOperatorIntentStore(fixture.Reference.Admission!.StorePath, fixture.Root);
        var admission = intents.CaptureAdmission(fixture.GoalId.Value, successorId.Value) with { DispatchedAt = reference.DispatchAt };
        intents.BindPreparedAdmission(admission, reference.DispatchAt);
        reference = reference with { Admission = admission };
        WorkerBuildRequestScopeSupport.Save(fixture, kernel);
        var host = new WorkerBuildRequestHost(reference, storageRoot: fixture.Storage);
        var response = await host.ExecuteAsync(new WorkerBuildRequest(1, "successor", [WorkerBuildRequestFixture.Project], "Debug"),
            identity, CancellationToken.None);
        Assert.Equal(WorkerBuildOutcome.Success, response.Outcome);

        var result = fixture.Operations.Consume(kernel, fixture.GoalId, successorId, [WorkerBuildRequestFixture.Source], []);

        Assert.False(result.MissingEvidence, result.Diagnostic);
        var stored = WorkerBuildRequestScopeSupport.Load(fixture).GetTask(fixture.GoalId, successorId);
        var invocation = Assert.Single(stored.WorkerBuildInvocations);
        Assert.True(invocation.Consumed);
        Assert.Equal(reference.DispatchId, invocation.DispatchId);
        Assert.NotEqual(fixture.Reference.DispatchId, invocation.DispatchId);
        Assert.Equal(successor.LastDispatch.WorkerBuildScope, stored.LastDispatch!.WorkerBuildScope);
        Assert.Equal(successor.LastDispatch.BaseCommit, stored.LastDispatch.BaseCommit);
    }
}
