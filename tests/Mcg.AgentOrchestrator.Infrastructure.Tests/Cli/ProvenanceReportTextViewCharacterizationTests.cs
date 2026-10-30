using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture and local fixtures.
// Expected text follows ConsoleViews.Provenance.cs at base 781c2fd21.
public sealed class ProvenanceReportTextViewCharacterizationTests
{
    [Fact]
    public void Print_NoCompletedGoals_PreservesExactText()
    {
        var snapshot = new ProvenanceReportSnapshot(0, 0, 0, [], [], []);

        var actual = AsyncLocalConsoleRouter.Capture(() => ProvenanceReportTextView.Print(snapshot));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Provenance audit (0 completed goal(s)):",
            "  Backed:   0",
            "  Unbacked: 0",
            "",
            "  No completed goals found.",
            "",
            "  No unbacked dogfood references found.",
            "",
            "  No unbacked commit SHAs found.",
            "", ""), actual);
    }

    [Fact]
    public void Print_MixedReceiptsAndMissingReferences_PreservesExactText()
    {
        var snapshot = new ProvenanceReportSnapshot(2, 1, 1,
            [
                new(new GoalId("aaaaaaaa00000000"), new string('x', 60), GoalStatus.Completed,
                    ProvenanceStatus.Backed, 2, 0),
                new(new GoalId("bbbbbbbb00000000"), new string('y', 61), GoalStatus.Completed,
                    ProvenanceStatus.Unbacked, 3, 2)
            ],
            [new("cccccccc", true, "goal cccccccc"), new("bbbbbbbb", false, "goal/bbbbbbbb")],
            ["1234567", "abcdef0"]);

        var actual = AsyncLocalConsoleRouter.Capture(() => ProvenanceReportTextView.Print(snapshot));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Provenance audit (2 completed goal(s)):",
            "  Backed:   1",
            "  Unbacked: 1",
            "",
            "  [BACKED  ] aaaaaaaa: xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
            "  [UNBACKED] bbbbbbbb: yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy... (2/3 tasks lack receipts)",
            "",
            "  Unbacked dogfood references (2):",
            "    cccccccc: absent from state (from \"goal cccccccc\")",
            "    bbbbbbbb: goal is unbacked (from \"goal/bbbbbbbb\")",
            "",
            "  Commit SHAs absent from git history (2):",
            "    1234567: not found in git history",
            "    abcdef0: not found in git history",
            "", ""), actual);
    }
}
