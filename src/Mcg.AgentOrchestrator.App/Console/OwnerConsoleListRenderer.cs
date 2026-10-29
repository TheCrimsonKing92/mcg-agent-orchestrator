using System.Collections.ObjectModel;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// One renderer belongs to one list. The snapshot never aliases the caller's collection.
internal sealed class OwnerConsoleListRenderer
{
    private string[]? _lines;
    internal int Replacements { get; private set; }

    internal bool Apply(ListView list, IReadOnlyList<string> lines)
    {
        if (_lines is not null && _lines.SequenceEqual(lines, StringComparer.Ordinal)) return false;
        var snapshot = lines.ToArray();
        list.SetSource(new ObservableCollection<string>(snapshot));
        _lines = snapshot;
        Replacements++;
        return true;
    }
}
