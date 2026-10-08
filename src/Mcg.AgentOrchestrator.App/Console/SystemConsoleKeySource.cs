namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class SystemConsoleKeySource : IOwnerConsoleKeySource
{
    public bool IsInputRedirected => System.Console.IsInputRedirected;
    public ConsoleKeyInfo? ReadKey() => System.Console.ReadKey(intercept: true);
}
