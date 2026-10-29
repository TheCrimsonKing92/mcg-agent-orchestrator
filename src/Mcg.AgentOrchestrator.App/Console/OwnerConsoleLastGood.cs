namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Session-local successful content, separated by the logical subject displayed by a surface.
// An empty value is still a successful load; absence is represented only by a missing key.
internal sealed class OwnerConsoleLastGood<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _values = new();
    private readonly object _gate = new();

    internal void Remember(TKey key, TValue value) { lock (_gate) _values[key] = value; }
    internal bool TryGet(TKey key, out TValue value) { lock (_gate) return _values.TryGetValue(key, out value!); }
}
