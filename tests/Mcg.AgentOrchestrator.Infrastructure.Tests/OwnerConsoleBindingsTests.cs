using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: immutable registry and per-test synthetic rows.
public sealed class OwnerConsoleBindingsTests
{
    [Fact]
    public void RegistrySatisfiesBindingSafetyRules() => Assert.Empty(OwnerConsoleBindings.Validate(OwnerConsoleBindings.All));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void OverlappingScopesRejectDuplicateKeys(int secondScope)
    {
        var violation = Assert.Single(OwnerConsoleBindings.Validate([Row("first", 'x'), Row("second", 'X', (OwnerConsoleScope)secondScope)]));
        Assert.Contains("'x'", violation);
        Assert.Contains("first", violation);
        Assert.Contains("second", violation);
    }

    [Fact]
    public void SeparateScopesRequireTheSameNoun()
    {
        var first = Row("first", 'x');
        var second = Row("second", 'x', OwnerConsoleScope.GoalDetail);
        var violation = Assert.Single(OwnerConsoleBindings.Validate([first, second]));
        Assert.Contains("'x'", violation);
        Assert.Contains("first", violation);
        Assert.Contains("second", violation);
        Assert.Empty(OwnerConsoleBindings.Validate([first, second with { Noun = first.Noun }]));
    }

    [Fact]
    public void KeyRequiresACommand()
    {
        var violation = Assert.Single(OwnerConsoleBindings.Validate([Row("", 'x') with { Usage = "missing" }]));
        Assert.Contains("'x'", violation);
        Assert.Contains("missing", violation);
        Assert.Contains("no command", violation);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ExternalKeysRequireSelectionScopeAndConfirmation(bool global, bool confirms)
    {
        var row = Row("external", 'x', global ? OwnerConsoleScope.Global : OwnerConsoleScope.Decisions)
            with { Effect = OwnerConsoleEffect.External, Confirms = confirms };
        var violation = Assert.Single(OwnerConsoleBindings.Validate([row]));
        Assert.Contains("'x'", violation);
        Assert.Contains("external", violation);
        Assert.Contains("unsafe", violation);
        Assert.Empty(OwnerConsoleBindings.Validate([row with { Scope = OwnerConsoleScope.Decisions, Confirms = true }]));
    }

    [Theory]
    [InlineData('j')]
    [InlineData('k')]
    [InlineData('/')]
    [InlineData(':')]
    [InlineData('?')]
    [InlineData('q')]
    public void ReservedKeysRejectUnrelatedCommands(char key)
    {
        var violation = Assert.Single(OwnerConsoleBindings.Validate([Row("unrelated", key)]));
        Assert.Contains($"'{key}'", violation);
        Assert.Contains("unrelated", violation);
        Assert.Contains("reserved", violation);
    }

    [Fact]
    public void LetterCapCountsDistinctTopLevelLetters()
    {
        var rows = "bcdefghilmn".Select((key, index) => Row("cmd" + index, key,
            index % 2 == 0 ? OwnerConsoleScope.Global : OwnerConsoleScope.Decisions)).ToArray();
        var violation = Assert.Single(OwnerConsoleBindings.Validate(rows));
        Assert.Contains("11", violation);
        foreach (var row in rows) Assert.Contains(row.Key!.Value.ToString(), violation);
        Assert.Empty(OwnerConsoleBindings.Validate(rows[..10]));
        Assert.Empty(OwnerConsoleBindings.Validate([.. rows[..10], Row("dialog", 'x', OwnerConsoleScope.GoalDetail)]));
    }

    [Fact]
    public void LookupUsesScopeCaseAndTheFirstCommandWord()
    {
        Assert.Equal(OwnerConsoleBindings.All.Single(row => row.Command == "landings"), OwnerConsoleBindings.ForKey('L', OwnerConsolePane.Activity));
        Assert.Null(OwnerConsoleBindings.ForKey('a', OwnerConsolePane.Board));
        Assert.NotNull(OwnerConsoleBindings.ForKey('A', OwnerConsolePane.Decisions));
        Assert.True(OwnerConsoleBindings.IsTopLevelKey('A'));
        Assert.False(OwnerConsoleBindings.IsTopLevelKey('x'));
        Assert.Equal(OwnerConsoleBindings.Gate, OwnerConsoleBindings.ForLine("  : GaTe trailing arguments  "));
        Assert.Null(OwnerConsoleBindings.ForLine(" : "));
        Assert.Null(OwnerConsoleBindings.ForLine("unknown"));
    }

    [Fact]
    public void PromptPreservesCommandOrderAndAdvertisesAliases()
    {
        const string expected = "conductor start | conductor stop | conductor status | digest | metrics | bell on | bell off | goal <id-prefix> | [e] epics | fleet | follow | [l] landings | [g] gate | [?] help | [q] quit | [a] accept | [r] answer";
        Assert.Equal(expected, OwnerConsoleBindings.CommandPrompt);
        Assert.Equal(expected.Split(" | "), OwnerConsoleBindings.PromptEntries);
        Assert.Equal(expected, OwnerConsoleKeyHints.CommandPrompt);
    }

    [Fact]
    public void HelpPreservesNavigationAndPairsEveryAliasWithItsCommand()
    {
        string[] expected =
        [
            "Tab: Next pane (DECISIONS, BOARD, ACTIVITY).",
            "Shift+Tab: Previous pane (ACTIVITY, BOARD, DECISIONS).",
            "Up: Select the previous row in DECISIONS or BOARD; scroll ACTIVITY up.",
            "Down: Select the next row in DECISIONS or BOARD; scroll ACTIVITY down.",
            "Enter: Open detail in DECISIONS or BOARD, or What this means in ACTIVITY; submit a command or activate a dialog button.",
            ": (any pane): Open the command line.",
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
            "Epic view Ctrl+R (list and detail): Reload the epic view.",
            "l: Show delivered goals (any pane).",
            ":landings: Show delivered goals: landing commit, integration kind and acceptance (landings today|30) (same as l).",
            "g: Open the GATE view (any pane).",
            ":gate: Open the GATE view (gate <goal-id-prefix> opens the gate that goal is in) (same as g).",
            "GATE view Up/Down PgUp/PgDn: Scroll lanes; Esc: Close the GATE view.",
            "Goal detail d: Open the goal's open question.",
            "Goal detail f: Show the goal's last failure.",
            "Goal detail e: Open the goal's epic.",
            "Goal detail g: Open the GATE view on this goal's gate (same as :gate <goal-id-prefix>).",
            "Goal detail q: Back out of goal detail.",
            "?: Open this help dialog (any pane).",
            ":help: Open this help dialog (same as ?).",
            "q: Quit the console (any pane).",
            ":quit: Quit the console (same as q).",
            "a: Accept the selected default in DECISIONS after confirmation.",
            ":accept: Accept the selected default in DECISIONS after confirmation (same as a).",
            "r: Answer the selected decision in DECISIONS.",
            ":answer: Answer the selected decision in DECISIONS (same as r).",
            "Home/End: Select the first/last row in DECISIONS, BOARD or ACTIVITY.",
            "PgUp/PgDn: Move the selection one visible page up/down in DECISIONS, BOARD or ACTIVITY."
        ];
        string[] unkeyed =
        [
            ":conductor start: Start the conductor.",
            ":conductor stop: Stop the conductor.",
            ":conductor status: Show conductor status.",
            ":digest: Show the owner digest.",
            ":metrics: Show conductor metrics.",
            ":bell on: Enable all three bell tiers; use bell <calm|attention|alarm> on|off for one tier.",
            ":bell off: Disable all three bell tiers; use bell <calm|attention|alarm> on|off for one tier.",
            ":goal <id-prefix>: Open live goal detail, landing status and actions.",
            ":fleet: Show the fleet diagram: host and executor boxes.",
            ":follow: Follow the newest ACTIVITY row; new events keep it selected until you move the selection or scroll (Home in ACTIVITY does the same)."
        ];
        Assert.Equal(string.Join("\n", expected.Concat(unkeyed)), OwnerConsoleBindings.HelpText);
        var lines = OwnerConsoleBindings.HelpText.Split('\n');
        foreach (var row in OwnerConsoleBindings.Commands.Where(row => row.Key.HasValue))
        {
            var commandLine = Assert.Single(lines, line => line.StartsWith($":{row.Usage}: ", StringComparison.Ordinal));
            Assert.Equal(1, commandLine.Split($"(same as {row.Key})", StringSplitOptions.None).Length - 1);
            Assert.Equal(row.KeyHelp, lines[Array.IndexOf(lines, commandLine) - 1]);
        }
        Assert.Equal(OwnerConsoleBindings.HelpText, OwnerConsoleKeyHints.HelpText);
    }

    [Theory]
    [InlineData(0, true, "Enter detail  a accept default  r answer")]
    [InlineData(0, false, "Enter detail")]
    [InlineData(1, false, "Enter goal detail")]
    [InlineData(2, false, "Following latest  Up/Down scroll  Enter what this means")]
    public void PaneHintsCombineSelectionActionsAndGlobalViews(int pane, bool answerable, string prefix)
    {
        const string suffix = "  Home/End first/last  PgUp/PgDn page  Tab/Shift+Tab pane  e epics  l landings  g gate  : command  ? help  q quit";
        Assert.Equal(prefix + suffix, OwnerConsoleBindings.PaneHint((OwnerConsolePane)pane, answerable));
        Assert.Equal(prefix + suffix, OwnerConsoleKeyHints.Hint((OwnerConsolePane)pane, answerable));
    }

    private static OwnerConsoleBinding Row(string command, char key, OwnerConsoleScope scope = OwnerConsoleScope.Global) =>
        new(command, command, key, scope, OwnerConsoleEffect.View, OwnerConsoleCommandOwner.View, false,
            command, command, command, command);
}
