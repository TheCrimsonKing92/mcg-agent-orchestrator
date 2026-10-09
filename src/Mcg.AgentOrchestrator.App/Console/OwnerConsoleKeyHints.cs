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
        ("goal <id-prefix>", "Show goal status, stage and recent events.")
    ];

    internal static string CommandPrompt => string.Join(" | ", Commands.Select(item => item.Command));

    internal static OwnerConsolePane Next(OwnerConsolePane pane) => pane switch
    {
        OwnerConsolePane.Decisions => OwnerConsolePane.Board,
        OwnerConsolePane.Board => OwnerConsolePane.Activity,
        OwnerConsolePane.Activity => OwnerConsolePane.Decisions,
        _ => throw new ArgumentOutOfRangeException(nameof(pane))
    };

    internal static string Hint(OwnerConsolePane pane) => (pane switch
    {
        OwnerConsolePane.Decisions => "Enter detail  a accept default  r answer",
        OwnerConsolePane.Board => "Enter goal detail",
        OwnerConsolePane.Activity => "Up/Down scroll  Enter what this means",
        _ => throw new ArgumentOutOfRangeException(nameof(pane))
    }) + "  " + OwnerActivityNarrator.JumpKeyHint + "  Tab next pane  : command  ? help  q quit";

    internal static string HelpText => string.Join("\n", new[]
    {
        "Tab: Next pane (DECISIONS, BOARD, ACTIVITY).",
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
        ":epics: Open the epic view (same as e)."
    }.Concat(OwnerActivityNarrator.JumpKeyHelp).Concat(Commands.Select(item => $":{item.Command}: {item.Description}")));
}
