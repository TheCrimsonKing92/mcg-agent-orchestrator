namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal enum OwnerConsoleNoticeSeverity { Success, Failure }
internal enum OwnerConsoleNoticeSource { Refresh, Action }

internal sealed record OwnerConsoleNotice(string Text, DateTimeOffset RaisedAt,
    OwnerConsoleNoticeSeverity Severity, OwnerConsoleNoticeSource Source);

// The view owns this UI-thread state. Failures live until their source recovers.
internal sealed class OwnerConsoleNoticeStrip(TimeProvider clock)
{
    internal const int SuccessLifetimeSeconds = 5;
    internal static readonly TimeSpan SuccessLifetime = TimeSpan.FromSeconds(SuccessLifetimeSeconds);
    internal const int MaxEntries = 100;
    private readonly List<OwnerConsoleNotice> _entries = [];

    internal IReadOnlyList<OwnerConsoleNotice> Visible
    {
        get
        {
            var now = clock.GetUtcNow();
            _entries.RemoveAll(entry => entry.Severity == OwnerConsoleNoticeSeverity.Success &&
                now - entry.RaisedAt >= SuccessLifetime);
            return _entries.ToArray();
        }
    }

    internal void Raise(string text, OwnerConsoleNoticeSeverity severity, OwnerConsoleNoticeSource source)
    {
        _ = Visible;
        if (severity == OwnerConsoleNoticeSeverity.Failure)
            _entries.RemoveAll(entry => entry.Severity == severity && entry.Source == source && entry.Text == text);
        _entries.Insert(0, new(text, clock.GetLocalNow(), severity, source));
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
    }

    internal TimeSpan? UntilNextSuccessExpiry
    {
        get
        {
            var expiry = _entries.Where(entry => entry.Severity == OwnerConsoleNoticeSeverity.Success)
                .Select(entry => (DateTimeOffset?)(entry.RaisedAt + SuccessLifetime)).Min();
            if (expiry is null) return null;
            var remaining = expiry.Value - clock.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    internal void RefreshSucceeded() => Recover(OwnerConsoleNoticeSource.Refresh);
    internal void ActionStarted() => Recover(OwnerConsoleNoticeSource.Action);

    private void Recover(OwnerConsoleNoticeSource source) =>
        _entries.RemoveAll(entry => entry.Severity == OwnerConsoleNoticeSeverity.Failure && entry.Source == source);

    internal string Format(int width)
    {
        var text = string.Join(" | ", Visible.Select(entry => $"{entry.RaisedAt:HH:mm:ss} {entry.Text}"));
        return width > 0 ? OwnerConsoleLineFitter.Cut(text, width) : text;
    }
}
