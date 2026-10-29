using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: immutable registry and isolated synthetic rows.
public sealed class OwnerConsoleDialogBindingsTests
{
    [Fact]
    public void DialogRows_ApprovedKeys_ValidateWithoutViolations()
    {
        Assert.Empty(OwnerConsoleBindings.Validate(OwnerConsoleBindings.All));
        Assert.Equal(new[] { "d", "f", "e", "g", "q" }, Keys(OwnerConsoleScope.GoalDetail));
        Assert.Equal(new[] { "w", "Ctrl+R", "q" }, Keys(OwnerConsoleScope.EpicView));
        Assert.Equal(new[] { "q" }, Keys(OwnerConsoleScope.GateView));
        Assert.Single(OwnerConsoleBindings.All.Where(row => row.Scope == OwnerConsoleScope.GateView && row.Key is null));
        Assert.All(OwnerConsoleDialogBindings.All, row => Assert.False(string.IsNullOrWhiteSpace(row.KeyHelp)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateKeys_SameScope_ReportOneCollision(bool ctrl)
    {
        var scope = ctrl ? OwnerConsoleScope.EpicView : OwnerConsoleScope.GoalDetail;
        var key = ctrl ? 'r' : 'd';
        CheckViolation([Row("first", key, scope, ctrl), Row("second", char.ToUpperInvariant(key), scope, ctrl)],
            ctrl ? "Ctrl+R" : "d", "first", "second");
    }

    [Fact]
    public void SharedLetter_DifferentNouns_RejectsBothScopes()
    {
        var goal = Row("question", 'd', OwnerConsoleScope.GoalDetail);
        var global = Row("decisions", 'd', OwnerConsoleScope.Global);
        CheckViolation([goal, global], "d", "question", "decisions");
        Assert.Empty(OwnerConsoleBindings.Validate([goal, global with { Noun = goal.Noun }]));
        CheckViolation([Row("answer", 'r', OwnerConsoleScope.Decisions), Row("reload", 'r', OwnerConsoleScope.EpicView)],
            "r", "answer", "reload");
        Assert.Empty(OwnerConsoleBindings.Validate([
            OwnerConsoleBindings.ForKey('r', OwnerConsolePane.Decisions)!, OwnerConsoleDialogBindings.Reload]));
    }

    [Fact]
    public void GoalQuit_UnrelatedCommand_NamesReservedCommand()
    {
        CheckViolation([Row("question", 'q', OwnerConsoleScope.GoalDetail)], "q", "question", "quit");
    }

    [Fact]
    public void TypedSurfaces_DialogRowsExcluded_PreserveTopLevelCommands()
    {
        const string prompt = "conductor start | conductor stop | conductor status | digest | metrics | bell on | bell off | goal <id-prefix> | [e] epics | fleet | scope [<name>[,<name>...]|all] | follow | [/] filter <text> | [l] landings | [g] gate | [?] help | [q] quit | [a] accept | [r] answer";
        Assert.Equal(prompt, OwnerConsoleBindings.CommandPrompt);
        Assert.Equal(prompt.Split(" | "), OwnerConsoleBindings.PromptEntries);
        Assert.Equal(19, OwnerConsoleKeyHints.Commands.Count);
        Assert.Null(OwnerConsoleBindings.ForLine("d"));
        Assert.Null(OwnerConsoleBindings.ForLine("w"));
        foreach (var row in OwnerConsoleDialogBindings.All.Where(row => row.Command is "question" or "failure" or "window" or "reload"))
            Assert.Null(OwnerConsoleBindings.ForLine(row.Command));
        Assert.Equal(OwnerConsoleBindings.Quit, OwnerConsoleBindings.ForLine("quit"));
        Assert.All(new[] { OwnerConsoleBindings.Epics, OwnerConsoleBindings.Gate, OwnerConsoleBindings.Help, OwnerConsoleBindings.Quit },
            row => Assert.Equal(OwnerConsoleScope.Global, row.Scope));
    }

    [Fact]
    public void DialogLookup_CaseAndControl_KeepReloadDistinct()
    {
        Assert.Equal(OwnerConsoleDialogBindings.Reload, OwnerConsoleBindings.ForDialogKey(OwnerConsoleScope.EpicView, 'R', true));
        Assert.Equal(OwnerConsoleDialogBindings.Reload, OwnerConsoleBindings.ForDialogKey(OwnerConsoleScope.EpicView, 'r', true));
        Assert.Null(OwnerConsoleBindings.ForDialogKey(OwnerConsoleScope.EpicView, 'r', false));
        Assert.Null(OwnerConsoleBindings.ForDialogKey(OwnerConsoleScope.GoalDetail, 'd', true));
        Assert.Equal("question", OwnerConsoleBindings.ForDialogKey(OwnerConsoleScope.GoalDetail, 'D', false)!.Command);
    }

    [Fact]
    public void Help_DialogRows_EmitApprovedOrderAndReloadLabel()
    {
        var lines = OwnerConsoleBindings.HelpText.Split('\n');
        var gate = Array.IndexOf(lines, "GATE view Up/Down PgUp/PgDn: Scroll lanes; Esc: Close the GATE view.");
        Assert.True(gate >= 0);
        Assert.Equal(new[] { "Goal detail d: Open the goal's open question.", "Goal detail f: Show the goal's last failure.",
            "Goal detail e: Open the goal's epic.", "Goal detail g: Open the GATE view on this goal's gate (same as :gate <goal-id-prefix>).",
            "Goal detail q: Back out of goal detail." }, lines.Skip(gate + 1).Take(5));
        Assert.Contains("Epic view Ctrl+R (list and detail): Reload the epic view.", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("Epic view r", StringComparison.Ordinal));
    }

    private static IEnumerable<string> Keys(OwnerConsoleScope scope) => OwnerConsoleBindings.All
        .Where(row => row.Scope == scope && row.Key.HasValue).Select(OwnerConsoleDialogBindings.KeyLabel);

    private static OwnerConsoleBinding Row(string command, char key, OwnerConsoleScope scope, bool ctrl = false) =>
        new(command, command, key, scope, OwnerConsoleEffect.View, OwnerConsoleCommandOwner.View, false,
            command, command, command, command, ctrl);

    private static void CheckViolation(IReadOnlyList<OwnerConsoleBinding> rows, string key, string first, string second)
    {
        var violation = Assert.Single(OwnerConsoleBindings.Validate(rows));
        Assert.Contains($"'{key}'", violation);
        Assert.Contains($"'{first}'", violation);
        Assert.Contains($"'{second}'", violation);
    }
}
