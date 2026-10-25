namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GateLaneHeartbeatLoop
{
    internal static async Task RunAsync(Action<bool> writeRunning, Action? onTick,
        CancellationToken cancellationToken, TimeSpan interval)
    {
        if (cancellationToken.IsCancellationRequested) return;
        writeRunning(true);
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (cancellationToken.IsCancellationRequested) break;
            writeRunning(false);
            if (cancellationToken.IsCancellationRequested) break;
            try { onTick?.Invoke(); }
            catch (Exception) { /* Diagnostic callbacks must not stop gate heartbeats. */ }
        }
    }
}
