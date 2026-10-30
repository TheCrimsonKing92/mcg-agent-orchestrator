using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: AsyncLocal console capture and immutable, local fixtures.
public sealed class GoalReadinessPreflightTextViewCharacterizationTests
{
    [Fact]
    public void Print_FindingsAndTaskScopes_PreservesExactText()
    {
        var report = new GoalReadinessPreflightReport(new GoalId("abcdef123456"),
            "Extract console printers.", GoalStatus.Active,
            GoalReadinessRecommendation.RequireOperatorConfirmation,
            AllowsUnattendedStart: false, RequiresOperatorConfirmation: true,
            HasHardBlockers: false, FileScopeConfidence: "mixed",
            RequiresWorkspace: true, HasWorkspace: false,
            Findings:
            [
                new(GoalReadinessSeverity.Warning, "workspace", "Workspace missing.", true),
                new(GoalReadinessSeverity.Info, "scope", "Scope recorded.", false)
            ],
            Tasks:
            [
                new(1, new TaskId("task-1"), AgentRole.Developer, WorkTaskStatus.Assigned,
                    TaskComplexity.Simple, true, []),
                new(2, new TaskId("task-2"), AgentRole.Tester, WorkTaskStatus.Pending,
                    TaskComplexity.Complex, false, ["tests/A.cs", "tests/B.cs"]),
                new(3, new TaskId("task-3"), AgentRole.Reviewer, WorkTaskStatus.Pending,
                    TaskComplexity.Auto, true,
                    ["src/A.cs", "src/B.cs", "src/C.cs", "src/D.cs", "src/E.cs", "src/F.cs"])
            ]);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            GoalReadinessPreflightTextView.PrintGoalReadinessPreflight(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Goal readiness abcdef12 Active: Extract console printers.",
            "Recommendation: RequireOperatorConfirmation",
            "Start allowed: False",
            "Requires confirmation: True",
            "Workspace: required=True, present=False",
            "File scope confidence: mixed",
            "Findings:",
            "  Warning workspace: Workspace missing.",
            "  Info scope: Scope recorded.",
            "Tasks:",
            "  task 1 Developer: Assigned, complexity=Simple, agent=assigned, scopes=none",
            "  task 2 Tester: Pending, complexity=Complex, agent=missing, scopes=tests/A.cs, tests/B.cs",
            "  task 3 Reviewer: Pending, complexity=Auto, agent=assigned, scopes=src/A.cs, src/B.cs, src/C.cs, src/D.cs, ... 2 more",
            "",
            ""), actual);
    }

    [Fact]
    public void Print_EmptyFindingsAndTasks_PreservesExactText()
    {
        var report = new GoalReadinessPreflightReport(new GoalId("0123456789ab"),
            "Ready goal.", GoalStatus.Draft, GoalReadinessRecommendation.Proceed,
            AllowsUnattendedStart: true, RequiresOperatorConfirmation: false,
            HasHardBlockers: false, FileScopeConfidence: "none",
            RequiresWorkspace: false, HasWorkspace: true, Findings: [], Tasks: []);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            GoalReadinessPreflightTextView.PrintGoalReadinessPreflight(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Goal readiness 01234567 Draft: Ready goal.",
            "Recommendation: Proceed",
            "Start allowed: True",
            "Requires confirmation: False",
            "Workspace: required=False, present=True",
            "File scope confidence: none",
            "Findings:",
            "Tasks:",
            "",
            ""), actual);
    }
}
