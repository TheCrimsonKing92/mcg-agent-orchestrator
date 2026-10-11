using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using MetadataFixture = GoalGitRefFingerprintReaderTests.MetadataFixture;

// Parallel-safe: each sweep owns its kernel, metadata, stores and injected runner.
public sealed class TerminalGoalSweepGitIndexReuseTests
{
    [Xunit.Fact]
    public void UnchangedTicksSkipExactlyFourIndexCommands()
    {
        using var fixture = new MetadataFixture();
        var cache = new TerminalGoalSweepCache();
        var first = Run(fixture, cache);
        var firstCommands = fixture.Commands.ToArray();
        var second = Run(fixture, cache);
        var secondCommands = fixture.Commands.Skip(firstCommands.Length).ToArray();
        var third = Run(fixture, cache);
        var thirdCommands = fixture.Commands.Skip(firstCommands.Length + secondCommands.Length).ToArray();

        foreach (var command in MetadataFixture.IndexCommands)
            Assert.Equal(1, fixture.Commands.Count(actual => actual == command));
        Assert.Equal(first.GitSpawnCount - 4, second.GitSpawnCount);
        Assert.Equal(first.GitSpawnCount - 4, third.GitSpawnCount);
        Assert.Equal(firstCommands.Where(command => !MetadataFixture.IndexCommands.Contains(command)).ToArray(), secondCommands);
        Assert.Equal(secondCommands, thirdCommands);
        AssertContentState(first, "inconclusive");
        AssertContentState(second, "inconclusive");
        AssertContentState(third, "inconclusive");
        Assert.Equal(3, fixture.Commands.Count(command => command.StartsWith("cherry ", StringComparison.Ordinal)));
        AssertSameGoalResults(first, second);
        AssertSameGoalResults(first, third);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ReusePreservesUnmergedGoalResultsAndWorktreeComparer(bool registeredWorktree)
    {
        using var fixture = new MetadataFixture();
        fixture.CherryResult = new(0, $"+ {new string('3', 40)}\n", string.Empty);
        if (registeredWorktree)
            fixture.WorktreeOutput = $"worktree {GoalWorktrees.WorktreePath(fixture.Root, fixture.GoalId).ToUpperInvariant()}\n";
        var cache = new TerminalGoalSweepCache();
        var first = Run(fixture, cache);
        var second = Run(fixture, cache);
        var third = Run(fixture, cache);
        var uncached = Run(fixture, cache: null);

        foreach (var result in new[] { first, second, third })
        {
            AssertContentState(result, "absentfrommain");
            AssertSameGoalResults(uncached, result);
            Assert.Equal(0, result.CacheHitCount);
        }
        AssertContentState(uncached, "absentfrommain");
        foreach (var command in MetadataFixture.IndexCommands)
            Assert.Equal(2, fixture.Commands.Count(actual => actual == command));
    }

    private static TerminalGoalSweepResult Run(MetadataFixture fixture, TerminalGoalSweepCache? cache)
    {
        var before = fixture.Commands.Count;
        var result = TerminalGoalSweep.Run(fixture.Kernel, fixture.Root, "main", cache: cache,
            integrationEvidenceResolver: new NoIntegrationEvidence(), gitRunner: fixture.RunGit);
        Assert.Equal(fixture.Commands.Count - before, result.GitSpawnCount);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        return result;
    }

    private static void AssertContentState(TerminalGoalSweepResult result, string state)
    {
        var goal = Assert.Single(result.Goals);
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
            Assert.Equal(left.Blockers.Select(item => (item.Kind, item.Evidence, item.Command)).ToArray(),
                right.Blockers.Select(item => (item.Kind, item.Evidence, item.Command)).ToArray());
            Assert.Equal(left.Repairs.Select(item => (item.Kind, item.Evidence, item.Command)).ToArray(),
                right.Repairs.Select(item => (item.Kind, item.Evidence, item.Command)).ToArray());
        }
        Assert.Equal(expected.ExplicitlySweptGoalIds.ToArray(), actual.ExplicitlySweptGoalIds.ToArray());
        Assert.Equal(expected.ExplicitlyTerminalizedGoalIds.ToArray(), actual.ExplicitlyTerminalizedGoalIds.ToArray());
        Assert.Equal(expected.TerminalizedGoalCount, actual.TerminalizedGoalCount);
        Assert.Equal(expected.ResolvedAttentionItemCount, actual.ResolvedAttentionItemCount);
    }

    private sealed class NoIntegrationEvidence : IGoalIntegrationEvidenceResolver
    {
        public bool TryResolve(GoalId goalId, out GoalIntegrationEvidence? evidence)
        {
            evidence = null;
            return false;
        }
    }
}
