using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalWorktreeCleanupHooks)]
public sealed class GoalGitFactIndexTests
{
    [Xunit.Fact]
    public void BuildBranchQueryFailureMarksEvidenceUnavailable()
    {
        var originalRunner = GoalGitFactIndex.GitRunner;
        try
        {
            GoalGitFactIndex.GitRunner = (_, _) => new GitCli.GitResult(1, string.Empty, "git unavailable");

            var index = GoalGitFactIndex.Build(Environment.CurrentDirectory);

            Assert.False(index.IsAvailable);
        }
        finally
        {
            GoalGitFactIndex.GitRunner = originalRunner;
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(0, "- 1111111111111111111111111111111111111111\n- 2222222222222222222222222222222222222222\n", "EquivalentToMain")]
    [Xunit.InlineData(0, "- 1111111111111111111111111111111111111111\n+ 2222222222222222222222222222222222222222\n", "AbsentFromMain")]
    [Xunit.InlineData(1, "", "Inconclusive")]
    [Xunit.InlineData(0, "", "NoCommitsAhead")]
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

    [Xunit.Fact]
    public void BuildGoalBranchFacts_WithoutMainSha_IsInconclusiveWithoutRunningCherry()
    {
        var originalRunner = GoalGitFactIndex.GitRunner;
        var goal = CreateGoalWithStatus("Main ref is unavailable", GoalStatus.Verified);
        var branch = GoalWorktrees.BranchName(goal.Id);
        var index = new GoalGitFactIndex(
            Environment.CurrentDirectory,
            isGitWorkTree: true,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [branch] = "1111111111111111111111111111111111111111"
            },
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            mainSha: null);
        try
        {
            GoalGitFactIndex.GitRunner = (_, _) => throw new Xunit.Sdk.XunitException("git cherry must not run without a main SHA");

            var facts = index.BuildGoalBranchFacts(goal);

            Assert.Equal(GoalBranchContentState.Inconclusive, facts.ContentState);
        }
        finally
        {
            GoalGitFactIndex.GitRunner = originalRunner;
        }
    }

    [Xunit.Fact]
    public void BuildReplacementFacts_BranchAtMainTipIsNotLanded()
    {
        var originalRunner = GoalGitFactIndex.GitRunner;
        var goal = CreateGoalWithStatus("Zero-commit replacement branch", GoalStatus.Cancelled);
        var branch = GoalWorktrees.BranchName(goal.Id);
        var mainSha = "1111111111111111111111111111111111111111";
        var index = new GoalGitFactIndex(
            Environment.CurrentDirectory,
            isGitWorkTree: true,
            new Dictionary<string, string>(StringComparer.Ordinal) { [branch] = mainSha },
            new HashSet<string>(StringComparer.Ordinal) { branch },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            mainSha);
        try
        {
            GoalGitFactIndex.GitRunner = (_, args) =>
                string.Join(" ", args).StartsWith("cherry ", StringComparison.Ordinal)
                    ? new GitCli.GitResult(0, string.Empty, string.Empty)
                    : new GitCli.GitResult(1, string.Empty, "unexpected git command");

            var facts = index.BuildReplacementFacts(goal);

            Assert.False(facts.BranchAlreadyLanded);
            Assert.Equal(GoalBranchContentState.NoCommitsAhead, facts.ContentState);
        }
        finally
        {
            GoalGitFactIndex.GitRunner = originalRunner;
        }
    }

    [Xunit.Fact]
    public void BuildReplacementFacts_ZeroCommitBranchAtOlderMainAncestorIsNotLanded()
    {
        var originalRunner = GoalGitFactIndex.GitRunner;
        var goal = CreateGoalWithStatus("Historical zero-commit replacement branch", GoalStatus.Failed);
        var branch = GoalWorktrees.BranchName(goal.Id);
        var branchSha = "1111111111111111111111111111111111111111";
        var mainSha = "2222222222222222222222222222222222222222";
        var index = new GoalGitFactIndex(
            Environment.CurrentDirectory,
            isGitWorkTree: true,
            new Dictionary<string, string>(StringComparer.Ordinal) { [branch] = branchSha },
            new HashSet<string>(StringComparer.Ordinal) { branch },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            mainSha);
        try
        {
            GoalGitFactIndex.GitRunner = (_, args) =>
                string.Join(" ", args).StartsWith("cherry ", StringComparison.Ordinal)
                    ? new GitCli.GitResult(0, string.Empty, string.Empty)
                    : new GitCli.GitResult(1, string.Empty, "unexpected git command");

            var facts = index.BuildReplacementFacts(goal);

            Assert.False(facts.BranchAlreadyLanded);
            Assert.Equal(GoalBranchContentState.NoCommitsAhead, facts.ContentState);
        }
        finally
        {
            GoalGitFactIndex.GitRunner = originalRunner;
        }
    }

    [Xunit.Fact]
    public void ReplacementEvidence_InconclusiveBranchContent_FailsClosedWithoutInventingDelta()
    {
        var originalRunner = GoalGitFactIndex.GitRunner;
        var root = Path.Combine(Path.GetTempPath(), $"mcg-goal-replacement-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var goal = CreateGoalWithStatus("Inconclusive replacement evidence", GoalStatus.Cancelled);
        var branch = GoalWorktrees.BranchName(goal.Id);
        try
        {
            GoalGitFactIndex.GitRunner = (_, args) => string.Join(" ", args) switch
            {
                "for-each-ref --format=%(refname:short) %(objectname) refs/heads/goal/" =>
                    new GitCli.GitResult(0, $"{branch} 1111111111111111111111111111111111111111\n", string.Empty),
                "for-each-ref --format=%(refname:short) --merged HEAD refs/heads/goal/" =>
                    new GitCli.GitResult(0, string.Empty, string.Empty),
                "worktree list --porcelain" => new GitCli.GitResult(0, string.Empty, string.Empty),
                "rev-parse --verify refs/heads/main" =>
                    new GitCli.GitResult(0, "2222222222222222222222222222222222222222\n", string.Empty),
                var command when command.StartsWith("cherry ", StringComparison.Ordinal) =>
                    new GitCli.GitResult(1, string.Empty, "classification unavailable"),
                var command => new GitCli.GitResult(1, string.Empty, $"unexpected git command: {command}")
            };

            var facts = GoalReplacementEvidence.Capture(workspace, goal);

            Assert.False(facts.IsGitEvidenceAvailable);
            Assert.False(facts.HasRepositoryDelta);
            Assert.Equal(
                GoalReplacementOutcome.ProtectedOwner,
                SourceBacklogClaimEligibility.Evaluate(GoalReplacementDisposition.ZeroWorkCorrection, facts));
        }
        finally
        {
            GoalGitFactIndex.GitRunner = originalRunner;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void BuildGoalEvidenceKey_DoesNotProbeBranchContentThatCannotBeCached()
    {
        var originalRunner = GoalGitFactIndex.GitRunner;
        var goal = CreateGoalWithStatus("Evidence key stays cheap", GoalStatus.Completed);
        var branch = GoalWorktrees.BranchName(goal.Id);
        var index = new GoalGitFactIndex(
            Environment.CurrentDirectory,
            isGitWorkTree: true,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [branch] = "2222222222222222222222222222222222222222"
            },
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            mainSha: "3333333333333333333333333333333333333333");
        try
        {
            GoalGitFactIndex.GitRunner = (_, _) => throw new Xunit.Sdk.XunitException("evidence-key construction must not run git cherry");

            var key = index.BuildGoalEvidenceKey(goal);

            Assert.DoesNotContain("main=", key, StringComparison.Ordinal);
            Assert.DoesNotContain("content=", key, StringComparison.Ordinal);
        }
        finally
        {
            GoalGitFactIndex.GitRunner = originalRunner;
        }
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

    private static Goal CreateGoalWithStatus(string objective, GoalStatus status)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            objective,
            [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals
                .Select(item => item.Id == goal.Id.Value ? item with { Status = status } : item)
                .ToArray()
        });
        return kernel.GetGoal(goal.Id);
    }
}
