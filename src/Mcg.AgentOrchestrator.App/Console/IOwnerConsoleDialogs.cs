namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleDialogs
{
    Task<bool> ConfirmAsync(string title, string text);
    Task<string?> PromptTextAsync(string title, string text);
    Task ShowTextAsync(string title, string text);
}
