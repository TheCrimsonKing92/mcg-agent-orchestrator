using System.Globalization;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleRefreshTimings(TimeProvider clock)
{
    private static readonly string[] Loaders = ["board", "gate-progress", "gate-motion", "latest-tick",
        "decisions", "lifecycle-tail", "landed-today", "activity", "render"];
    private readonly object _sync = new();
    private readonly long[] _calls = new long[Loaders.Length];
    private readonly long[] _ticks = new long[Loaders.Length];

    internal T Time<T>(string loader, Func<T> action)
    {
        var index = Index(loader);
        var started = clock.GetTimestamp();
        try { return action(); }
        finally { Record(index, clock.GetTimestamp() - started); }
    }

    internal async Task<T> TimeAsync<T>(string loader, Func<Task<T>> action)
    {
        var index = Index(loader);
        var started = clock.GetTimestamp();
        try { return await action().ConfigureAwait(false); }
        finally { Record(index, clock.GetTimestamp() - started); }
    }

    internal string Detail()
    {
        lock (_sync)
            return string.Join(" ", Loaders.Select((loader, index) => string.Create(CultureInfo.InvariantCulture,
                $"{loader}={_calls[index]}/{(long)((decimal)_ticks[index] * 1000 / clock.TimestampFrequency)}ms")));
    }

    private static int Index(string loader)
    {
        var index = Array.IndexOf(Loaders, loader);
        return index >= 0 ? index : throw new ArgumentOutOfRangeException(nameof(loader), loader, "Unknown refresh loader.");
    }

    private void Record(int index, long ticks)
    {
        lock (_sync) { _calls[index]++; _ticks[index] += ticks; }
    }
}
