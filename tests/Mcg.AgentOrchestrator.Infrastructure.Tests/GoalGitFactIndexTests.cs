using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalWorktreeCleanupHooks)]
public sealed class GoalGitFactIndexTests
{
    [Xunit.Theory]
    [Xunit.InlineData(0, "- 1111111111111111111111111111111111111111\n- 2222222222222222222222222222222222222222\n", "EquivalentToMain")]
    [Xunit.InlineData(0, "- 1111111111111111111111111111111111111111\n+ 2222222222222222222222222222222222222222\n", "AbsentFromMain")]
    [Xunit.InlineData(1, "", "Inconclusive")]
    [Xunit.InlineData(0, "", "Inconclusive")]
    [Xunit.InlineData(0, "unexpected output", "Inconclusive")]
    [Xunit.InlineData(0, "- not-an-object-id", "Inconclusive")]
    public void ClassifyCherryResult_Output_ReturnsExpectedState(
        int exitCode,
        string output,
        string expected)
    {
        var result = GoalGitFactIndex.ClassifyCherryResult(new GitCli.GitResult(exitCode, output, string.Empty));

        Assert.Equal(expected, result.ToString());
    }

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

    [Xunit.Fact(DisplayName = "GoalIntegrationEvidenceResolver_subject_without_main_ancestry_is_not_landed")]
    public void GoalIntegrationEvidenceResolverSubjectWithoutMainAncestryIsNotLanded()
    {
        var originalRunner = GoalIntegrationEvidenceResolver.GitRunner;
        var goalId = new GoalId("aaaaaaaa111111111111111111111111");
        try
        {
            GoalIntegrationEvidenceResolver.GitRunner = (_, args) =>
            {
                var command = string.Join(" ", args);
                return command switch
                {
                    "log main-sha --format=%H%x09%s --grep=^Integrate goal/" =>
                        new GitCli.GitResult(0, $"discarded-sha\tIntegrate goal/{goalId.Value[..8]}\n", string.Empty),
                    "merge-base --is-ancestor discarded-sha main-sha" =>
                        new GitCli.GitResult(1, string.Empty, string.Empty),
                    _ => new GitCli.GitResult(1, string.Empty, $"unexpected git command: {command}")
                };
            };

            var resolver = GoalIntegrationEvidenceResolver.Build(Environment.CurrentDirectory, "main-sha");

            Assert.False(resolver.TryResolve(goalId, out var evidence));
            Assert.Null(evidence);
        }
        finally
        {
            GoalIntegrationEvidenceResolver.GitRunner = originalRunner;
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
