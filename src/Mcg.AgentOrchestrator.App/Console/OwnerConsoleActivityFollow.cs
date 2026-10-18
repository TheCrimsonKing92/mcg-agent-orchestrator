namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal readonly record struct OwnerConsoleActivityKey(DateTimeOffset Timestamp, string Kind,
    string GoalPrefix, string Detail, string? OwnerQuestionId, int Occurrence);

// The view owns this state; only an owner move or an explicit follow command changes mode.
internal sealed class OwnerConsoleActivityFollow
{
    private OwnerConsoleActivityKey? _key;
    private bool _anchored;
    internal bool Following { get; private set; } = true;

    internal static OwnerConsoleActivityKey Key(IReadOnlyList<OwnerConsoleActivityItem> items, int index) => Keys(items)[index];

    internal static IReadOnlyList<OwnerConsoleActivityKey> Keys(IReadOnlyList<OwnerConsoleActivityItem> items)
    {
        var keys = new OwnerConsoleActivityKey[items.Count];
        var occurrences = new Dictionary<OwnerConsoleActivityKey, int>();
        for (var index = items.Count - 1; index >= 0; index--)
        {
            var item = items[index];
            var raw = new OwnerConsoleActivityKey(item.Timestamp, item.Kind, item.GoalPrefix,
                item.Detail, item.OwnerQuestionId, 0);
            occurrences.TryGetValue(raw, out var occurrence);
            keys[index] = raw with { Occurrence = occurrence };
            occurrences[raw] = occurrence + 1;
        }
        return keys;
    }

    internal void Inspect(OwnerConsoleActivityKey? key)
    {
        Following = false;
        _key = key;
        _anchored = false;
    }

    internal void Scrolled(OwnerConsoleActivityKey key, int selected, int top, int visibleRows)
    {
        Inspect(key);
        // A wheel move can intentionally leave the selection outside the content being read.
        _anchored = selected < top || selected >= top + Math.Max(1, visibleRows);
    }

    internal void Follow()
    {
        Following = true;
        _key = null;
        _anchored = false;
    }

    internal (int? Selected, int Top) Place(IReadOnlyList<OwnerConsoleActivityKey> current,
        int? selected, int top, int visibleRows)
    {
        if (current.Count == 0) return (null, 0);
        if (Following) return (0, 0);
        var rows = Math.Max(1, visibleRows);
        var index = -1;
        for (var candidate = 0; candidate < current.Count; candidate++)
            if (current[candidate] == _key) { index = candidate; break; }
        if (index >= 0) top += index - (selected ?? index);
        else
        {
            index = Math.Clamp(selected ?? 0, 0, current.Count - 1);
            _key = current[index];
            _anchored = false;
        }
        top = Math.Clamp(top, 0, Math.Max(0, current.Count - rows));
        if (!_anchored) top = Math.Clamp(top, index - rows + 1, index);
        return (index, top);
    }
}
