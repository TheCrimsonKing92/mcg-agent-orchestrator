namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal enum OwnerConsolePane { Decisions, Board, Activity }

internal static class OwnerConsoleKeyHints
{
    internal static IReadOnlyList<(string Command, string Description)> Commands =>
        OwnerConsoleBindings.Commands.Select(row => (row.Usage, OwnerConsoleBindings.Description(row))).ToArray();

    internal static string CommandPrompt => OwnerConsoleBindings.CommandPrompt;
    internal static string CommandLineHint => "Enter run  Esc cancel  Tab complete  commands: " + CommandPrompt;

    internal static string UnavailableKey(char key, OwnerConsolePane pane) =>
        $"{key}: not available here ({pane.ToString().ToUpperInvariant()}); use DECISIONS.";

    internal static string DecisionDetailHint(bool canAnswer, bool canAcceptDefault) =>
        (canAnswer ? "r answer  " : "") + (canAcceptDefault ? "a accept default  " : "") +
        "Up/Down PgUp/PgDn scroll  ? help  Esc close";

    internal static string DecisionDetailHelpText(bool canAnswer, bool canAcceptDefault) => string.Join("\n",
        (canAnswer ? new[] { "r: Answer this decision." } : [])
        .Concat(canAcceptDefault ? ["a: Accept this decision's proposed default after confirmation."] : [])
        .Concat(["Up/Down: Scroll the detail.", "PgUp/PgDn: Scroll one page.",
            "Tab/Shift+Tab: Move between the detail and its buttons.", "Enter: Activate the focused button.",
            "?: Open this help dialog.", "Esc: Close the detail."]));

    internal static OwnerConsolePane Next(OwnerConsolePane pane) => pane switch
    {
        OwnerConsolePane.Decisions => OwnerConsolePane.Board,
        OwnerConsolePane.Board => OwnerConsolePane.Activity,
        OwnerConsolePane.Activity => OwnerConsolePane.Decisions,
        _ => throw new ArgumentOutOfRangeException(nameof(pane))
    };

    internal static OwnerConsolePane Previous(OwnerConsolePane pane) => pane switch
    {
        OwnerConsolePane.Decisions => OwnerConsolePane.Activity,
        OwnerConsolePane.Board => OwnerConsolePane.Decisions,
        OwnerConsolePane.Activity => OwnerConsolePane.Board,
        _ => throw new ArgumentOutOfRangeException(nameof(pane))
    };

    internal static string Hint(OwnerConsolePane pane, bool answerable = true, bool following = true) => OwnerConsoleBindings.PaneHint(pane, answerable, following);

    internal static string HelpText => OwnerConsoleBindings.HelpText;
}
