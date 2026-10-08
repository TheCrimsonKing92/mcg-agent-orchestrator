namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class SystemConsoleTextSink : IOwnerConsoleTextSink
{
    public void Write(string text)
    {
        System.Console.Write(text);
        System.Console.Out.Flush();
    }
}
