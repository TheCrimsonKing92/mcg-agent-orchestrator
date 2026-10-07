namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class CaptureLimitStop(bool enabled) : IDisposable
{
    private readonly CancellationTokenSource? _source = enabled ? new CancellationTokenSource() : null;
    private int _stopped;

    public Action? OnLimitReached => _source is null ? null : Stop;
    public CancellationToken Token => _source?.Token ?? CancellationToken.None;
    public bool Stopped => Volatile.Read(ref _stopped) != 0;

    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        try { _source?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose() => _source?.Dispose();
}
