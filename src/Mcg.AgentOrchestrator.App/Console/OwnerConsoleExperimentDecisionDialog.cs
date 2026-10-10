using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleExperimentDecisionDialog : Dialog
{
    internal OwnerExperimentDecisionForm? Result { get; private set; }
    internal event Action? CloseRequested;

    internal OwnerConsoleExperimentDecisionDialog(string title, string text, string defaultEvidence)
    {
        Title = title; Width = Dim.Percent(85); Height = Dim.Percent(80);
        var description = new TextView
        { Text = text, ReadOnly = true, Width = Dim.Fill(), Height = Dim.Fill(10) };
        var outcome = new OptionSelector
        { Y = Pos.Bottom(description), Labels = ["keep", "revert", "inconclusive"], Value = 0 };
        var evidenceLabel = new Label { Y = Pos.Bottom(outcome), Text = "Evidence reference" };
        var evidence = new TextField
        { Y = Pos.Bottom(evidenceLabel), Width = Dim.Fill(), Height = 1, Text = defaultEvidence };
        var actionLabel = new Label { Y = Pos.Bottom(evidence), Text = "Action" };
        var action = new TextField { Y = Pos.Bottom(actionLabel), Width = Dim.Fill(), Height = 1 };
        Add(description, outcome, evidenceLabel, evidence, actionLabel, action);
        var submit = new Button { Text = "Submit", IsDefault = true };
        var cancel = new Button { Text = "Cancel" };
        submit.Accepting += (_, args) =>
        {
            args.Handled = true;
            var word = outcome.Value switch { 0 => "confirmed", 1 => "refuted", 2 => "inconclusive", _ => "" };
            Result = new(word, evidence.Text, action.Text);
            CloseRequested?.Invoke();
        };
        cancel.Accepting += (_, args) => { args.Handled = true; CloseRequested?.Invoke(); };
        AddButton(submit); AddButton(cancel);
        Initialized += (_, _) => outcome.SetFocus();
    }
}
