using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalWorktreeCleanupHooks)]
public sealed class GoalGitFactIndexTests
{
    [Xunit.Fact(DisplayName = "GoalGitFactIndex_parse_branch_tips_keeps_present_and_ignores_malformed_lines")]
    public void GoalGitFactIndexParseBranchTipsKeepsPresentAndIgnoresMalformedLines()
    {
        var tips = GoalGitFactIndex.ParseBranchTips(
            """
            goal/present 1111111111111111111111111111111111111111
            goal/missing-object
            goal/blank-object
            goal/extra-object 2222222222222222222222222222222222222222 trailing
            goal/also-present abcdefabcdefabcdefabcdefabcdefabcdefabcd
            """);

        Assert.Equal("1111111111111111111111111111111111111111", tips["goal/present"]);
        Assert.Equal("abcdefabcdefabcdefabcdefabcdefabcdefabcd", tips["goal/also-present"]);
        Assert.False(tips.ContainsKey("goal/missing-object"));
        Assert.False(tips.ContainsKey("goal/blank-object"));
        Assert.False(tips.ContainsKey("goal/extra-object"));
    }

    [Xunit.Fact(DisplayName = "GoalGitFactIndex_main_ancestry_requires_positive_landing_path_evidence")]
    public void GoalGitFactIndexMainAncestryRequiresPositiveLandingPathEvidence()
    {
        var originalRunner = GoalGitFactIndex.GitRunner;
        try
        {
            GoalGitFactIndex.GitRunner = (_, args) =>
            {
                var command = string.Join(" ", args);
                return command switch
                {
                    "for-each-ref --format=%(refname:short) %(objectname) refs/heads/goal/" =>
                        new GitCli.GitResult(0, "goal/empty aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n", string.Empty),
                    "for-each-ref --format=%(refname:short) --merged HEAD refs/heads/goal/" =>
                        new GitCli.GitResult(0, "goal/empty\n", string.Empty),
                    "worktree list --porcelain" => new GitCli.GitResult(0, string.Empty, string.Empty),
                    "rev-parse --verify refs/heads/main" =>
                        new GitCli.GitResult(0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n", string.Empty),
                    "merge-base --is-ancestor aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" =>
                        new GitCli.GitResult(0, string.Empty, string.Empty),
                    "log --format=%H -n 1 --ancestry-path aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa..aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" =>
                        new GitCli.GitResult(0, string.Empty, string.Empty),
                    _ => new GitCli.GitResult(1, string.Empty, $"unexpected git command: {command}")
                };
            };

            var index = GoalGitFactIndex.Build(Environment.CurrentDirectory);

            Assert.False(index.TryResolveMainAncestry(new GoalId("empty"), out var ancestry));
            Assert.Null(ancestry);
        }
        finally
        {
            GoalGitFactIndex.GitRunner = originalRunner;
        }
    }

    [Xunit.Fact(DisplayName = "GoalGitFactIndex_build_reports_present_and_missing_goal_branches_from_batch_query")]
    public void GoalGitFactIndexBuildReportsPresentAndMissingGoalBranchesFromBatchQuery()
    {
        var originalRunner = GoalGitFactIndex.GitRunner;
        try
        {
            GoalGitFactIndex.GitRunner = (_, args) =>
            {
                var command = string.Join(" ", args);
                return command switch
                {
                    "for-each-ref --format=%(refname:short) %(objectname) refs/heads/goal/" =>
                        new GitCli.GitResult(
                            0,
                            "goal/present 1111111111111111111111111111111111111111\ngoal/malformed 222 trailing\n",
                            string.Empty),
                    "for-each-ref --format=%(refname:short) --merged HEAD refs/heads/goal/" =>
                        new GitCli.GitResult(0, string.Empty, string.Empty),
                    "worktree list --porcelain" =>
                        new GitCli.GitResult(0, string.Empty, string.Empty),
                    _ => new GitCli.GitResult(1, string.Empty, "unexpected git command")
                };
            };

            var index = GoalGitFactIndex.Build(Environment.CurrentDirectory);

            Assert.True(index.HasGoalBranch("goal/present"));
            Assert.False(index.HasGoalBranch("goal/missing"));
            Assert.False(index.HasGoalBranch("goal/malformed"));
        }
        finally
        {
            GoalGitFactIndex.GitRunner = originalRunner;
        }
    }
}
