namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// One stream poll requests at most one rebuild; lifecycle text remains owned by the tail.
internal sealed class OwnerConsoleStreamRefresh(ChangeStreamFileReader changes,
    Func<OwnerConsoleRefreshScope, Task> rebuild, TimeProvider clock)
{
    internal async Task PollOnceAsync()
    {
        var records = changes.ReadAvailable(out var discontinuity);
        if (discontinuity is not null)
        {
            // Anchor before rebuilding so rotation cannot repeatedly request a full read.
            changes.Reanchor();
            await rebuild(OwnerConsoleRefreshScope.All);
        }
        else if (records.Count > 0)
            await rebuild(OwnerConsoleRefreshScope.For(records.Select(record => record.GoalId)));
    }

    internal async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await PollOnceAsync(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception) { /* Retry on the next interval; periodic refresh remains the fallback. */ }
            try { await Task.Delay(OwnerConsoleLoopOptions.Default.PollBound, clock, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }
}
