using System.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleCapturedOutput : IOwnerConsoleOutput
{
    private readonly StringBuilder _text = new();
    public void Write(string text) => _text.Append(text);
    public void WriteLine(string text) => _text.AppendLine(text);
    internal string Text => _text.ToString();
}
