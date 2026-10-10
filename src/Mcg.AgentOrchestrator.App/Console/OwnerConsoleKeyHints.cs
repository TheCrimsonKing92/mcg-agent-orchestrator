namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal enum OwnerConsolePane { Decisions, Board, Activity }

internal static class OwnerConsoleKeyHints
{
    internal static IReadOnlyList<(string Command, string Description)> Commands { get; } =
    [
        ("conductor start", "Start the conductor."),
        ("conductor stop", "Stop the conductor."),
        ("conductor status", "Show conductor status."),
        ("digest", "Show the owner digest."),
        ("metrics", "Show conductor metrics."),
        ("bell on", "Enable the attention bell."),
        ("bell off", "Disable the attention bell."),
        ("goal <id-prefix>", "Open live goal detail, landing status and actions.")
    ];

    internal static string CommandPrompt => string.Join(" | ", Commands.Select(item => item.Command));

    internal static string CommandLineHint => "Enter run  Esc cancel  commands: " + CommandPrompt + " | epics";

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

    internal static string Hint(OwnerConsolePane pane, bool answerable = true) => (pane switch
    {
        OwnerConsolePane.Decisions => "Enter detail" + (answerable ? "  a accept default  r answer" : ""),
        OwnerConsolePane.Board => "Enter goal detail",
        OwnerConsolePane.Activity => "Up/Down scroll  Enter what this means",
        _ => throw new ArgumentOutOfRangeException(nameof(pane))
    }) + "  " + OwnerActivityNarrator.JumpKeyHint + "  Tab/Shift+Tab pane  : command  ? help  q quit";

    internal static string HelpText => string.Join("\n", new[]
    {
        "Tab: Next pane (DECISIONS, BOARD, ACTIVITY).",
        "Shift+Tab: Previous pane (ACTIVITY, BOARD, DECISIONS).",
        "Up: Select the previous row in DECISIONS or BOARD; scroll ACTIVITY up.",
        "Down: Select the next row in DECISIONS or BOARD; scroll ACTIVITY down.",
        "Enter: Open detail in DECISIONS or BOARD, or What this means in ACTIVITY; submit a command or activate a dialog button.",
        "a: Accept the selected default in DECISIONS after confirmation.",
        "r: Answer the selected decision in DECISIONS.",
        ": (any pane): Open the command line.",
        "?: Open this help dialog (any pane).",
        "q: Quit the console (any pane).",
        "Esc: Cancel command entry or close a dialog.",
        "e: Open the epic view (any pane).",
        ":epics: Open the epic view (same as e).",
        "Epic list Up: Select the previous epic.",
        "Epic list Down: Select the next epic.",
        "Epic list Enter: Open the selected epic's detail.",
        "Epic list Esc: Close the epic view.",
        "Epic detail Up: Scroll up one line.",
        "Epic detail Down: Scroll down one line.",
        "Epic detail PgUp: Scroll up one page.",
        "Epic detail PgDn: Scroll down one page.",
        "Epic detail Esc: Back to the epic list.",
        "Epic view w (list and detail): Cycle the time window (day, week, all time).",
        "Epic view r (list and detail): Reload after a failed load."
    }.Concat(OwnerActivityNarrator.JumpKeyHelp).Concat(Commands.Select(item => $":{item.Command}: {item.Description}")));
}
