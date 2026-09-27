namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleLoopOptions(TimeSpan PollBound, TimeSpan OperationBound, TimeSpan ShutdownBound)
{
    internal static OwnerConsoleLoopOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2));
}

internal sealed class OwnerConsoleLoop(
    OwnerConsoleSession session,
    IOwnerConsoleInput input,
    IConductEventSource events,
    TimeProvider clock,
    OwnerConsoleLoopOptions? options = null)
{
    private readonly OwnerConsoleLoopOptions _options = options ?? OwnerConsoleLoopOptions.Default;
    internal bool EventSourceAbandoned { get; private set; }

    internal async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        Task<string?>? lineTask = null;
        Task<OwnerConductEvent>? eventTask = null;
        try
        {
            await session.StartAsync(events.LastActivity, token).WaitAsync(_options.OperationBound, clock, token);
            while (true)
            {
                lineTask ??= input.ReadLineAsync(token).AsTask();
                eventTask ??= events.ReadAsync(token).AsTask();
                Task completed;
                try
                {
                    completed = await Task.WhenAny(eventTask, lineTask)
                        .WaitAsync(_options.PollBound, clock, token);
                }
                catch (TimeoutException) { continue; }
                if (completed == eventTask)
                {
                    if (input.IsEditingLine && !lineTask.IsCompleted)
                    {
                        try { await lineTask.WaitAsync(_options.PollBound, clock, token); }
                        catch (TimeoutException) { continue; }
                    }
                    var item = await eventTask.WaitAsync(_options.OperationBound, clock, token);
                    eventTask = null;
                    await session.HandleEventAsync(item, token).WaitAsync(_options.OperationBound, clock, token);
                    continue;
                }
                var line = await lineTask.WaitAsync(_options.OperationBound, clock, token);
                lineTask = null;
                if (line is null || !await session.HandleCommandAsync(line, token).WaitAsync(_options.OperationBound, clock, token))
                    break;
            }
        }
        finally
        {
            linked.Cancel();
            if (eventTask is not null)
            {
                try { await eventTask.WaitAsync(_options.ShutdownBound, clock, CancellationToken.None); }
                catch (TimeoutException) { EventSourceAbandoned = true; }
                catch (OperationCanceledException) { }
            }
            try { await events.DisposeAsync().AsTask().WaitAsync(_options.ShutdownBound, clock, CancellationToken.None); }
            catch (TimeoutException) { EventSourceAbandoned = true; }
        }
    }
}
