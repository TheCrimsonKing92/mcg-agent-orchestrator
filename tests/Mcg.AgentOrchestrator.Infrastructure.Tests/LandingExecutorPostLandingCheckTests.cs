using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorPostLandingCheckTests
{
    [Fact]
    public void Execute_PostLandingCheckLandsAndRemainsAnswerable()
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/post-landing.txt");
            var candidate = LandingExecutorTests.ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            Assert.NotEqual(mainBefore, candidate);
            var wait = PostLandingOperatorCheckTests.Request(kernel, goal, "post-landing");

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced);
            Assert.IsNotType<LandingDecision.Escalate>(result.Decision);
            var mainAfter = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            Assert.NotEqual(mainBefore, mainAfter);
            Assert.Equal(result.MergeCommitSha, mainAfter);
            Assert.Equal(0, GitCli.Run(repo, "merge-base", "--is-ancestor", candidate, "main").ExitCode);
            Assert.Equal("goal work", LandingExecutorTests.ReadGit(repo, "show", "main:src/post-landing.txt"));
            var landingRecord = $"Landed with open post-landing operator check(s): {wait.Id.Value[..8]} " +
                $"(criterion 1: {PostLandingOperatorCheckTests.IncidentNeeded})";
            Assert.Contains(landingRecord, result.Message, StringComparison.Ordinal);
            Assert.Single(goal.Timeline.Where(entry => entry.Kind == ProgressKind.GoalPolicyDecision && entry.Message == landingRecord));
            Assert.Equal(wait.Id, Assert.Single(kernel.GetPendingHumanInput(goal.Id)).Id);
            Assert.False(wait.IsCompleted);
            Assert.Null(wait.Answer);

            kernel.SubmitHumanInput(wait.Id, "Both conductors now show the required schema-drift log", origin: HumanInputAnswerOrigin.Operator);

            Assert.True(wait.IsCompleted);
            Assert.Equal("Both conductors now show the required schema-drift log", wait.Answer);
            Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Execute_PostImplementationOperatorWaitStillHoldsLanding()
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/pre-landing.txt");
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            var wait = PostLandingOperatorCheckTests.Request(kernel, goal, "post-implementation", "Owner review of the candidate diff");

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.Contains(wait.Id.Value[..8], escalation.Reason, StringComparison.Ordinal);
            Assert.Equal(mainBefore, LandingExecutorTests.ReadGit(repo, "rev-parse", "main"));
            Assert.Equal(wait.Id, Assert.Single(kernel.GetPendingHumanInput(goal.Id)).Id);
            Assert.False(wait.IsCompleted);
            Assert.Null(wait.Answer);
            Assert.DoesNotContain(goal.Timeline, entry => entry.Kind == ProgressKind.GoalPolicyDecision &&
                entry.Message.StartsWith("Landed with open post-landing", StringComparison.Ordinal));
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }
}
