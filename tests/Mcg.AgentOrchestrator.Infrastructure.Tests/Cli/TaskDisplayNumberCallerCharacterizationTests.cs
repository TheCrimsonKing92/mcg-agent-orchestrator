using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture, async-context culture, and local fixtures.
// Expected strings derive from ReadinessWorklistTextView.PrintVerificationWorklist
// at base 7bf9f91fe45d5b90445139295dda786e4392d443 (before forwarder removal).
public sealed class TaskDisplayNumberCallerCharacterizationTests
{
    [Fact]
    public void PrintVerificationWorklist_ThreeTasksOutOfOrder_PreservesExactNumbers()
    {
        var goal = new Goal(new GoalId("abcdef01-rest"), "Three task sample",
            [new(new TaskId("11111111-rest"), "Implement", AgentRole.Developer),
             new(new TaskId("22222222-rest"), "Verify", AgentRole.Tester),
             new(new TaskId("33333333-rest"), "Review", AgentRole.Reviewer)]);
        // Deliberately differ from goal order: numbering must follow task identity,
        // not the position of an item in the displayed worklist.
        var worklist = new GoalVerificationWorklist(goal.Id, goal.Objective,
            GoalStatus.Active, false, 3,
            [new(goal.Tasks[2].Id, AgentRole.Reviewer, "Review", WorkTaskStatus.Completed,
                 VerificationGateStatus.MissingVerification, "No review receipt", "Record review"),
             new(goal.Tasks[0].Id, AgentRole.Developer, "Implement", WorkTaskStatus.Completed,
                 VerificationGateStatus.FailedVerification, "Build failed", "Repair build"),
             new(goal.Tasks[1].Id, AgentRole.Tester, "Verify", WorkTaskStatus.Pending,
                 VerificationGateStatus.NotReady, "Awaiting implementation", "Inspect task")]);

        var actual = Capture(() => ReadinessWorklistTextView.PrintVerificationWorklist(goal, worklist));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Goal abcdef01 verification worklist: 3 open",
            "Objective: Three task sample", "Status: Active",
            "  3. [MissingVerification] Reviewer: Review",
            "     No review receipt", "     action: Record review",
            "     command: verify 3 <command> | verify-manual 3 passed <note>",
            "  1. [FailedVerification] Developer: Implement",
            "     Build failed", "     action: Repair build",
            "     command: verifications 1 | retry 1 <note>",
            "  2. [NotReady] Tester: Verify",
            "     Awaiting implementation", "     action: Inspect task",
            "     command: task 2", "", ""), actual);
    }

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
