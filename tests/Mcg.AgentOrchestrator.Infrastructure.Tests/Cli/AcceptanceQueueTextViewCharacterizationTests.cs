using System.Reflection;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: Capture routes output through AsyncLocal; fixtures have no shared state.
public sealed class AcceptanceQueueTextViewCharacterizationTests
{
    // Expected text derived from ConsoleViews.AcceptanceQueue.cs before the extraction.
    [Fact]
    public void Print_EmptyQueue_PreservesBlankLinesAndNone()
    {
        var plan = new AcceptanceQueuePlan(AutonomyPolicy.SupervisedAuto, []);
        var actual = AsyncLocalConsoleRouter.Capture(() => AcceptanceQueueTextView.PrintAcceptanceQueuePlan(plan));
        var expected = string.Join(Environment.NewLine,
            "",
            "Acceptance queue: 0 goal(s), ready=0, held=0, blocked=0, policy=supervised-auto",
            "  none",
            "",
            "");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Print_AllDispositions_PreservesCountsAndDirtyStates()
    {
        var plan = new AcceptanceQueuePlan(AutonomyPolicy.SupervisedAuto,
        [
            Item("ready", AcceptanceQueueDisposition.Ready, false, true, "Ready objective.", "Ready reason."),
            Item("held", AcceptanceQueueDisposition.Held, true, false, "Held objective.", "Held reason."),
            Item("blocked", AcceptanceQueueDisposition.Blocked, null, true, "Blocked objective.", "Blocked reason.")
        ]);
        var actual = AsyncLocalConsoleRouter.Capture(() => AcceptanceQueueTextView.PrintAcceptanceQueuePlan(plan));
        var expected = string.Join(Environment.NewLine,
            "",
            "Acceptance queue: 3 goal(s), ready=1, held=1, blocked=1, policy=supervised-auto",
            "  ready: Ready status=Active branch=goal/ready diff=True dirty=False",
            "     objective: Ready objective.",
            "     reason: Ready reason.",
            "     command: accept ready",
            "  held: Held status=Active branch=goal/held diff=False dirty=True",
            "     objective: Held objective.",
            "     reason: Held reason.",
            "     command: accept held",
            "  blocked: Blocked status=Active branch=goal/blocked diff=True dirty=unknown",
            "     objective: Blocked objective.",
            "     reason: Blocked reason.",
            "     command: accept blocked",
            "",
            "");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Print_LongObjectiveAndReason_PreservesShortenedText()
    {
        var longText = new string('h', 420) + new string('m', 20) + new string('t', 180);
        var plan = new AcceptanceQueuePlan(AutonomyPolicy.SupervisedAuto,
            [Item("long", AcceptanceQueueDisposition.Ready, false, true, longText, longText)]);
        var actual = AsyncLocalConsoleRouter.Capture(() => AcceptanceQueueTextView.PrintAcceptanceQueuePlan(plan));
        var expected = string.Join(Environment.NewLine,
            "",
            "Acceptance queue: 1 goal(s), ready=1, held=0, blocked=0, policy=supervised-auto",
            "  long: Ready status=Active branch=goal/long diff=True dirty=False",
            "     objective: hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh",
            "",
            "[truncated 20 chars]",
            "",
            "tttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttt",
            "     reason: hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh",
            "",
            "[truncated 20 chars]",
            "",
            "tttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttt",
            "     command: accept long",
            "",
            "");
        Assert.Equal(expected, actual);
    }

    private static AcceptanceQueueItem Item(string prefix, AcceptanceQueueDisposition disposition,
        bool? dirty, bool diff, string objective, string reason) => new(
        GoalId.New(), prefix, objective, GoalStatus.Active, disposition, "goal/" + prefix,
        null, diff, dirty, true, false, reason, "accept " + prefix);

    [Fact]
    public void ExtractedOwner_LeavesNoForwarderOnConsoleViews()
    {
        var root = VerifiedRepositoryRoot.Find();
        var cliRoot = Path.Combine(root, "src", "Mcg.AgentOrchestrator.App", "Cli");
        Assert.False(File.Exists(Path.Combine(cliRoot, "ConsoleViews.AcceptanceQueue.cs")));
        var source = File.ReadAllText(Path.Combine(cliRoot, "AcceptanceQueueTextView.cs"));
        Assert.Contains("internal static class AcceptanceQueueTextView", source);
        Assert.DoesNotMatch(@"\bpartial\s+class\b", source);

        var members = typeof(ConsoleViews).GetMembers(
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(members, member => member.Name == "PrintAcceptanceQueuePlan");

        // Scan every ConsoleViews partial, including ConsoleViews.cs, as the ratchet does.
        var partials = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .Select(File.ReadAllText)
            .Where(text => Regex.IsMatch(text, @"\bpartial\s+class\s+ConsoleViews\b", RegexOptions.CultureInvariant))
            .ToArray();
        Assert.True(partials.Length <= 36);
        foreach (var text in partials)
            Assert.DoesNotMatch(@"\bAcceptanceQueueTextView\s*\.", text);
    }
}
