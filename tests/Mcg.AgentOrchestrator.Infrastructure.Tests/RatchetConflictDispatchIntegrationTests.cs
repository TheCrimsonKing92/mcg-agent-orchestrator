using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;
using static ConductorDriverTests;

// Parallel-safe: each case owns its kernel/repository; build execution is injected.
public sealed class RatchetConflictDispatchIntegrationTests
{
    [Fact]
    public void RatchetOnlyConflict_BuildsMergedHeadAndAllowsDispatch()
    {
        var (kernel, goal) = SimpleGoal();
        using var fixture = new AdditiveConflictGitFixture(RatchetFiles(), goal.Id);
        var calls = new List<(string Path, string Head)>();
        var integration = new RatchetConflictDispatchIntegration(kernel, fixture.Repository, TrunkBranchName.Default,
            Path.Combine(fixture.Repository, ".orchestrator/logs/conduct-events.log"), path =>
            {
                calls.Add((path, Git(path, "rev-parse", "HEAD").Trim()));
                return new(true, 0, "build passes");
            });

        var result = integration.IntegrateMainBeforeDeveloperDispatch(goal);

        Assert.Equal(DeveloperBranchIntegrationStatus.Integrated, result.Status);
        Assert.True(result.CanDispatch);
        Assert.Equal(fixture.OriginalHead, result.OriginalCandidateSha);
        Assert.Equal(fixture.MainHead, result.IntegratedMainSha);
        Assert.NotEqual(result.OriginalCandidateSha, result.ResultingCandidateSha);
        var call = Assert.Single(calls);
        Assert.Equal(Path.GetFullPath(fixture.Worktree), Path.GetFullPath(call.Path));
        Assert.Equal(result.ResultingCandidateSha, call.Head);
        Assert.Equal(new[] { call.Head, fixture.OriginalHead, fixture.MainHead },
            Git(fixture.Worktree, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' '));
        Assert.Empty(Git(fixture.Worktree, "status", "--porcelain=v1"));
    }

    [Fact]
    public void FailedBuild_RestoresOriginalHeadAndIncludesBuildOutput()
    {
        var (kernel, goal) = SimpleGoal();
        using var fixture = new AdditiveConflictGitFixture(RatchetFiles(), goal.Id);
        var calls = 0;
        var integration = new RatchetConflictDispatchIntegration(kernel, fixture.Repository, TrunkBranchName.Default,
            Path.Combine(fixture.Repository, ".orchestrator/logs/conduct-events.log"), _ =>
            {
                calls++;
                Assert.NotEqual(fixture.OriginalHead, Git(fixture.Worktree, "rev-parse", "HEAD").Trim());
                return new(true, 1, "CS1002 sentinel");
            });

        var result = integration.IntegrateMainBeforeDeveloperDispatch(goal);

        Assert.Equal(DeveloperBranchIntegrationStatus.Conflict, result.Status);
        Assert.Contains("CS1002 sentinel", result.Message);
        Assert.Equal(1, calls);
        fixture.AssertRestored();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OtherConflictOrAbsentClass_PreservesOriginalConflict(bool extra, bool absent)
    {
        var (kernel, goal) = SimpleGoal();
        using var fixture = new AdditiveConflictGitFixture(RatchetFiles(extra, absent), goal.Id);
        var original = new FailedRoundCheckpointPreDispatch(kernel, fixture.Repository, TrunkBranchName.Default)
            .IntegrateMainBeforeDeveloperDispatch(goal);
        Assert.Equal(DeveloperBranchIntegrationStatus.Conflict, original.Status);
        var calls = 0;
        var integration = new RatchetConflictDispatchIntegration(kernel, fixture.Repository, TrunkBranchName.Default,
            Path.Combine(fixture.Repository, ".orchestrator/logs/conduct-events.log"), _ =>
            { calls++; return new(true, 0, "unexpected"); });

        var result = integration.IntegrateMainBeforeDeveloperDispatch(goal);

        Assert.Equal(original.Status, result.Status);
        Assert.Equal(original.Message, result.Message);
        Assert.Equal(original.ConflictPaths, result.ConflictPaths);
        Assert.Equal(0, calls);
        fixture.AssertRestored();
    }

    [Fact]
    public void FailedBuildWithSuccessorHead_DoesNotOverwriteSuccessor()
    {
        var (kernel, goal) = SimpleGoal();
        using var fixture = new AdditiveConflictGitFixture(RatchetFiles(), goal.Id);
        string? successor = null;
        var integration = new RatchetConflictDispatchIntegration(kernel, fixture.Repository, TrunkBranchName.Default,
            Path.Combine(fixture.Repository, ".orchestrator/logs/conduct-events.log"), path =>
            {
                Git(path, "commit", "--allow-empty", "-m", "concurrent successor");
                successor = Git(path, "rev-parse", "HEAD").Trim();
                return new(true, 1, "build failed");
            });

        var result = integration.IntegrateMainBeforeDeveloperDispatch(goal);

        Assert.Equal(DeveloperBranchIntegrationStatus.Conflict, result.Status);
        Assert.Contains("Compare-and-swap reset did not restore", result.Message);
        Assert.NotNull(successor);
        Assert.Equal(successor, Git(fixture.Worktree, "rev-parse", "HEAD").Trim());
        Assert.Empty(Git(fixture.Worktree, "status", "--porcelain=v1"));
    }
}
