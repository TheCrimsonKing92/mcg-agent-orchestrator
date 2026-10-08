namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleKeySource
{
    bool IsInputRedirected { get; }
    ConsoleKeyInfo? ReadKey();
}
