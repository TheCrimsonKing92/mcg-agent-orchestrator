using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static LandingExecutorTests;

[Xunit.Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorTestsIntegrationMergeBody
{
    [Xunit.Theory]
    [Xunit.InlineData("# Readable landing merges\nDetails", "Goal: Readable landing merges")]
    [Xunit.InlineData("Fix Integrate goal/abc handling", "")]
    public void LandingMergePreservesSubjectAndWritesSafeTitleBody(string objective, string expectedBody)
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(objective, [new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id, task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            var branch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, branch, "src/feature.txt", "goal work");
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            GoalOperationJournal.AcceptancePassed(
                repo, goal, "conductor:acceptance",
                ReadGit(worktree, "rev-parse", "HEAD"),
                ReadGit(repo, "rev-parse", "main"),
                "passing acceptance for the candidate",
                DateTimeOffset.UtcNow.AddMinutes(-1));

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Xunit.Assert.True(result.MainAdvanced, result.Message);
            Xunit.Assert.Equal($"Integrate {branch}", ReadGit(repo, "log", "main", "-1", "--format=%s"));
            Xunit.Assert.Equal(expectedBody, ReadGit(repo, "log", "main", "-1", "--format=%b"));
            var mergeSha = ReadGit(repo, "rev-parse", "main");
            Xunit.Assert.True(GoalIntegrationEvidenceResolver.Build(repo).TryResolve(goal.Id, out var evidence));
            Xunit.Assert.Equal(mergeSha, evidence!.IntegrateSha);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }
}
