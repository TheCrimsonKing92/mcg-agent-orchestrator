using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: AsyncLocal console capture, async-context culture, and local fixtures.
// Expected strings derive from ConsoleViews.GoalRecovery.cs at base
// 7bf9f91fe45d5b90445139295dda786e4392d443 (before extraction).
public sealed class GoalRecoveryReportTextViewCharacterizationTests
{
    [Fact]
    public void Print_MissingWorktreeAndNoFindings_PreservesExactText()
    {
        var actual = Capture(() => GoalRecoveryReportTextView.Print(EmptyReport()));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Goal recovery abcdef01 Active: Recovery sample", "Worktree: missing",
            "Build lease: goal-abcdef01 root=missing artifacts=missing metadata=missing ownerPid=unknown ownerAlive=False canCleanup=False",
            "  Lease absent", "Pending human input: 0", "Findings:", "  none",
            "Recommended actions:", "", ""), actual);
    }

    [Fact]
    public void Print_DirtyWorktreeJournalAndFindings_PreservesExactText()
    {
        var report = EmptyReport();
        var entry = new GoalOperationJournalEntry("acceptance-1", report.GoalId,
            "acceptance", GoalOperationStatus.Begin,
            new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero), "Checking candidate");
        report = report with
        {
            WorktreePath = "C:/work",
            WorktreeExists = true,
            WorktreeDirty = true,
            WorktreeDirtyPaths = ["one.cs", "two.cs", "three.cs", "four.cs", "five.cs", "six.cs"],
            HasBranchDiff = true,
            ChangeSummary = new([new("one.cs", [RepositoryChangeCategory.Source], false, false, false)],
                false, true, false, false, false, false, false, "focused checks"),
            TestImpactPlan = new(true, false, "one focused check",
                [new("focused", ["check", "focused"], "Changed printer")]),
            OperationJournal = new("C:/journal.jsonl", [entry], [entry], [entry]),
            BuildLease = new("goal-abcdef01", "C:/build", "C:/build/artifacts", "C:/build/lease.json",
                true, true, true, 42, true, false, "Owner active"),
            PendingHumanInputCount = 1,
            TaskFindings = [new(2, new TaskId("87654321-rest"), AgentRole.Tester,
                WorkTaskStatus.Failed, "Evidence missing", "verify 2 check")],
            RecommendedActions = ["Record verification", "Review diff"]
        };

        var actual = Capture(() => GoalRecoveryReportTextView.Print(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Goal recovery abcdef01 Active: Recovery sample",
            "Worktree: C:/work dirty=True diff=True", "  Commit-worthy paths:",
            "    one.cs", "    two.cs", "    three.cs", "    four.cs", "    five.cs", "    +1 more",
            "Build lease: goal-abcdef01 root=present artifacts=present metadata=present ownerPid=42 ownerAlive=True canCleanup=False",
            "  Owner active",
            "Change classification: files=1, docsOnly=False, behavior=True, build=False, security=False, generated=False, broad=False",
            "Recommended verification: focused checks", "Test impact: one focused check",
            "  focused: check focused", "Operation journal: C:/journal.jsonl",
            "  acceptance: Begin at 2026-10-30 08:00:00Z - Checking candidate",
            "Interrupted operations:", "  acceptance: idempotency=acceptance-1",
            "Pending human input: 1", "Findings:",
            "  Task 2 Tester Failed: Evidence missing", "     command: verify 2 check",
            "Recommended actions:", "  - Record verification", "  - Review diff", "", ""), actual);
    }

    [Fact]
    public void Print_UnknownWorktreeStatus_PreservesExactText()
    {
        var report = EmptyReport() with
        {
            WorktreeExists = true,
            WorktreePath = "C:/work",
            WorktreeStatusError = "git unavailable"
        };

        var actual = Capture(() => GoalRecoveryReportTextView.Print(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Goal recovery abcdef01 Active: Recovery sample",
            "Worktree: C:/work dirty=unknown diff=False",
            "  Worktree status unavailable: git unavailable",
            "Build lease: goal-abcdef01 root=missing artifacts=missing metadata=missing ownerPid=unknown ownerAlive=False canCleanup=False",
            "  Lease absent", "Pending human input: 0", "Findings:", "  none",
            "Recommended actions:", "", ""), actual);
    }

    private static GoalRecoveryReport EmptyReport() => new(
        new GoalId("abcdef01-rest"), "Recovery sample", GoalStatus.Active,
        null, false, null, [], null, false,
        new([], false, false, false, false, false, false, false, "none"),
        new(false, false, "no checks", []), new("C:/journal.jsonl", [], [], []),
        new("goal-abcdef01", "C:/build", "C:/build/artifacts", "C:/build/lease.json",
            false, false, false, null, false, false, "Lease absent"),
        null, 0, [], []);

    private static string Capture(Action print)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            return AsyncLocalConsoleRouter.Capture(print);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
