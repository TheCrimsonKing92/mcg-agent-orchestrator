using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns a unique repository and all of its branches/worktrees.
public sealed class StreamComposerTests : GoalWorktreeTestBase
{
    private static readonly GoalId Parent = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compose_DisjointChildren_PreservesOrderAndMatchesManualTree(bool reverse)
    {
        var repo = CreateSeededRepository();
        try
        {
            RunGit(repo, "branch", "-M", "main");
            var parentRevision = RunGitOutput(repo, "rev-parse", "HEAD");
            RunGit(repo, "branch", GoalWorktrees.BranchName(Parent), parentRevision);
            var children = Enumerable.Range(1, 3)
                .Select(index => CreateChild(repo, parentRevision, index, $"{index}.txt")).ToArray();
            if (reverse) Array.Reverse(children);
            var worktrees = RunGitOutput(repo, "worktree", "list", "--porcelain");

            var result = StreamComposer.Compose(repo, Parent, parentRevision, children);

            Assert.Equal(children.Select(child => child.GoalId), result.ComposedChildren);
            Assert.Equal(children.Select(child => RunGitOutput(repo, "show", "-s", "--format=%s", child.BranchRevision)),
                RunGitOutput(repo, "log", "--first-parent", "--reverse", "--format=%s",
                    $"{parentRevision}..{result.CommitRevision}").Split('\n'));
            RunGit(repo, "checkout", "--detach", parentRevision);
            foreach (var child in children) RunGit(repo, "cherry-pick", child.BranchRevision);
            Assert.Equal(RunGitOutput(repo, "rev-parse", "HEAD^{tree}"), result.TreeRevision);
            Assert.Equal(result.TreeRevision, RunGitOutput(repo, "rev-parse", $"{result.CommitRevision}^{{tree}}"));
            RunGit(repo, "checkout", "main");
            Assert.Null(result.Ejection);
            Assert.False(result.IsReleasable); // A composed tree still requires build evidence.
            Assert.Equal(worktrees, RunGitOutput(repo, "worktree", "list", "--porcelain"));
            AssertBranches(repo, parentRevision, children);
            AssertNoCompositionWorktree(repo);
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void Compose_ConflictingChild_KeepsPrefixAndCleansWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            RunGit(repo, "branch", "-M", "main");
            File.WriteAllText(Path.Combine(repo, "z.txt"), "base\n");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "base\n");
            RunGit(repo, "add", "z.txt", "a.txt");
            RunGit(repo, "commit", "-m", "Conflict seed");
            var parentRevision = RunGitOutput(repo, "rev-parse", "HEAD");
            RunGit(repo, "branch", GoalWorktrees.BranchName(Parent), parentRevision);
            var first = CreateChild(repo, parentRevision, 1, "z.txt", "a.txt");
            var conflict = CreateChild(repo, parentRevision, 2, "z.txt", "a.txt");
            var later = CreateChild(repo, parentRevision, 3, "later.txt");
            var worktrees = RunGitOutput(repo, "worktree", "list", "--porcelain");

            var result = StreamComposer.Compose(repo, Parent, parentRevision, [first, conflict, later]);

            Assert.Equal(new[] { first.GoalId }, result.ComposedChildren);
            var ejection = Assert.IsType<MergeTrainEjection>(result.Ejection);
            Assert.Equal(conflict.GoalId, ejection.GoalId);
            Assert.Equal(MergeTrainEjectionReason.RebaseConflict, ejection.Reason);
            Assert.Equal(new[] { "a.txt", "z.txt" }, ejection.ConflictPaths);
            Assert.Equal("child 1", RunGitOutput(repo, "show", $"{result.CommitRevision}:a.txt"));
            Assert.DoesNotContain("later.txt", RunGitOutput(repo, "ls-tree", "-r", "--name-only", result.CommitRevision));
            Assert.False((result with { BuildCheck = new(true, null, "ok") }).IsReleasable);
            Assert.Equal(worktrees, RunGitOutput(repo, "worktree", "list", "--porcelain"));
            Assert.False(Directory.Exists(Path.Combine(repo, ".git", "rebase-merge")));
            AssertNoCompositionWorktree(repo);
            AssertBranches(repo, parentRevision, [first, conflict, later]);
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compose_StaleParentOrChild_ThrowsBeforeWorktree(bool staleParent)
    {
        var repo = CreateSeededRepository();
        try
        {
            RunGit(repo, "branch", "-M", "main");
            var parentRevision = RunGitOutput(repo, "rev-parse", "HEAD");
            RunGit(repo, "branch", GoalWorktrees.BranchName(Parent), parentRevision);
            var child = CreateChild(repo, parentRevision, 1, "child.txt");
            var changed = staleParent ? Parent : child.GoalId;
            RunGit(repo, "checkout", GoalWorktrees.BranchName(changed));
            RunGit(repo, "commit", "--allow-empty", "-m", "Advance candidate");
            RunGit(repo, "checkout", "main");
            var worktrees = RunGitOutput(repo, "worktree", "list", "--porcelain");

            var error = Assert.Throws<InvalidOperationException>(() =>
                StreamComposer.Compose(repo, Parent, parentRevision, [child]));

            Assert.Contains(changed.Value[..8], error.Message);
            Assert.Contains("is stale before composition", error.Message);
            Assert.Equal(worktrees, RunGitOutput(repo, "worktree", "list", "--porcelain"));
            Assert.False(Directory.Exists(Path.Combine(repo, GoalWorktrees.DirectoryName)));
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void Compose_OverRatchetChild_CommitsSharedRetightenerRepair()
    {
        var scenario = GoalWorktreeTestsRebaseRatchetRetighten.CreateScenario();
        try
        {
            RunGit(scenario.Repo, "branch", GoalWorktrees.BranchName(Parent), scenario.Main);
            var worktrees = RunGitOutput(scenario.Repo, "worktree", "list", "--porcelain");
            var result = StreamComposer.Compose(scenario.Repo, Parent, scenario.Main,
                [new(scenario.Goal, scenario.OldHead)]);

            Assert.Equal(SourceSizeRatchetRetightener.CommitSubject,
                RunGitOutput(scenario.Repo, "show", "-s", "--format=%s", result.CommitRevision));
            Assert.Equal(18, RunGitOutput(scenario.Repo, "show",
                $"{result.CommitRevision}:{GoalWorktreeTestsRebaseRatchetRetighten.GuardedPath}").Split('\n').Length);
            Assert.Contains("\"src/Guarded.cs\", 18", RunGitOutput(scenario.Repo, "show",
                $"{result.CommitRevision}:{SourceSizeRatchet.SourcePath}"));
            Assert.Equal(new[] { scenario.Goal }, result.ComposedChildren);
            Assert.Equal(worktrees, RunGitOutput(scenario.Repo, "worktree", "list", "--porcelain"));
            AssertBranches(scenario.Repo, scenario.Main, [new(scenario.Goal, scenario.OldHead)]);
            AssertNoCompositionWorktree(scenario.Repo);
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    [Fact]
    public void Compose_EmptyDuplicateOrParentCandidates_RejectsBeforeFilesystem()
    {
        var repo = CreateSeededRepository();
        try
        {
            var revision = RunGitOutput(repo, "rev-parse", "HEAD");
            var child = new StreamCompositionCandidate(new GoalId(new string('1', 32)), revision);
            Assert.Throws<ArgumentException>(() => StreamComposer.Compose(repo, Parent, revision, []));
            Assert.Throws<ArgumentException>(() => StreamComposer.Compose(repo, Parent, revision, [child, child]));
            Assert.Throws<ArgumentException>(() => StreamComposer.Compose(repo, Parent, revision, [new(Parent, revision)]));
            Assert.False(Directory.Exists(Path.Combine(repo, GoalWorktrees.DirectoryName)));
        }
        finally { DeleteDirectory(repo); }
    }

    private static StreamCompositionCandidate CreateChild(string repo, string revision, int index, params string[] paths)
    {
        var id = new GoalId(new string((char)('0' + index), 32));
        RunGit(repo, "checkout", "-b", GoalWorktrees.BranchName(id), revision);
        foreach (var path in paths) File.WriteAllText(Path.Combine(repo, path), $"child {index}\n");
        RunGit(repo, ["add", .. paths]);
        RunGit(repo, "commit", "-m", $"Child {index}");
        var head = RunGitOutput(repo, "rev-parse", "HEAD");
        RunGit(repo, "checkout", "main");
        return new(id, head);
    }

    private static void AssertBranches(string repo, string parentRevision, IReadOnlyList<StreamCompositionCandidate> children)
    {
        Assert.Equal(parentRevision, RunGitOutput(repo, "rev-parse", GoalWorktrees.BranchName(Parent)));
        foreach (var child in children)
            Assert.Equal(child.BranchRevision, RunGitOutput(repo, "rev-parse", GoalWorktrees.BranchName(child.GoalId)));
    }

    private static void AssertNoCompositionWorktree(string repo) =>
        Assert.Empty(Directory.GetDirectories(Path.Combine(repo, GoalWorktrees.DirectoryName), "c-*"));
}
