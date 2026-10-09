namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleDialogs
{
    Task<bool> ConfirmAsync(string title, string text);
    Task<string?> PromptTextAsync(string title, string text);
    Task ShowTextAsync(string title, string text);
    Task ShowDecisionAsync(OwnerConsoleDecisionDetail detail) => ShowTextAsync("Decision", detail.State.Text);
    async Task<int?> ShowPageAsync(string title, string text, IReadOnlyList<int>? choiceLines = null)
    { await ShowTextAsync(title, text); return null; }
    async Task ShowGoalAsync(OwnerConsoleGoalDialog dialog)
    {
        dialog.Start(action => action());
        var selected = await ShowPageAsync(dialog.Title, dialog.Current.Text, dialog.Current.ChoiceLines);
        if (selected is { } index) await dialog.OpenResolutionAsync(index);
    }
}
