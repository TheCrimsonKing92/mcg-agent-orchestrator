using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture, local fixtures, and an explicit clock value.
// Expected text follows HumanWaitsTextView.cs at base 781c2fd21, reached by
// ConsoleViews.Attention.cs's human-waits forwarder at that commit.
public sealed class HumanWaitsTextViewCharacterizationTests
{
    [Theory]
    [InlineData(false, "Human waits: 0 active wait(s)")]
    [InlineData(true, "Human waits: 0 wait(s)")]
    public void Print_NoWaits_PreservesExactText(bool includeHistory, string heading)
    {
        var now = new DateTimeOffset(2026, 10, 30, 5, 0, 0, TimeSpan.Zero);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            HumanWaitsTextView.Print([], now, includeHistory));

        Assert.Equal(string.Join(Environment.NewLine, "", heading, "  none", "", ""), actual);
    }

    [Fact]
    public void Print_TwoWaits_PreservesExactText()
    {
        var now = new DateTimeOffset(2026, 10, 30, 5, 0, 0, TimeSpan.Zero);
        var taskWait = new HumanInputRequest(
            new HumanInputRequestId("aaaaaaaa00000000"), new GoalId("1111111100000000"),
            new TaskId("2222222200000000"), "Choose scope", now.AddSeconds(-95),
            HumanWaitKind.SpecClarification, isAutoDefaultable: false, isDismissible: false,
            isAnswerRequired: true, isExternallyBlocked: false);
        var goalWait = new HumanInputRequest(
            new HumanInputRequestId("bbbbbbbb00000000"), new GoalId("3333333300000000"),
            null, "Restore access", now.AddSeconds(10), HumanWaitKind.ExternalCredential,
            isAutoDefaultable: true, isDismissible: true, isAnswerRequired: false,
            isExternallyBlocked: true, resumeCommand: "provider login");

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            HumanWaitsTextView.Print([taskWait, goalWait], now));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Human waits: 2 active wait(s)",
            "  [SpecClarification] aaaaaaaa goal=11111111 task=22222222 age=95s",
            "    flags: auto-defaultable=False; dismissible=False; answer-required=True; externally-blocked=False",
            "    question: Choose scope",
            "    resume: attention answer 11111111 aaaaaaaa <answer>",
            "  [ExternalCredential] bbbbbbbb goal=33333333 task=goal age=0s",
            "    flags: auto-defaultable=True; dismissible=True; answer-required=False; externally-blocked=True",
            "    question: Restore access",
            "    resume: provider login",
            "", ""), actual);
    }
}
