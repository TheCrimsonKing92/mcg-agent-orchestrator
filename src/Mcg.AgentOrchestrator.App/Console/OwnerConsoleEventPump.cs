namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleEventPump(IConductEventSource events, TimeProvider clock,
    Action<string> report, OwnerConsoleLoopOptions? options = null)
{
    internal async Task RunAsync(Func<OwnerConductEvent, Task> refresh, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            OwnerConductEvent item;
            try { item = await events.ReadAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                report($"conductor event read failed; retrying: {ex.Message}");
                try { await Task.Delay((options ?? OwnerConsoleLoopOptions.Default).PollBound, clock, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                continue;
            }
            await refresh(item);
        }
    }
}
