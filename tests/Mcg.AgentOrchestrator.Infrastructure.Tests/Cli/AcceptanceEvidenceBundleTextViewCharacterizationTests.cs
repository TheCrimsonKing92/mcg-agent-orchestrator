using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: AsyncLocal console capture, async-context culture, and local fixtures.
// Expected strings derive from ConsoleViews.AcceptanceEvidence.cs at base
// 7bf9f91fe45d5b90445139295dda786e4392d443 (before extraction).
public sealed class AcceptanceEvidenceBundleTextViewCharacterizationTests
{
    [Fact]
    public void Print_EmptyPassedBundle_PreservesExactText()
    {
        var actual = Capture(() => AcceptanceEvidenceBundleTextView.Print(EmptyBundle()));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Acceptance evidence bundle: passed", "Goal: abcdef01 Verified",
            "Worktree: (missing)", "Changed files: 0", "Diff stat: no branch diff",
            "Change classification: docsOnly=False, behavior=False, build=False, security=False, generated=False, broad=False",
            "Recommended verification: none", "Test impact: no checks",
            "Verification policy: tests=not required, humanReview=not required",
            "Build environment:", "  lease id: goal-abcdef01",
            "  root: missing C:/build", "  lease: missing C:/build/lease.json",
            "Acceptance checks: 0", "Task verification records: 0/0",
            "Blockers: none", "", ""), actual);
    }

    [Fact]
    public void Print_PopulatedBlockedBundle_PreservesExactText()
    {
        var bundle = EmptyBundle() with
        {
            Status = GoalStatus.Active,
            Passed = false,
            WorktreePath = "C:/work",
            VerificationSkipped = true,
            ChangedFiles = ["one.cs", "two.cs", "three.cs", "four.cs", "five.cs",
                "six.cs", "seven.cs", "eight.cs", "nine.cs", "ten.cs"],
            DiffStat = "10 files changed",
            ChangeSummary = new([], false, true, true, true, false, true, true, "broad checks"),
            TestImpactPlan = new(true, true, "focused and broad",
                [new("focused", ["check", "focused"], "Changed printer")]),
            VerificationPolicy = new("policy", true, true, []),
            PolicyChecks = [new("review", "manual", true, "missing", "review diff", "Needs review"),
                new("lint", "command", false, "passed", "lint", "Style check")],
            BuildEnvironment = new("goal-abcdef01", "C:/build", "C:/build/lease.json", true, true),
            AcceptanceChecks =
            [
                new("compile", true, 0, null, "C:/artifacts", "build-broker", "lease-1",
                    42, true, "No errors"),
                new("review", false, null, null)
            ],
            TaskEvidence =
            [
                new(1, AgentRole.Developer, WorkTaskStatus.Completed, "check build", 0, true, null),
                new(2, AgentRole.Tester, WorkTaskStatus.Failed, "check tests", 1, false, null)
            ],
            Blockers = [new("review-missing", "Review required", "review diff")]
        };

        var actual = Capture(() => AcceptanceEvidenceBundleTextView.Print(bundle));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Acceptance evidence bundle: blocked", "Goal: abcdef01 Active",
            "Worktree: C:/work", "Changed files: 10", "Diff stat: 10 files changed",
            "Change classification: docsOnly=False, behavior=True, build=True, security=True, generated=True, broad=True",
            "Recommended verification: broad checks", "Test impact: focused and broad",
            "  focused: check focused", "     reason: Changed printer",
            "Verification policy: tests=required, humanReview=required",
            "  missing - required review [manual] review diff", "     reason: Needs review",
            "  passed - optional lint [command] lint", "     reason: Style check",
            "  one.cs", "  two.cs", "  three.cs", "  four.cs", "  five.cs",
            "  six.cs", "  seven.cs", "  eight.cs", "  ... 2 more",
            "Build environment:", "  lease id: goal-abcdef01",
            "  root: present C:/build", "  lease: present C:/build/lease.json",
            "Acceptance checks: 2 (verification skipped)",
            "  passed - compile (exit 0) artifacts=C:/artifacts",
            "    broker: build-broker lease=lease-1 durationMs=42 lockRemediation=True",
            "    result: No errors", "  failed - review",
            "Task verification records: 1/2",
            "  task 1 Developer: Completed, verification exit 0, worker contract present, check build",
            "  task 2 Tester: Failed, verification exit 1, worker contract missing, check tests",
            "Blockers:", "  review-missing: Review required", "     next: review diff", "", ""), actual);
    }

    private static GoalAcceptanceEvidenceBundle EmptyBundle() => new(
        new GoalId("abcdef01-rest"), "Acceptance sample", GoalStatus.Verified, null,
        true, false, [], "no branch diff",
        new([], false, false, false, false, false, false, false, "none"),
        new(false, false, "no checks", []), new("policy", false, false, []), [],
        new("goal-abcdef01", "C:/build", "C:/build/lease.json", false, false), [], [], [], []);

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
