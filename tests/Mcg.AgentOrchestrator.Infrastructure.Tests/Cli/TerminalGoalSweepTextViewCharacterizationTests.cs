using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture and immutable, local fixtures.
// Expected text follows ConsoleViews.TerminalGoalSweep.cs at base 8711cbf2f.
public sealed class TerminalGoalSweepTextViewCharacterizationTests
{
    [Fact]
    public void Print_EmptySweep_PreservesExactText()
    {
        var actual = AsyncLocalConsoleRouter.Capture(() =>
            TerminalGoalSweepTextView.PrintTerminalGoalSweep(new([])));

        Assert.Equal(string.Empty, actual);
    }

    [Fact]
    public void Print_SummaryOwnedRootsRepairsAndBlockers_PreservesExactText()
    {
        var result = CreateResult();

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            TerminalGoalSweepTextView.PrintTerminalGoalSweep(result));

        Assert.Equal(string.Join(Environment.NewLine,
            "SWEEP_SUMMARY kind=stale-terminal-excluded count=2",
            "Resolved attention items: 3",
            "SWEEP_OWNED_ROOT_DEFERRED reason=\"registry unavailable\"",
            "SWEEP_OWNED_ROOT_OBSERVED detail=\"legacy-root\"",
            "SWEEP_REPAIR goal=abcdef12 kind=cleaned evidence=\"receipt found\" command=\"cleanup abcdef12\"",
            "SWEEP_REPAIR goal=abcdef12 kind=attention-resolved evidence=\"landed\" command=\"resolve abcdef12\"",
            "SWEEP_BLOCKER goal=abcdef12 kind=manual-review evidence=\"needs review\" command=\"inspect abcdef12\"",
            ""), actual);
    }

    [Fact]
    public void Print_DisabledRepairsAndBlockers_PreservesExactText()
    {
        var result = CreateResult();

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            TerminalGoalSweepTextView.PrintTerminalGoalSweep(result,
                includeRepairs: false, includeBlockers: false));

        Assert.Equal(string.Join(Environment.NewLine,
            "SWEEP_SUMMARY kind=stale-terminal-excluded count=2",
            "Resolved attention items: 3",
            "SWEEP_OWNED_ROOT_DEFERRED reason=\"registry unavailable\"",
            "SWEEP_OWNED_ROOT_OBSERVED detail=\"legacy-root\"",
            ""), actual);
    }

    [Theory]
    [InlineData(1, 0, "Resolved attention items: 0")]
    [InlineData(0, 2, "Resolved attention items: 2")]
    public void Print_AttentionSummaryWithoutGoalDetails_PreservesExactText(
        int terminalized, int resolved, string expectedLine)
    {
        var result = new TerminalGoalSweepResult([],
            TerminalizedGoalCount: terminalized, ResolvedAttentionItemCount: resolved);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            TerminalGoalSweepTextView.PrintTerminalGoalSweep(result));

        Assert.Equal(expectedLine + Environment.NewLine, actual);
    }

    private static TerminalGoalSweepResult CreateResult() =>
        new([new(new GoalId("abcdef123456"), "abcdef12",
            [new("cleaned", "receipt found", "cleanup abcdef12"),
             new("attention-resolved", "landed", "resolve abcdef12")],
            [new("stale-terminal-excluded", "already excluded", "ignored"),
             new("manual-review", "needs review", "inspect abcdef12")])],
            ExcludedGoalCount: 2, TerminalizedGoalCount: 1, ResolvedAttentionItemCount: 3)
        {
            OwnedRoots = new(0, 0, 0, 0, ["legacy-root"], "registry unavailable")
        };
}
