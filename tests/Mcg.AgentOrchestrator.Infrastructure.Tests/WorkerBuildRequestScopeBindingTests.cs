using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: genuine host compilations and all authority mutations use fixture-owned resources.
public sealed class WorkerBuildRequestScopeBindingTests
{
    [Fact]
    public async Task InvalidRetainedAuthority_CannotConsumeNativePass()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake, goalBranch: true);
        var original = await fixture.RunHelper();
        Assert.Equal(WorkerBuildOutcome.Success, original.Outcome);
        WorkerBuildRequestScopeSupport.PrepareSuccessor(fixture, false);
        var snapshot = WorkerBuildRequestScopeSupport.Load(fixture).ExportSnapshot();
        var dispatch = WorkerBuildRequestScopeSupport.Load(fixture).GetTask(fixture.GoalId, fixture.TaskId).LastDispatch!;
        var identity = dispatch.WorkerBuildScope!;
        (string Name, WorkerBuildScopeIdentity? Identity)[] controls =
        [
            ("no-authority", null),
            ("missing-contents", identity with { RetainedMetadataBase64 = null }),
            ("missing-binding", identity with { CapturedDispatchAt = null }),
            ("foreign-attempt", identity with { CapturedDispatchAt = dispatch.DispatchedAt.AddTicks(1) }),
            ("corrupt-base64", identity with { RetainedMetadataBase64 = "invalid!" }),
            ("corrupt-json", identity with { RetainedMetadataBase64 = Convert.ToBase64String("{}"u8) }),
            ("baseline", identity with { GoalBaseline = new string('a', 40) }),
            ("goal", Mutate(identity, m => m with { GoalId = GoalId.New().Value }, true)),
            ("task", Mutate(identity, m => m with { TaskId = TaskId.New().Value }, true)),
            ("worktree", Mutate(identity, m => m with { WorktreeRoot = fixture.Root }, true)),
            ("dispatch-base", Mutate(identity, m => m with { DispatchBase = new string('a', 40) }, true)),
            ("integration", Mutate(identity, m => m with { IntegrationBranch = "foreign" })),
            ("dispatch-nonce", Mutate(identity, m => m with { DispatchIdentity = "foreign" })),
            // Old records must not manufacture missing historical authority from successor bytes.
            ("legacy-record", identity with { RetainedMetadataBase64 = null, CapturedDispatchAt = null })
        ];
        foreach (var control in controls)
        {
            var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot);
            kernel.RecordDispatchBaseCommit(fixture.GoalId, fixture.TaskId, dispatch.BaseCommit!, control.Identity);
            WorkerBuildRequestScopeSupport.Save(fixture, kernel);

            var result = fixture.Consume();

            Assert.True(result.MissingEvidence, control.Name + ": " + result.Diagnostic);
            Assert.StartsWith("worker_build_host=scope-invalid;", result.Diagnostic);
            WorkerBuildRequestScopeSupport.AssertUnconsumed(fixture, original);
        }
        WorkerBuildRequestScopeSupport.Save(fixture, AgentOrchestratorKernel.FromSnapshot(snapshot));
        Assert.False(fixture.Consume().MissingEvidence);
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("staged")]
    [InlineData("committed")]
    [InlineData("untracked")]
    public async Task ChangedRequiredInputs_CannotBorrowNativePass(string change)
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake, goalBranch: true);
        var original = await fixture.RunHelper();
        Assert.Equal(WorkerBuildOutcome.Success, original.Outcome);
        WorkerBuildRequestScopeSupport.PrepareSuccessor(fixture, false);
        var path = change == "untracked" ? "src/Feature/New.cs" : WorkerBuildRequestFixture.Source;
        File.AppendAllText(Path.Combine(fixture.RepositoryRoot, path), "\npublic class LaterEdit { }");
        if (change is "staged" or "committed") fixture.Git("add", path);
        if (change == "committed") fixture.Git("commit", "-m", "later candidate");

        var result = fixture.Consume();

        Assert.True(result.MissingEvidence, result.Diagnostic);
        Assert.Equal("worker_build_host=missing-current-candidate-outcome", result.Diagnostic);
        WorkerBuildRequestScopeSupport.AssertUnconsumed(fixture, original);
    }

    [Fact]
    public async Task IncompleteProjectCoverage_CannotBorrowNativePass()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake, goalBranch: true, extraProject: true);
        var original = await fixture.RunHelper();
        Assert.Equal(WorkerBuildOutcome.Success, original.Outcome);
        WorkerBuildRequestScopeSupport.PrepareSuccessor(fixture, false);
        Assert.DoesNotContain(WorkerBuildRequestFixture.OtherProject, original.Projects);
        // A completion path can add an obligation without changing the built candidate tree.
        var result = fixture.Operations.Consume(fixture.Kernel, fixture.GoalId, fixture.TaskId,
            [WorkerBuildRequestFixture.OtherSource], []);

        Assert.True(result.MissingEvidence, result.Diagnostic);
        Assert.Equal("worker_build_host=missing-current-candidate-outcome", result.Diagnostic);
        WorkerBuildRequestScopeSupport.AssertUnconsumed(fixture, original);
    }

    [Fact]
    public async Task PreLaunchSuccessorOverwrite_CannotStartCompiler()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake, goalBranch: true);
        var host = fixture.Host();
        WorkerBuildRequestScopeSupport.PrepareSuccessor(fixture, true);
        using var process = Process.GetCurrentProcess();

        var error = await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            new WorkerBuildRequest(1, "foreign-before-launch", [WorkerBuildRequestFixture.Project], "Debug"),
            WorkerBuildRequestChannel.ReadIdentity(process), CancellationToken.None));

        Assert.Contains("differs from conductor-captured dispatch identity", error.Message);
        Assert.Empty(WorkerBuildRequestScopeSupport.Load(fixture).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
        Assert.True(fixture.Consume().MissingEvidence);
    }

    [Fact]
    public async Task ConsumedPass_StillRequiresValidScopeAuthority()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake, goalBranch: true);
        Assert.Equal(WorkerBuildOutcome.Success, (await fixture.RunHelper()).Outcome);
        Assert.False(fixture.Consume().MissingEvidence);
        var consumed = WorkerBuildRequestScopeSupport.Invocation(fixture);
        var kernel = WorkerBuildRequestScopeSupport.Load(fixture);
        var dispatch = kernel.GetTask(fixture.GoalId, fixture.TaskId).LastDispatch!;
        kernel.RecordDispatchBaseCommit(fixture.GoalId, fixture.TaskId, dispatch.BaseCommit!,
            dispatch.WorkerBuildScope! with { RetainedMetadataBase64 = null });
        WorkerBuildRequestScopeSupport.Save(fixture, kernel);

        Assert.True(fixture.Consume().MissingEvidence);
        var retained = WorkerBuildRequestScopeSupport.Invocation(fixture);
        Assert.True(consumed.SameOrigin(retained));
        Assert.True(retained.Consumed);
        Assert.Equal(consumed.Version, retained.Version);
    }

    [Fact]
    public async Task ScopeOverwriteAtRegistration_PreventsResumeAndReleasesCustody()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake, goalBranch: true);
        var changed = false;
        var invocation = await fixture.RunHelper(current =>
        {
            if (current.Phase != WorkerBuildPhase.ChildRegistered || changed) return;
            changed = true;
            WorkerBuildProjectScope.Prepare(fixture.RepositoryRoot, fixture.GoalId.Value, TaskId.New().Value, "main");
        });

        Assert.True(changed);
        Assert.Equal(WorkerBuildOutcome.ApparatusFailure, invocation.Outcome);
        Assert.Single(invocation.Children!);
        Assert.DoesNotContain(invocation.Observations, observation => observation.Phase == WorkerBuildPhase.CompilerStarted);
        Assert.True(invocation.CustodyReleased);
        Assert.False(invocation.Consumed);
        Assert.True(fixture.Consume().MissingEvidence);
    }

    [Fact]
    public async Task RetiredSameTaskDispatch_CannotResurrectNativePass()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake, goalBranch: true);
        var original = await fixture.RunHelper();
        Assert.Equal(WorkerBuildOutcome.Success, original.Outcome);
        var snapshot = WorkerBuildRequestScopeSupport.Load(fixture).ExportSnapshot();
        var goal = Assert.Single(snapshot.Goals);
        var task = Assert.Single(goal.Tasks);
        // Arrange the canonical retry-ready state; retain the genuine invocation and history.
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [goal with
        {
            Tasks = [task with { Status = WorkTaskStatus.Assigned, LastProcess = null }]
        }] });
        var oldDispatch = kernel.GetTask(fixture.GoalId, fixture.TaskId).LastDispatch!;
        kernel.RecordTaskDispatch(fixture.GoalId, fixture.TaskId, oldDispatch with
        {
            DispatchedAt = oldDispatch.DispatchedAt.AddTicks(1), WorkerBuildScope = null
        });
        WorkerBuildScopeDispatchPreparation.RecordDispatchBaseCommit(kernel, fixture.GoalId,
            kernel.GetTask(fixture.GoalId, fixture.TaskId), fixture.RepositoryRoot, fixture.Git("rev-parse", "HEAD").Trim());
        var process = WorkerBuildRequestScopeSupport.Load(fixture).GetTask(fixture.GoalId, fixture.TaskId).LastProcess!;
        kernel.RecordTaskProcessStarted(fixture.GoalId, fixture.TaskId, process);
        WorkerBuildRequestScopeSupport.Save(fixture, kernel);

        Assert.Throws<UnauthorizedAccessException>(() => WorkerBuildInvocationOperations.RequireOrigin(
            kernel, fixture.Reference, original.Host, requireRunning: false));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Host());
        var result = fixture.Consume();

        Assert.True(result.MissingEvidence, result.Diagnostic);
        Assert.Equal("worker_build_host=missing-current-candidate-outcome", result.Diagnostic);
        WorkerBuildRequestScopeSupport.AssertUnconsumed(fixture, original);
        Assert.Equal(2, WorkerBuildRequestScopeSupport.Load(fixture).GetTask(fixture.GoalId, fixture.TaskId).DispatchHistory.Count);
    }

    private static WorkerBuildScopeIdentity Mutate(WorkerBuildScopeIdentity identity,
        Func<WorkerBuildProjectScope.Metadata, WorkerBuildProjectScope.Metadata> change, bool rehash = false)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(change(JsonSerializer.Deserialize<WorkerBuildProjectScope.Metadata>(
            Convert.FromBase64String(identity.RetainedMetadataBase64!))!));
        return identity with { RetainedMetadataBase64 = Convert.ToBase64String(bytes),
            MetadataDigest = rehash ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : identity.MetadataDigest };
    }
}
