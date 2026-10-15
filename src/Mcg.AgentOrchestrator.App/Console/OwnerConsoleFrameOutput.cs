using System.Drawing;
using System.Text;
using Terminal.Gui.Drivers;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Session-owned adapter: keep the built-in Windows handle, cursor, size and disposal lifecycle.
// OutputBase still owns cell widths, attributes, images and dirty-row traversal.
internal sealed class OwnerConsoleFrameOutput(IOutput windowsOutput) : OutputBase, IOutput
{
    private readonly StringBuilder _frame = new();
    private bool _composing;

    public override void Write(IOutputBuffer buffer)
    {
        if (_composing) throw new InvalidOperationException("A console frame is already being composed.");
        if (IsLegacyConsole)
        {
            // Win32 cursor/attribute operations cannot be represented by an ANSI frame.
            windowsOutput.Write(buffer);
            return;
        }

        _composing = true;
        _frame.Clear();
        try
        {
            base.Write(buffer);
            if (_frame.Length > 0) windowsOutput.Write(_frame.ToString().AsSpan());
        }
        finally
        {
            _frame.Clear();
            _composing = false;
        }
    }

    protected override bool SetCursorPositionImpl(int x, int y)
    {
        _frame.Append("\u001b[").Append(y + 1).Append(';').Append(x + 1).Append('H');
        return true;
    }

    protected override void Write(StringBuilder text)
    {
        base.Write(text);
        _frame.Append(text);
    }

    // Direct startup/query writes are outside a refresh, and retain the built-in behavior.
    public void Write(ReadOnlySpan<char> text)
    {
        if (_composing) throw new InvalidOperationException("Direct output during console frame composition.");
        windowsOutput.Write(text);
    }

    public new bool Force16Colors
    {
        get => base.Force16Colors;
        set { base.Force16Colors = value; windowsOutput.Force16Colors = value; }
    }

    public new bool IsLegacyConsole
    {
        get => base.IsLegacyConsole;
        set { base.IsLegacyConsole = value; windowsOutput.IsLegacyConsole = value; }
    }

    public Size GetSize() => windowsOutput.GetSize();
    public void SetSize(int width, int height) => windowsOutput.SetSize(width, height);
    public Terminal.Gui.Drivers.Cursor GetCursor() => windowsOutput.GetCursor();
    public void SetCursor(Terminal.Gui.Drivers.Cursor cursor) => windowsOutput.SetCursor(cursor);
    public void Suspend() => windowsOutput.Suspend();
    public void Dispose() => windowsOutput.Dispose();
}
