using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns its repository, refs, worktrees and cleanup hooks.
public sealed class MainIntegrationSquashTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void IntegratedMergeCommitMember_TreeAndIdentityAreStableAcrossMaterializations()
    {
        using var scenario = new Repository();
        var refs = RunGitOutput(scenario.Root, "for-each-ref");
        var date = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string tree, commit, identity;
        using (var first = scenario.Compose(date))
        {
            AssertMembers(scenario, first);
            tree = first.TreeRevision;
            commit = first.CommitRevision;
            identity = MergeTrainIdentity.Create(first.Members, scenario.Main, tree, "manifest-v1").Value;
            AssertResolvedLinearTree(scenario, first);
        }
        using (var second = scenario.Compose(date.AddSeconds(10)))
        {
            AssertMembers(scenario, second);
            Assert.Equal(tree, second.TreeRevision);
            Assert.NotEqual(commit, second.CommitRevision);
            Assert.Equal(identity, MergeTrainIdentity.Create(
                second.Members, scenario.Main, second.TreeRevision, "manifest-v1").Value);
            AssertResolvedLinearTree(scenario, second);
        }
        foreach (var member in scenario.Members)
            Assert.Equal(member.BranchRevision, RunGitOutput(scenario.Root,
                "rev-parse", GoalWorktrees.BranchName(member.GoalId)));
        Assert.Equal(refs, RunGitOutput(scenario.Root, "for-each-ref"));
    }

    [Fact]
    public void UnresolvedConflictAgainstMain_StillEjectsWithRebaseConflict()
    {
        using var scenario = new Repository(integrate: false);
        using var train = scenario.Compose();
        AssertConflictAndSurvivor(scenario, train);
    }

    [Fact]
    public void StaleIntegrationThenMainMovesAgain_StillEjects()
    {
        using var scenario = new Repository(stale: true);
        using var train = scenario.Compose();
        AssertConflictAndSurvivor(scenario, train);
    }

    [Fact]
    public void LinearBranch_TakesTheUnchangedRebasePath()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main");
            var first = GoalId.New();
            _ = CreateWorktreeCandidate(repo, first, "first.txt", "one\n");
            var revision = CreateWorktreeCandidate(repo, first, "first.txt", "two\n");
            var other = GoalId.New();
            var otherRevision = CreateWorktreeCandidate(repo, other, "other.txt", "other\n");
            Assert.Null(MainIntegrationSquash.TryCreate(repo, main, main, revision, first,
                _ => throw new InvalidOperationException("Linear branches must not call commit-tree")));
            using var train = GoalWorktrees.CreateMergeTrainWorkspace(repo, main,
                [TrainBind(first, revision, "first.txt", "first"),
                 TrainBind(other, otherRevision, "other.txt", "other")], TrunkBranchName.Default);
            Assert.Empty(train.Ejections);
            Assert.Equal(new[] { first, other }, train.Members.Select(member => member.GoalId));
            Assert.Equal("2", RunGitOutput(repo, "rev-list", "--count",
                $"{main}..{train.Members[0].RebasedTrainHeadRevision}"));
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void TryCreate_UpdatesNoRefAndNoWorktree()
    {
        using var scenario = new Repository();
        var refs = RunGitOutput(scenario.Root, "for-each-ref");
        var worktrees = RunGitOutput(scenario.Root, "worktree", "list", "--porcelain");
        var status = RunGitOutput(scenario.MemberWorktree, "status", "--porcelain");
        var member = scenario.Members[0];
        var mergeBase = RunGitOutput(scenario.Root, "merge-base", scenario.Main, member.CandidateRevision);
        string[]? arguments = null;
        var sha = MainIntegrationSquash.TryCreate(scenario.Root, scenario.Main, mergeBase,
            member.CandidateRevision, member.GoalId, args =>
            {
                arguments = args;
                return GitCli.Run(scenario.Root, args);
            });
        Assert.NotNull(sha);
        Assert.NotNull(arguments);
        var tree = RunGitOutput(scenario.Root, "rev-parse", $"{member.CandidateRevision}^{{tree}}");
        Assert.Equal(new[] { "-c", "user.name=mcg-orchestrator", "-c",
            "user.email=mcg-orchestrator@localhost", "commit-tree", tree, "-p", mergeBase, "-m" },
            arguments.Take(9));
        Assert.Equal(GoalWorktrees.ComposeSquashCommitMessage(scenario.Root, scenario.Main,
            mergeBase, member.CandidateRevision, tree, member.GoalId), arguments[9]);
        Assert.Equal("commit", RunGitOutput(scenario.Root, "cat-file", "-t", sha));
        Assert.Equal(tree, RunGitOutput(scenario.Root, "rev-parse", $"{sha}^{{tree}}"));
        Assert.Equal($"{sha} {mergeBase}", RunGitOutput(scenario.Root, "rev-list", "--parents", "-n", "1", sha));
        Assert.Equal(refs, RunGitOutput(scenario.Root, "for-each-ref"));
        Assert.Equal(worktrees, RunGitOutput(scenario.Root, "worktree", "list", "--porcelain"));
        Assert.Equal(status, RunGitOutput(scenario.MemberWorktree, "status", "--porcelain"));
        Assert.Equal(member.CandidateRevision, RunGitOutput(scenario.MemberWorktree, "rev-parse", "HEAD"));
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "not-a-commit")]
    public void TryCreate_FailedOrMalformedCommit_ReturnsNull(int exitCode, string output)
    {
        using var scenario = new Repository();
        var member = scenario.Members[0];
        var calls = 0;
        Assert.Null(MainIntegrationSquash.TryCreate(scenario.Root, scenario.Main, scenario.Main,
            member.CandidateRevision, member.GoalId, _ =>
            {
                calls++;
                return new GitCli.GitResult(exitCode, output, "injected failure");
            }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void StreamCompose_ChildWithMainIntegrationMerge_Composes()
    {
        using var scenario = new Repository();
        var parent = GoalId.New();
        RunGit(scenario.Root, "branch", GoalWorktrees.BranchName(parent), scenario.Main);
        var child = scenario.Members[0];
        var composition = StreamComposer.Compose(scenario.Root, parent, scenario.Main,
            [new StreamCompositionCandidate(child.GoalId, child.BranchRevision)]);
        Assert.Null(composition.Ejection);
        Assert.Equal(new[] { child.GoalId }, composition.ComposedChildren);
        Assert.Equal(RunGitOutput(scenario.Root, "show", $"{child.CandidateRevision}:{Repository.SharedPath}"),
            RunGitOutput(scenario.Root, "show", $"{composition.TreeRevision}:{Repository.SharedPath}"));
    }

    private static void AssertMembers(Repository scenario, MergeTrainWorkspace train)
    {
        Assert.Empty(train.Ejections);
        Assert.Equal(scenario.Members.Select(member => member.GoalId), train.Members.Select(member => member.GoalId));
    }

    private static void AssertResolvedLinearTree(Repository scenario, MergeTrainWorkspace train)
    {
        Assert.Equal(RunGitOutput(scenario.Root, "show", $"{scenario.Members[0].CandidateRevision}:{Repository.SharedPath}"),
            RunGitOutput(scenario.Root, "show", $"{train.TreeRevision}:{Repository.SharedPath}"));
        Assert.Equal("0", RunGitOutput(scenario.Root, "rev-list", "--merges", "--count",
            $"{scenario.Main}..{train.CommitRevision}"));
    }

    private static void AssertConflictAndSurvivor(Repository scenario, MergeTrainWorkspace train)
    {
        var ejection = Assert.Single(train.Ejections);
        Assert.Equal(scenario.Members[0].GoalId, ejection.GoalId);
        Assert.Equal(MergeTrainEjectionReason.RebaseConflict, ejection.Reason);
        Assert.Equal(new[] { Repository.SharedPath }, ejection.ConflictPaths);
        Assert.Equal(scenario.Members[1].GoalId, Assert.Single(train.Members).GoalId);
        var rebasePath = RunGitOutput(train.Path, "rev-parse", "--git-path", "rebase-merge");
        Assert.False(Directory.Exists(Path.GetFullPath(rebasePath, train.Path)));
    }

    private sealed class Repository : IDisposable
    {
        internal const string SharedPath = "shared.txt";
        internal string Root { get; }
        internal string Main { get; }
        internal string MemberWorktree { get; }
        internal MergeTrainMemberBinding[] Members { get; }

        internal Repository(bool integrate = true, bool stale = false)
        {
            Root = CreateAcceptanceCohortRepository();
            try
            {
                RunGit(Root, "config", "core.autocrlf", "false");
                File.WriteAllText(Path.Combine(Root, SharedPath), "base\nanchor\n");
                RunGit(Root, "add", SharedPath);
                RunGit(Root, "commit", "-m", "Seed shared file");
                var member = GoalId.New();
                _ = CreateWorktreeCandidate(Root, member, SharedPath, "candidate\nanchor\n");
                MemberWorktree = GoalWorktrees.Ensure(Root, member);
                var landed = GoalId.New();
                _ = CreateWorktreeCandidate(Root, landed, stale ? "landed.txt" : SharedPath, "landed\nanchor\n");
                RunGit(Root, "merge", "--ff-only", GoalWorktrees.BranchName(landed));
                if (integrate)
                {
                    Assert.Equal(stale ? 0 : 1, RunGitExitCode(MemberWorktree, "merge", "--no-edit", "main"));
                    if (!stale)
                    {
                        Assert.Equal(SharedPath, RunGitOutput(MemberWorktree, "diff", "--name-only", "--diff-filter=U"));
                        File.WriteAllText(Path.Combine(MemberWorktree, SharedPath), "landed\ncandidate\nanchor\n");
                        RunGit(MemberWorktree, "add", SharedPath);
                        RunGit(MemberWorktree, "commit", "-m", "Resolve main integration keeping both sides");
                    }
                    Assert.Equal("1", RunGitOutput(Root, "rev-list", "--merges", "--count",
                        $"main..{GoalWorktrees.BranchName(member)}"));
                }
                if (stale)
                {
                    var later = GoalId.New();
                    _ = CreateWorktreeCandidate(Root, later, SharedPath, "new-main\nanchor\n");
                    RunGit(Root, "merge", "--ff-only", GoalWorktrees.BranchName(later));
                }
                var other = GoalId.New();
                var otherRevision = CreateWorktreeCandidate(Root, other, "other.txt", "unrelated\n");
                Main = RunGitOutput(Root, "rev-parse", "main");
                Members = [TrainBind(member, RunGitOutput(MemberWorktree, "rev-parse", "HEAD"), SharedPath, "shared"),
                    TrainBind(other, otherRevision, "other.txt", "other")];
            }
            catch { DeleteDirectory(Root); throw; }
        }

        internal MergeTrainWorkspace Compose(DateTimeOffset? date = null) =>
            GoalWorktrees.CreateMergeTrainWorkspace(Root, Main, Members, TrunkBranchName.Default, committerDate: date);

        public void Dispose() => DeleteDirectory(Root);
    }
}
