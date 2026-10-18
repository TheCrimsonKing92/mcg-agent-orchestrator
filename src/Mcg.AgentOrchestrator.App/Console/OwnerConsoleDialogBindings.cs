namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// View-local intents share key identity and display text with the console registry.
internal static class OwnerConsoleDialogBindings
{
    internal static IReadOnlyList<OwnerConsoleBinding> All { get; } = Array.AsReadOnly<OwnerConsoleBinding>(
    [
        Row(OwnerConsoleScope.GoalDetail, 'd', "question", "Open question", "Goal detail d: Open the goal's open question."),
        Row(OwnerConsoleScope.GoalDetail, 'f', "failure", "Last failure", "Goal detail f: Show the goal's last failure."),
        Row(OwnerConsoleScope.GoalDetail, 'e', "epics", "Epic", "Goal detail e: Open the goal's epic."),
        Row(OwnerConsoleScope.GoalDetail, 'q', "quit", "Back", "Goal detail q: Back out of goal detail."),
        Row(OwnerConsoleScope.EpicView, 'w', "window", "Window", "Epic view w (list and detail): Cycle the time window (day, week, all time)."),
        Row(OwnerConsoleScope.EpicView, 'r', "reload", "Reload", "Epic view Ctrl+R (list and detail): Reload the epic view.", ctrl: true),
        Row(OwnerConsoleScope.EpicView, 'q', "quit", "Back", "Epic view q: Back out of the epic view."),
        Row(OwnerConsoleScope.GateView, 'q', "quit", "Back", "GATE view q: Back out of the GATE view."),
        Row(OwnerConsoleScope.GateView, null, "gate", "Up/Down PgUp/PgDn scroll  Enter expand passed and baseline  Esc close",
            "GATE view Up/Down PgUp/PgDn: Scroll lanes; Esc: Close the GATE view.")
    ]);

    internal static IEnumerable<OwnerConsoleBinding> GoalDetail => All.Where(row => row.Scope == OwnerConsoleScope.GoalDetail);
    internal static OwnerConsoleBinding Reload => All.Single(row => row.Scope == OwnerConsoleScope.EpicView && row.Command == "reload");
    internal static OwnerConsoleBinding GateHelp => All.Single(row => row.Scope == OwnerConsoleScope.GateView && row.Key is null);
    internal static string KeyLabel(OwnerConsoleBinding row) => row.Ctrl ? $"Ctrl+{char.ToUpperInvariant(row.Key!.Value)}" : $"{row.Key}";
    internal static string Label(OwnerConsoleBinding row) => $"{row.HintLabel} ({KeyLabel(row)})";

    private static OwnerConsoleBinding Row(OwnerConsoleScope scope, char? key, string command, string label, string help, bool ctrl = false) =>
        new(command, command, key, scope, OwnerConsoleEffect.View, OwnerConsoleCommandOwner.View, false,
            command, label, help, help, ctrl);
}
