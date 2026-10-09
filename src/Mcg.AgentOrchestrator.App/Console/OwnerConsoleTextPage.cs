using System.Text;
using Terminal.Gui.Input;
using Terminal.Gui.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Full text belongs to the model; wrapping and the viewport belong to this dialog instance.
internal sealed class OwnerConsoleTextPage
{
    private readonly string _text;
    private readonly IReadOnlyList<int> _choiceLines;
    private int _height;
    internal IReadOnlyList<string> Lines { get; private set; } = [];
    internal IReadOnlyList<int> SourceLines { get; private set; } = [];
    internal IReadOnlyList<string> Visible => Lines.Skip(Offset).Take(_height).ToArray();
    internal int Offset { get; private set; }
    internal int Selected { get; private set; }
    internal int? Choice => _choiceLines.ToList().IndexOf(SourceLines.Count == 0 ? -1 : SourceLines[Selected]) is var index && index >= 0 ? index : null;

    internal OwnerConsoleTextPage(string text, int width, int height, IReadOnlyList<int>? choiceLines = null)
    { _text = text; _choiceLines = choiceLines ?? []; Resize(width, height); }

    internal void Resize(int width, int height)
    {
        width = Math.Max(2, width);
        _height = Math.Max(1, height);
        var lines = new List<string>();
        var sources = new List<int>();
        var source = 0;
        foreach (var paragraph in _text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var buffer = new StringBuilder();
            var cells = 0;
            foreach (var rune in paragraph.EnumerateRunes())
            {
                var size = rune.GetColumns();
                if (cells + size > width && buffer.Length > 0)
                {
                    var buffered = buffer.ToString();
                    var split = buffered.LastIndexOf(' ') + 1;
                    if (split == 0) split = buffered.Length;
                    lines.Add(buffered[..split]); sources.Add(source);
                    buffer.Clear(); buffer.Append(buffered[split..]);
                    cells = buffer.ToString().GetColumns();
                }
                buffer.Append(rune);
                cells += size;
            }
            lines.Add(buffer.ToString()); sources.Add(source++);
        }
        Lines = lines; SourceLines = sources;
        Select(Selected);
    }

    internal void Select(int row)
    {
        Selected = Math.Clamp(row, 0, Math.Max(0, Lines.Count - 1));
        if (Selected < Offset) Offset = Selected;
        if (Selected >= Offset + _height) Offset = Selected - _height + 1;
        Offset = Math.Clamp(Offset, 0, Math.Max(0, Lines.Count - _height));
    }

    internal bool HandleKey(Key key)
    {
        var delta = key == Key.CursorUp ? -1 : key == Key.CursorDown ? 1 :
            key == Key.PageUp ? -_height : key == Key.PageDown ? _height : 0;
        if (delta == 0) return false;
        Select(Selected + delta);
        return true;
    }
}
