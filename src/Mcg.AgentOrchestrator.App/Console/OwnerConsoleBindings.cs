namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleBindings
{
    internal static IReadOnlyList<OwnerConsoleBinding> All { get; } = Array.AsReadOnly<OwnerConsoleBinding>(
    [
        Row("conductor start", "Start the conductor.", effect: OwnerConsoleEffect.External),
        Row("conductor stop", "Stop the conductor.", effect: OwnerConsoleEffect.External),
        Row("conductor status", "Show conductor status."),
        Row("digest", "Show the owner digest."),
        Row("metrics", "Show conductor metrics."),
        Row("bell on", "Enable all three bell tiers; use bell <calm|attention|alarm> on|off for one tier.", effect: OwnerConsoleEffect.ConsoleLocal),
        Row("bell off", "Disable all three bell tiers; use bell <calm|attention|alarm> on|off for one tier.", effect: OwnerConsoleEffect.ConsoleLocal),
        Row("goal <id-prefix>", "Open live goal detail, landing status and actions."),
        Row("epics", "Open the epic view", 'e', "Open the epic view (any pane).", owner: OwnerConsoleCommandOwner.View),
        Row("fleet", "Show the fleet diagram: host and executor boxes."),
        Row("follow", "Follow the newest ACTIVITY row; new events keep it selected until you move the selection or scroll (Home in ACTIVITY does the same).", owner: OwnerConsoleCommandOwner.View),
        Row("landings", "Show delivered goals: landing commit, integration kind and acceptance (landings today|30)", 'l', "Show delivered goals (any pane)."),
        Row("gate", "Open the GATE view (gate <goal-id-prefix> opens the gate that goal is in)", 'g', "Open the GATE view (any pane).", owner: OwnerConsoleCommandOwner.View),
        Row("help", "Open this help dialog", '?', "Open this help dialog (any pane).", owner: OwnerConsoleCommandOwner.View),
        Row("quit", "Quit the console", 'q', "Quit the console (any pane).", owner: OwnerConsoleCommandOwner.View),
        Row("accept", "Accept the selected default in DECISIONS after confirmation", 'a', "Accept the selected default in DECISIONS after confirmation.",
            OwnerConsoleScope.Decisions, OwnerConsoleEffect.External, OwnerConsoleCommandOwner.View, true, "accept default"),
        Row("answer", "Answer the selected decision in DECISIONS", 'r', "Answer the selected decision in DECISIONS.",
            OwnerConsoleScope.Decisions, OwnerConsoleEffect.External, OwnerConsoleCommandOwner.View, true),
        .. OwnerConsoleDialogBindings.All
    ]);

    private static OwnerConsoleBinding Row(string usage, string help, char? key = null, string? keyHelp = null,
        OwnerConsoleScope scope = OwnerConsoleScope.Global, OwnerConsoleEffect effect = OwnerConsoleEffect.View,
        OwnerConsoleCommandOwner owner = OwnerConsoleCommandOwner.Controller, bool confirms = false, string? hint = null)
    {
        var command = usage.Split(' ')[0];
        return new(command, usage, key, scope, effect, owner, confirms, command,
            key is null ? null : hint ?? command, key is null ? null : $"{key}: {keyHelp}", help);
    }

    internal static OwnerConsoleBinding Epics => All.Single(row => row.Scope == OwnerConsoleScope.Global && row.Command == "epics");
    internal static OwnerConsoleBinding Gate => All.Single(row => row.Scope == OwnerConsoleScope.Global && row.Command == "gate");
    internal static OwnerConsoleBinding Help => All.Single(row => row.Scope == OwnerConsoleScope.Global && row.Command == "help");
    internal static OwnerConsoleBinding Quit => All.Single(row => row.Scope == OwnerConsoleScope.Global && row.Command == "quit");
    internal static OwnerConsoleBinding Follow => All.Single(row => row.Scope == OwnerConsoleScope.Global && row.Command == "follow");
    internal static IEnumerable<OwnerConsoleBinding> Commands => All.Where(row => TopLevel(row.Scope));
    internal static OwnerConsoleBinding? ForDialogKey(OwnerConsoleScope scope, char key, bool ctrl = false) => All.FirstOrDefault(row =>
        !TopLevel(scope) && row.Scope == scope && row.Ctrl == ctrl && row.Key is { } bound &&
        char.ToLowerInvariant(bound) == char.ToLowerInvariant(key));

    private static bool TopLevel(OwnerConsoleScope scope) => scope is OwnerConsoleScope.Global or OwnerConsoleScope.Decisions;
    internal static OwnerConsoleBinding? ForKey(char key, OwnerConsolePane pane) => All.FirstOrDefault(row =>
        !row.Ctrl && row.Key is { } bound && char.ToLowerInvariant(bound) == char.ToLowerInvariant(key) &&
        (row.Scope == OwnerConsoleScope.Global || row.Scope == OwnerConsoleScope.Decisions && pane == OwnerConsolePane.Decisions));
    internal static bool IsTopLevelKey(char key) => All.Any(row => TopLevel(row.Scope) &&
        row.Key is { } bound && char.ToLowerInvariant(bound) == char.ToLowerInvariant(key));
    internal static OwnerConsoleBinding? ForLine(string line)
    {
        var text = line.Trim();
        if (text.StartsWith(':')) text = text[1..].TrimStart();
        var command = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return Commands.FirstOrDefault(row => row.Command.Equals(command, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<string> Validate(IReadOnlyList<OwnerConsoleBinding> rows)
    {
        var violations = new List<string>();
        var keyed = rows.Where(row => row.Key.HasValue).ToArray();
        for (var index = 0; index < keyed.Length; index++)
        {
            var row = keyed[index];
            var key = char.ToLowerInvariant(row.Key!.Value);
            var label = OwnerConsoleDialogBindings.KeyLabel(row);
            if (string.IsNullOrWhiteSpace(row.Command)) violations.Add($"Key '{key}' on '{row.Usage}' has no command.");
            if (row.Effect == OwnerConsoleEffect.External && (row.Scope == OwnerConsoleScope.Global || !row.Confirms))
                violations.Add($"Key '{key}' on '{row.Command}' ({row.Usage}) is unsafe for an External effect.");
            if (!row.Ctrl && (key is 'j' or 'k' or '/' or ':' || key == '?' && row.Command != "help" || key == 'q' && row.Command != "quit"))
                violations.Add($"Key '{key}' on '{row.Command}' ({row.Usage}) is reserved" + (key == 'q' ? " for 'quit'." : "."));
            foreach (var other in keyed.Skip(index + 1).Where(other => char.ToLowerInvariant(other.Key!.Value) == key && other.Ctrl == row.Ctrl))
            {
                if (row.Scope == other.Scope || TopLevel(row.Scope) && TopLevel(other.Scope))
                    violations.Add($"Key '{label}' collides between '{row.Command}' and '{other.Command}'.");
                else if (!row.Noun.Equals(other.Noun, StringComparison.OrdinalIgnoreCase))
                    violations.Add($"Key '{label}' has different meanings for '{row.Command}' and '{other.Command}'.");
            }
        }
        var letters = keyed.Where(row => TopLevel(row.Scope)).Select(row => char.ToLowerInvariant(row.Key!.Value))
            .Where(char.IsLetter).Distinct().Order().ToArray();
        if (letters.Length > 10) violations.Add($"Top-level letter cap exceeded: {letters.Length} keys ({string.Join(", ", letters)}); maximum 10.");
        return violations;
    }

    internal static string Description(OwnerConsoleBinding row) => row.Key is { } key
        ? $"{row.CommandHelp.TrimEnd('.')} (same as {key})." : row.CommandHelp;
    internal static IReadOnlyList<string> PromptEntries => Commands.Select(row =>
        row.Key is { } key ? $"[{key}] {row.Usage}" : row.Usage).ToArray();
    internal static string CommandPrompt => string.Join(" | ", PromptEntries);

    internal static string PaneHint(OwnerConsolePane pane, bool answerable = true, bool following = true)
    {
        var prefix = pane switch
        {
            OwnerConsolePane.Decisions => "Enter detail" + (answerable ? "  " + string.Join("  ", All
                .Where(row => row.Scope == OwnerConsoleScope.Decisions && row.Key.HasValue).Select(Hint)) : ""),
            OwnerConsolePane.Board => "Enter goal detail",
            OwnerConsolePane.Activity => (following ? "Following latest  " : "Inspecting  Home follow latest  ") + "Up/Down scroll  Enter what this means",
            _ => throw new ArgumentOutOfRangeException(nameof(pane))
        };
        return string.Join("  ", new[] { prefix, OwnerActivityNarrator.JumpKeyHint, "Tab/Shift+Tab pane" }
            .Concat(All.Where(row => row.Scope == OwnerConsoleScope.Global && row.Key.HasValue)
                .SelectMany(row => row == Help ? new[] { ": command", Hint(row) } : [Hint(row)])));
    }

    private static string Hint(OwnerConsoleBinding row) => $"{row.Key} {row.HintLabel}";

    internal static string HelpText => string.Join("\n", NavigationHelp
        .Concat(Commands.Where(row => row.Key.HasValue).SelectMany(row =>
            new[] { row.KeyHelp!, $":{row.Usage}: {Description(row)}" }.Concat(ExtraHelp(row))))
        .Concat(OwnerActivityNarrator.JumpKeyHelp)
        .Concat(Commands.Where(row => row.Key is null).Select(row => $":{row.Usage}: {Description(row)}")));

    private static readonly string[] NavigationHelp =
    [
        "Tab: Next pane (DECISIONS, BOARD, ACTIVITY).",
        "Shift+Tab: Previous pane (ACTIVITY, BOARD, DECISIONS).",
        "Up: Select the previous row in DECISIONS or BOARD; scroll ACTIVITY up.",
        "Down: Select the next row in DECISIONS or BOARD; scroll ACTIVITY down.",
        "Enter: Open detail in DECISIONS or BOARD, or What this means in ACTIVITY; submit a command or activate a dialog button.",
        ": (any pane): Open the command line.",
        "Esc: Cancel command entry or close a dialog."
    ];

    private static string[] ExtraHelp(OwnerConsoleBinding row) => row == Epics ?
    [
        "Epic list Up: Select the previous epic.",
        "Epic list Down: Select the next epic.",
        "Epic list Enter: Open the selected epic's detail.",
        "Epic list Esc: Close the epic view.",
        "Epic detail Up: Scroll up one line.",
        "Epic detail Down: Scroll down one line.",
        "Epic detail PgUp: Scroll up one page.",
        "Epic detail PgDn: Scroll down one page.",
        "Epic detail Esc: Back to the epic list.",
        .. OwnerConsoleDialogBindings.All.Where(binding => binding.Scope == OwnerConsoleScope.EpicView && binding.Command != "quit")
            .Select(binding => binding.KeyHelp!)
    ] : row == Gate ? [OwnerConsoleDialogBindings.GateHelp.KeyHelp!, .. OwnerConsoleDialogBindings.GoalDetail.Select(binding => binding.KeyHelp!)] : [];
}
