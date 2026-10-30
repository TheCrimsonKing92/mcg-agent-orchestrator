using Terminal.Gui.Drawing;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Keep TableView's goal columns and scrolling, replacing only the decorative section lines.
internal sealed class OwnerConsoleBoardPane : TableView
{
    internal IReadOnlyDictionary<int, string> Headers { get; set; } = new Dictionary<int, string>();

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var result = base.OnDrawingContent(context);
        var headerHeight = CurrentHeaderHeightVisible();
        var normal = GetScheme().Normal;
        var headerAttribute = new Attribute(normal.Foreground, normal.Background, normal.Style | TextStyle.Bold);
        foreach (var (index, text) in Headers)
        {
            var y = headerHeight + index - RowOffset;
            if (index < RowOffset || y >= Viewport.Height) continue;
            SetAttribute(headerAttribute);
            AddStr(0, y, new string(' ', Viewport.Width));
            DrawSegments(this, y, [(text, false)], headerAttribute);
        }
        return result;
    }

    internal static void DrawSegments(View view, int y, IReadOnlyList<(string Text, bool Dimmed)> segments,
        Attribute? baseAttribute = null)
    {
        var x = 0;
        foreach (var (text, dimmed) in segments)
        {
            view.SetAttribute(dimmed ? view.GetScheme().Disabled : baseAttribute ?? view.GetScheme().Normal);
            foreach (var rune in text.EnumerateRunes())
            {
                var width = rune.GetColumns();
                if (x + width > view.Viewport.Width) return;
                view.AddStr(x, y, rune.ToString());
                x += width;
            }
        }
    }
}
