using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fixture owns its temporary directory, stores, kernel and runner.
// No global GitRunner override, real repository or shared database is used.
public sealed class TerminalGoalSweepCherryCacheTests
{
    [Xunit.Fact(DisplayName = "Unmoved branches reuse cherry and preserve sweep outcomes")]
    public void SharedCacheSkipsRepeatedCherryAndPreservesResults()
    {
        using var fixture = new SweepFixture();
        var cache = new TerminalGoalSweepCache();

        var first = fixture.Run(cache);
        var second = fixture.Run(cache);

        Assert.Equal(1, fixture.CherryCount);
        Assert.True(second.GitSpawnCount <= first.GitSpawnCount - 1);
        Assert.Equal(0, first.CacheHitCount);
        Assert.Equal(0, second.CacheHitCount);
        AssertContentState(first, fixture.GoalId, "absentfrommain");
        AssertContentState(second, fixture.GoalId, "absentfrommain");

        var uncached = fixture.Run(cache: null);
        Assert.Equal(2, fixture.CherryCount);
        AssertContentState(uncached, fixture.GoalId, "absentfrommain");
        AssertSameGoalResults(uncached, first);
        AssertSameGoalResults(uncached, second);
        Assert.Equal(GoalStatus.Verified, fixture.Kernel.GetGoal(fixture.GoalId).Status);
    }

    [Xunit.Theory(DisplayName = "Moving main or the goal tip reruns cherry and reports the new state")]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void ChangedCommitInvalidatesSharedCache(bool changeMain)
    {
        using var fixture = new SweepFixture();
        var cache = new TerminalGoalSweepCache();
        var first = fixture.Run(cache);
        AssertContentState(first, fixture.GoalId, "absentfrommain");
        Assert.Equal(1, fixture.CherryCount);

        if (changeMain)
        {
            fixture.MainSha = new string('4', 40);
        }
        else
        {
            fixture.TipSha = new string('5', 40);
        }
        fixture.CherryResult = new GitCli.GitResult(0, string.Empty, string.Empty);

        var second = fixture.Run(cache);

        Assert.Equal(2, fixture.CherryCount);
        Assert.Equal(0, second.CacheHitCount);
        AssertContentState(second, fixture.GoalId, "nocommitsahead");
    }

    [Xunit.Fact(DisplayName = "Sweeps without a shared cache invoke cherry on every run")]
    public void NullCacheKeepsPerRunCherryInvocation()
    {
        using var fixture = new SweepFixture();

        var first = fixture.Run(cache: null);
        Assert.Equal(1, fixture.CherryCount);
        var second = fixture.Run(cache: null);

        Assert.Equal(2, fixture.CherryCount);
        Assert.Equal(first.GitSpawnCount, second.GitSpawnCount);
        AssertSameGoalResults(first, second);
        AssertContentState(second, fixture.GoalId, "absentfrommain");
    }

    [Xunit.Theory(DisplayName = "Failed or ambiguous cherry calls are retried on the next sweep")]
    [Xunit.InlineData(1, "")]
    [Xunit.InlineData(0, "unexpected output")]
    public void InconclusiveCherryIsRetried(int exitCode, string output)
    {
        using var fixture = new SweepFixture();
        var cache = new TerminalGoalSweepCache();
        fixture.CherryResult = new GitCli.GitResult(exitCode, output, "classification unavailable");

        var first = fixture.Run(cache);
        Assert.Equal(1, fixture.CherryCount);
        AssertContentState(first, fixture.GoalId, "inconclusive");
        fixture.CherryResult = SweepFixture.AbsentCherryResult;

        var second = fixture.Run(cache);
        var third = fixture.Run(cache);

        Assert.Equal(2, fixture.CherryCount);
        AssertContentState(second, fixture.GoalId, "absentfrommain");
        AssertContentState(third, fixture.GoalId, "absentfrommain");
    }

    private static void AssertContentState(TerminalGoalSweepResult result, GoalId goalId, string state)
    {
        var goal = Assert.Single(result.Goals);
        Assert.Equal(goalId, goal.GoalId);
        var blocker = Assert.Single(goal.Blockers);
        Assert.Equal("completed-branch-unmerged", blocker.Kind);
        Assert.Contains($"contentCheck={state}", blocker.Evidence, StringComparison.Ordinal);
    }

    private static void AssertSameGoalResults(TerminalGoalSweepResult expected, TerminalGoalSweepResult actual)
    {
        Assert.Equal(expected.Goals.Count, actual.Goals.Count);
        foreach (var (left, right) in expected.Goals.Zip(actual.Goals))
        {
            Assert.Equal(left.GoalId, right.GoalId);
            Assert.Equal(left.GoalPrefix, right.GoalPrefix);
            Assert.Equal(left.Changed, right.Changed);
            Assert.Equal(left.Reconciliation, right.Reconciliation);
            Assert.Equal(
                left.Blockers.Select(item => (item.Kind, item.Evidence, item.Command)).ToArray(),
                right.Blockers.Select(item => (item.Kind, item.Evidence, item.Command)).ToArray());
            Assert.Equal(
                left.Repairs.Select(item => (item.Kind, item.Evidence, item.Command)).ToArray(),
                right.Repairs.Select(item => (item.Kind, item.Evidence, item.Command)).ToArray());
        }
        Assert.Equal(expected.ExplicitlySweptGoalIds.ToArray(), actual.ExplicitlySweptGoalIds.ToArray());
        Assert.Equal(expected.ExplicitlyTerminalizedGoalIds.ToArray(), actual.ExplicitlyTerminalizedGoalIds.ToArray());
        Assert.Equal(expected.TerminalizedGoalCount, actual.TerminalizedGoalCount);
        Assert.Equal(expected.ResolvedAttentionItemCount, actual.ResolvedAttentionItemCount);
    }

    private sealed class SweepFixture : IDisposable
    {
        internal static GitCli.GitResult AbsentCherryResult =>
            new(0, $"+ {new string('3', 40)}\n", string.Empty);

        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-cherry-cache-{Guid.NewGuid():N}");
        private readonly Func<string, IReadOnlyList<string>, GitCli.GitResult> _runner;
        private int _invocationCount;

        internal AgentOrchestratorKernel Kernel { get; }
        internal GoalId GoalId { get; }
        internal string MainSha { get; set; } = new('1', 40);
        internal string TipSha { get; set; } = new('2', 40);
        internal GitCli.GitResult CherryResult { get; set; } = AbsentCherryResult;
        internal int CherryCount { get; private set; }

        internal SweepFixture()
        {
            Directory.CreateDirectory(_root);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Memoize terminal branch content", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            GoalId = goal.Id;
            var snapshot = kernel.ExportSnapshot();
            Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(item => item with
                {
                    Status = GoalStatus.Verified,
                    Tasks = item.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray()
                }).ToArray()
            });
            _runner = RunGit;
        }

        internal TerminalGoalSweepResult Run(TerminalGoalSweepCache? cache)
        {
            var before = _invocationCount;
            var result = TerminalGoalSweep.Run(Kernel, _root, TrunkBranchName.Default, cache: cache, gitRunner: _runner);
            Assert.Equal(_invocationCount - before, result.GitSpawnCount);
            Assert.Equal(GoalStatus.Verified, Kernel.GetGoal(GoalId).Status);
            return result;
        }

        private GitCli.GitResult RunGit(string workingDirectory, IReadOnlyList<string> args)
        {
            Assert.Equal(_root, workingDirectory);
            _invocationCount++;
            var command = string.Join(" ", args);
            switch (command)
            {
                case "for-each-ref --format=%(refname:short) %(objectname) refs/heads/goal/":
                    return new(0, $"{GoalWorktrees.BranchName(GoalId)} {TipSha}\n", string.Empty);
                case "for-each-ref --format=%(refname:short) --merged HEAD refs/heads/goal/":
                case "worktree list --porcelain":
                    return new(0, string.Empty, string.Empty);
                case "rev-parse --verify refs/heads/main":
                    return new(0, $"{MainSha}\n", string.Empty);
                default:
                    if (args[0] == "cherry")
                    {
                        Assert.Equal(new[] { "cherry", MainSha, GoalWorktrees.BranchName(GoalId) }, args.ToArray());
                        CherryCount++;
                        return CherryResult;
                    }
                    return new(1, string.Empty, $"unexpected git command: {command}");
            }
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
