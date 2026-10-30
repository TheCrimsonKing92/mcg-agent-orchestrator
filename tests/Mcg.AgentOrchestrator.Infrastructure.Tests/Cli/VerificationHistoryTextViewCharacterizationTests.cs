using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture, async-context culture, and local fixtures.
// Expected strings derive from ConsoleViews.VerificationCommands.cs at base
// 7bf9f91fe45d5b90445139295dda786e4392d443 (before extraction).
public sealed class VerificationHistoryTextViewCharacterizationTests
{
    [Fact]
    public void Print_NoHistory_PreservesExactText()
    {
        var task = new TaskSpec(new TaskId("12345678-rest"), "Verify output", AgentRole.Tester);

        var actual = Capture(() => VerificationHistoryTextView.Print(task));

        Assert.Equal("No verification history." + Environment.NewLine, actual);
    }

    [Fact]
    public void Print_MultipleRecordsAndOptionalStreams_PreservesExactText()
    {
        var task = new TaskSpec(new TaskId("12345678-rest"), "Verify output", AgentRole.Tester);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Verification history sample", [task]);
        var timestamp = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "check first", "C:/work", 1, "first line\nsecond line  \n", "error  \n", timestamp));
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "check again", "C:/work", 0, " \t", "", timestamp.AddMinutes(1)));

        var actual = Capture(() => VerificationHistoryTextView.Print(task));

        Assert.Equal(string.Join(Environment.NewLine,
            "1. exit=1 completed=2026-10-30 08:00:00Z",
            "   command: check first",
            "   working directory: C:/work",
            "   stdout: first line\nsecond line",
            "   stderr: error",
            "2. exit=0 completed=2026-10-30 08:01:00Z",
            "   command: check again",
            "   working directory: C:/work", ""), actual);
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
