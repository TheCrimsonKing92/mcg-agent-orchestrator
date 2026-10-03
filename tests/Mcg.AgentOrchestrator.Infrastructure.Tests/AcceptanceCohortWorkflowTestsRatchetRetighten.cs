using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceCohortWorkflowTestsRatchetRetighten : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void CohortRetightensBeforeIdentityAndLeavesMembersUnchanged()
    {
        var scenario = GoalWorktreeTestsRebaseRatchetRetighten.CreateScenario();
        var previous = SourceSizeRatchetRetightener.ProgressSink;
        var progress = new List<string>();
        try
        {
            SourceSizeRatchetRetightener.ProgressSink = progress.Add;
            var second = CreateCandidate(scenario.Repo, "22222222222222222222222222222222", "src/Other.cs", "unrelated");
            var members = new[]
            {
                Bind(scenario.Goal, scenario.OldHead, GoalWorktreeTestsRebaseRatchetRetighten.GuardedPath, "guarded"),
                Bind(second.GoalId, second.Revision, "src/Other.cs", "other")
            };
            string tree;
            using (var workspace = GoalWorktrees.CreateAcceptanceCohortWorkspace(scenario.Repo, scenario.Main, members))
            {
                GoalWorktreeTestsRebaseRatchetRetighten.AssertRetightened(workspace.Path);
                tree = workspace.TreeRevision;
                Assert.Equal(RunGitOutput(workspace.Path, "rev-parse", "HEAD^{tree}").Trim(), tree);
                Assert.Equal(RunGitOutput(workspace.Path, "rev-parse", "HEAD").Trim(), workspace.CommitRevision);
                Assert.Equal("unrelated", File.ReadAllText(Path.Combine(workspace.Path, "src/Other.cs")));
                workspace.AssertGoalBranchesUnchanged();
            }
            using var again = GoalWorktrees.CreateAcceptanceCohortWorkspace(scenario.Repo, scenario.Main, members);
            Assert.Equal(tree, again.TreeRevision);
            GoalWorktreeTestsRebaseRatchetRetighten.AssertRetightened(again.Path);
            Assert.Equal(new[]
            {
                "RATCHET_RETIGHTEN goal=cohort:11111111+22222222 path=src/Guarded.cs old=15 new=18",
                "RATCHET_RETIGHTEN goal=cohort:11111111+22222222 path=src/Guarded.cs old=15 new=18"
            }, progress);
        }
        finally
        {
            SourceSizeRatchetRetightener.ProgressSink = previous;
            DeleteDirectory(scenario.Repo);
        }
    }

    [Fact]
    public void PartitionRetightensBeforeIdentity()
    {
        var scenario = GoalWorktreeTestsRebaseRatchetRetighten.CreateScenario();
        try
        {
            var member = Bind(scenario.Goal, scenario.OldHead, GoalWorktreeTestsRebaseRatchetRetighten.GuardedPath, "guarded");
            using var workspace = GoalWorktrees.CreateAcceptancePartitionWorkspace(scenario.Repo, scenario.Main, member);
            GoalWorktreeTestsRebaseRatchetRetighten.AssertRetightened(workspace.Path);
            Assert.Equal(RunGitOutput(workspace.Path, "rev-parse", "HEAD^{tree}").Trim(), workspace.TreeRevision);
            Assert.Equal(RunGitOutput(workspace.Path, "rev-parse", "HEAD").Trim(), workspace.CommitRevision);
            workspace.AssertGoalBranchesUnchanged();
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    [Fact]
    public void TrainRetightensBeforeIdentityWithSuppliedCommitDate()
    {
        var scenario = GoalWorktreeTestsRebaseRatchetRetighten.CreateScenario();
        var previous = SourceSizeRatchetRetightener.ProgressSink;
        var progress = new List<string>();
        try
        {
            SourceSizeRatchetRetightener.ProgressSink = progress.Add;
            var second = CreateCandidate(scenario.Repo, "22222222222222222222222222222222", "src/Other.cs", "unrelated");
            var members = new[]
            {
                TrainBind(scenario.Goal, scenario.OldHead, GoalWorktreeTestsRebaseRatchetRetighten.GuardedPath, "guarded"),
                TrainBind(second.GoalId, second.Revision, "src/Other.cs", "other")
            };
            var date = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            string tree;
            string commit;
            string identity;
            using (var workspace = GoalWorktrees.CreateMergeTrainWorkspace(scenario.Repo, scenario.Main, members, committerDate: date))
            {
                GoalWorktreeTestsRebaseRatchetRetighten.AssertRetightened(workspace.Path);
                Assert.Empty(workspace.Ejections);
                Assert.Equal(2, workspace.Members.Count);
                Assert.Equal(date.ToUnixTimeSeconds().ToString(), RunGitOutput(workspace.Path, "log", "-1", "--format=%ct").Trim());
                Assert.Equal(date.ToUnixTimeSeconds().ToString(), RunGitOutput(workspace.Path, "log", "-1", "--format=%at").Trim());
                tree = workspace.TreeRevision;
                commit = workspace.CommitRevision;
                identity = MergeTrainIdentity.Create(workspace.Members, scenario.Main, tree, "manifest-v1").Value;
                workspace.AssertGoalBranchesUnchanged();
            }
            using var again = GoalWorktrees.CreateMergeTrainWorkspace(scenario.Repo, scenario.Main, members, committerDate: date.AddSeconds(10));
            GoalWorktreeTestsRebaseRatchetRetighten.AssertRetightened(again.Path);
            Assert.Equal(tree, again.TreeRevision);
            Assert.NotEqual(commit, again.CommitRevision);
            Assert.Equal(identity, MergeTrainIdentity.Create(again.Members, scenario.Main, again.TreeRevision, "manifest-v1").Value);
            Assert.Equal(2, progress.Count);
            Assert.All(progress, line => Assert.Equal(
                "RATCHET_RETIGHTEN goal=train:11111111+22222222 path=src/Guarded.cs old=15 new=18", line));
        }
        finally
        {
            SourceSizeRatchetRetightener.ProgressSink = previous;
            DeleteDirectory(scenario.Repo);
        }
    }
}
