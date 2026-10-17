using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;
using static ConductorConflictIntegrationRecordTests;

// Parallel-safe: all git state and kernel state belong to each fixture.
public sealed class ConductorConflictIntegrationAdmissionTests
{
    [Fact]
    public void Dispatch_MatchingRound_StartsDiff3MergeAndAdmitsIdempotently()
    {
        using var fixture = new Fixture();
        fixture.Admit();
        var git = fixture.GitFixture;
        Assert.Equal(git.MainHead, Git(git.Worktree, "rev-parse", "MERGE_HEAD").Trim());
        Assert.Equal(git.OriginalHead, Git(git.Worktree, "rev-parse", "HEAD").Trim());
        Assert.Equal(WidgetPath, Git(git.Worktree, "diff", "--name-only", "--diff-filter=U").Trim());
        Assert.Contains("<<<<<<< ", git.Read());
        Assert.Contains("||||||| ", git.Read());
        Assert.Contains("=======", git.Read());
        Assert.Contains(">>>>>>> ", git.Read());
        Assert.True(ConductorConflictIntegrationMarker.Admits(fixture.Developer, git.Worktree));
        var calls = fixture.InnerCalls;
        // Recovery after a crash between merge and marker must also be idempotent.
        ConductorConflictIntegrationMarker.Remove(git.Worktree);
        var result = fixture.Coordinator.IntegrateMainBeforeDeveloperDispatch(fixture.Goal);
        Assert.Equal(DeveloperBranchIntegrationStatus.ConflictAdmitted, result.Status);
        Assert.True(result.CanDispatch);
        Assert.Equal(git.OriginalHead, result.OriginalCandidateSha);
        Assert.Equal(git.MainHead, result.IntegratedMainSha);
        Assert.Equal(calls, fixture.InnerCalls);
        Assert.True(ConductorConflictIntegrationMarker.Admits(fixture.Developer, git.Worktree));
    }

    [Theory]
    [InlineData("no record")]
    [InlineData("wrong head")]
    [InlineData("wrong cause")]
    [InlineData("wrong paths")]
    [InlineData("failed")]
    [InlineData("wrong retry time")]
    [InlineData("moved main")]
    public void Dispatch_NonMatchingRound_ReturnsOriginalGuardResult(string kind)
    {
        using var fixture = new Fixture();
        if (kind != "no record") Assert.True(fixture.Coordinator.TryRecord(fixture.Goal).Started);
        if (kind is "wrong head" or "wrong paths" or "failed" or "wrong retry time")
        {
            fixture.RewriteRound(root => new
            {
                OriginalHead = kind == "wrong head" ? new string('a', 40) : fixture.GitFixture.OriginalHead,
                MainSha = fixture.GitFixture.MainHead,
                ConflictPaths = kind == "wrong paths" ? new[] { WidgetPath, "src/Other.cs" } : new[] { WidgetPath },
                DeveloperTaskId = fixture.Developer.Id.Value,
                LandingGoalPrefixes = new[] { "aaaaaaaa" },
                RetryAt = kind == "wrong retry time" ? fixture.Developer.LatestRetryAt!.Value.AddTicks(1) : root.GetProperty("RetryAt").GetDateTimeOffset(),
                Admitted = false
            }, failed: kind == "failed");
        }
        if (kind == "wrong cause") fixture.Kernel.RetryTaskAutomatically(fixture.Goal.Id, fixture.Developer.Id, "new source",
            RetryCause.NewSourceFinding, authoritativeRetryFeedback: true);
        if (kind == "moved main")
        {
            File.WriteAllText(Path.Combine(fixture.GitFixture.Repository, "src/Other.cs"), "new main\n");
            Commit(fixture.GitFixture.Repository, "Main moved");
        }
        var result = fixture.Coordinator.IntegrateMainBeforeDeveloperDispatch(fixture.Goal);
        Assert.Same(fixture.InnerResult, result);
        Assert.Equal(DeveloperBranchIntegrationStatus.Conflict, result.Status);
        Assert.False(result.CanDispatch);
        Assert.Equal(GuardText(fixture.Goal), result.Message);
        fixture.GitFixture.AssertRestored();
    }

    internal static string GuardText(Goal goal) => $"Developer dispatch blocked: main conflicts with {GoalWorktrees.BranchName(goal.Id)} in src/Widget.cs. Conflict resolution requires semantic ownership and must be performed by the conductor or operator; do not instruct a worker to rebase or resolve the branch integration.";
}
