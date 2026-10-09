namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleDialogs
{
    Task<bool> ConfirmAsync(string title, string text);
    Task<string?> PromptTextAsync(string title, string text);
    Task ShowTextAsync(string title, string text);
    async Task<int?> ShowPageAsync(string title, string text, IReadOnlyList<int>? choiceLines = null)
    { await ShowTextAsync(title, text); return null; }
}
