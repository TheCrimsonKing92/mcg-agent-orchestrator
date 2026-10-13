using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: unique fixture roots, no live children or ambient environment edits.
public sealed class CompletionBuildEvidenceIdentityTests
{
    [Fact]
    public void RepeatedPollsAndRestartLaunchOnceAndApplyMatchingResultOnce()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var runner = fixture.Runner();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        fixture.Reload();
        runner = fixture.Runner();
        for (var i = 0; i < 3; i++) runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal(1, fixture.Authorizations);
        Assert.Equal(operation.Id, fixture.Task.CompletionBuildEvidence!.Id);
        Assert.Equal(fixture.GoalId.Value, operation.GoalId);
        Assert.Equal(fixture.TaskId.Value, operation.TaskId);
        Assert.Equal(fixture.Task.LastDispatch!.DispatchedAt, operation.DispatchAt);
        Assert.Equal(["src/Feature/Feature.csproj"], operation.Projects);
        Assert.Null(fixture.Task.LastVerification);
        fixture.Release();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var commit = fixture.Task.LastDispatch!.ResultCommit;
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.NotNull(commit);
        Assert.Equal(commit, fixture.Task.LastDispatch.ResultCommit);
        Assert.Single(fixture.Task.VerificationHistory);
        Assert.True(fixture.Task.CompletionBuildEvidence!.Applied);
    }

    [Fact]
    public void ChangedTreeCannotUseSuccessEvenWhenNewReceiptMatchesCurrentTree()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var runner = fixture.Runner();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        fixture.Release();
        File.AppendAllText(Path.Combine(fixture.Task.LastProcess!.WorkingDirectory, "src/Feature/Feature.cs"), "\nclass LaterEdit { }");
        fixture.WriteReceipt(["src/Feature/Feature.csproj"]);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.StaleCandidate, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrIncompleteProjectCoverageCannotCertifyTask(bool incomplete)
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var runner = fixture.Runner();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        fixture.Release(receipt: false);
        if (incomplete) fixture.WriteReceipt(["another/project.csproj"]);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        Assert.False(fixture.Task.LastVerification!.Succeeded);
    }

    [Fact]
    public void ForeignDispatchAndOperationResultDoNotMatch()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        Assert.False(CompletionBuildEvidenceCoordinator.MatchesResult(operation, operation with { DispatchId = "foreign" }));
        Assert.False(CompletionBuildEvidenceCoordinator.MatchesResult(operation, operation with { Id = "foreign" }));
        Assert.False(CompletionBuildEvidenceCoordinator.MatchesResult(operation, operation with { Projects = ["foreign.csproj"] }));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("malformed")]
    [InlineData("nonzero-success")]
    [InlineData("null-worker-process")]
    [InlineData("null-projects")]
    public void InvalidTerminalReceiptCannotResumeCompletion(string kind)
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var runner = fixture.Runner();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        fixture.Release();
        var operation = fixture.Task.CompletionBuildEvidence!;
        File.WriteAllText(operation.ResultPath, kind == "malformed" ? "{broken" :
            System.Text.Json.JsonSerializer.Serialize(operation with
            {
                Id = kind == "foreign" ? "another-operation" : operation.Id,
                Phase = CompletionBuildEvidencePhase.Success, BuildExitCode = kind == "nonzero-success" ? 1 : 0,
                WorkerProcess = kind == "null-worker-process" ? null! : operation.WorkerProcess,
                Projects = kind == "null-projects" ? null! : operation.Projects
            }));
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        Assert.False(fixture.Task.LastVerification!.Succeeded);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(1, fixture.Launches);
        Assert.Single(fixture.Task.VerificationHistory);
    }
}
