namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleLoopOptions(TimeSpan PollBound, TimeSpan OperationBound, TimeSpan ShutdownBound)
{
    internal TimeSpan BusyNoticeAfter { get; init; } = TimeSpan.FromSeconds(2);
    internal static OwnerConsoleLoopOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2));
}

internal interface IOwnerConsoleSteps
{
    Task StartAsync(DateTimeOffset? lastActivity, CancellationToken cancellationToken);
    Task HandleEventAsync(OwnerConductEvent item, CancellationToken cancellationToken);
    Task<bool> HandleCommandAsync(string line, CancellationToken cancellationToken);
}

internal sealed class OwnerConsoleLoop(
    IOwnerConsoleSteps steps,
    IOwnerConsoleInput input,
    IConductEventSource events,
    IOwnerConsoleOutput output,
    TimeProvider clock,
    OwnerConsoleLoopOptions? options = null)
{
    private readonly OwnerConsoleLoopOptions _options = options ?? OwnerConsoleLoopOptions.Default;
    internal bool EventSourceAbandoned { get; private set; }

    internal OwnerConsoleLoop(OwnerConsoleSession session, IOwnerConsoleInput input,
        IConductEventSource events, TimeProvider clock, OwnerConsoleLoopOptions? options = null)
        : this(new SessionSteps(session), input, events, new SystemConsoleOutput(), clock, options) { }

    internal async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        Task<string?>? lineTask = null;
        Task<OwnerConductEvent>? eventTask = null;
        OwnerConductEvent? pendingEvent = null;
        Task? unfinishedRefresh = null;
        var lateSteps = new List<Task>();
        try
        {
            await RunStepAsync("startup", async ct =>
            {
                await steps.StartAsync(events.LastActivity, ct);
                return true;
            }, collectEvents: false);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                lineTask ??= input.ReadLineAsync(token).AsTask();
                eventTask ??= ReadEventAsync();
                lateSteps.RemoveAll(task => task.IsCompleted);

                // WhenAny's argument order cannot establish command priority.
                if (lineTask.IsCompleted)
                {
                    var line = await lineTask;
                    lineTask = null;
                    if (line is null) break;
                    if (await RunStepAsync(line.Trim(), ct => steps.HandleCommandAsync(line, ct)) == false)
                        break;
                    continue;
                }
                if (eventTask.IsCompleted)
                    await CollectEventAsync();
                if (pendingEvent is not null && !input.IsEditingLine &&
                    (unfinishedRefresh is null || unfinishedRefresh.IsCompleted))
                {
                    if (lineTask.IsCompleted) continue;
                    var item = pendingEvent;
                    pendingEvent = null;
                    await RunStepAsync("refresh after conductor event", async ct =>
                    {
                        await steps.HandleEventAsync(item, ct);
                        return true;
                    }, isRefresh: true);
                    continue;
                }
                eventTask ??= ReadEventAsync();
                try
                {
                    await Task.WhenAny(lateSteps.Concat([lineTask, eventTask]))
                        .WaitAsync(_options.PollBound, clock, token);
                }
                catch (TimeoutException) { }
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
                catch (Exception ex) { ReportError(ex); }
            }
            try { await events.DisposeAsync().AsTask().WaitAsync(_options.ShutdownBound, clock, CancellationToken.None); }
            catch (TimeoutException) { EventSourceAbandoned = true; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ReportError(ex); }
        }

        async Task<OwnerConductEvent> ReadEventAsync()
        {
            // Normalize synchronous source throws into the same faulted-read path.
            return await events.ReadAsync(token);
        }

        async Task<OwnerConductEvent> RetryEventAsync()
        {
            await Task.Delay(_options.PollBound, clock, token);
            return await ReadEventAsync();
        }

        async Task<bool> CollectEventAsync()
        {
            var read = eventTask!;
            eventTask = null;
            try
            {
                var latest = await read;
                // Preserve the board trigger from OwnerConsoleSession.HandleEventAsync
                // while retaining the latest event's timestamp and other data.
                pendingEvent = pendingEvent is not null && PrintsBoard(pendingEvent.EventKind) &&
                    !PrintsBoard(latest.EventKind)
                    ? latest with { EventKind = pendingEvent.EventKind }
                    : latest;
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Keep the retry pending so input and active steps can progress.
                // Arm the delay before publishing the error, including under fake time.
                eventTask = RetryEventAsync();
                ReportError(ex);
                return false;
            }
        }

        async Task<bool?> RunStepAsync(string label, Func<CancellationToken, Task<bool>> action,
            bool isRefresh = false, bool collectEvents = true)
        {
            var stepCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var timers = CancellationTokenSource.CreateLinkedTokenSource(token);
            // Arm timers before invoking the step, including in fake-time tests.
            var busy = Task.Delay(_options.BusyNoticeAfter, clock, timers.Token);
            var bound = Task.Delay(_options.OperationBound, clock, timers.Token);
            Task<bool>? operation = null;
            var detached = false;
            var busyPrinted = false;
            try
            {
                // Session adapters can perform synchronous store I/O before their first await.
                // Keep that work off the loop so its notices and bounds remain observable.
                var stepToken = stepCancellation.Token;
                operation = Task.Run(() => action(stepToken), stepToken);
                while (!operation.IsCompleted)
                {
                    token.ThrowIfCancellationRequested();
                    if (!busyPrinted && busy.IsCompleted)
                    {
                        output.WriteLine($"working: {label} ...");
                        busyPrinted = true;
                    }
                    if (bound.IsCompleted)
                    {
                        output.WriteLine($"{label} did not finish within {_options.OperationBound.TotalSeconds:0.###}s; the console is still running");
                        detached = true;
                        try { stepCancellation.Cancel(); }
                        catch (Exception ex) { ReportError(ex); }
                        var observer = ObserveLateStepAsync(label, operation, stepCancellation);
                        lateSteps.Add(observer);
                        if (isRefresh) unfinishedRefresh = observer;
                        return null;
                    }
                    // Drain events into one pending refresh while a step is running.
                    if (collectEvents)
                    {
                        eventTask ??= ReadEventAsync();
                        if (eventTask.IsCompleted)
                        {
                            await CollectEventAsync();
                            continue;
                        }
                    }
                    var waits = new List<Task> { operation, bound };
                    if (!busyPrinted) waits.Add(busy);
                    if (collectEvents) waits.Add(eventTask!);
                    await Task.WhenAny(waits).WaitAsync(token);
                }
                // Finish draining an already-started burst before the next refresh.
                // Do not initiate a read here for a synchronous completed step.
                while (collectEvents && eventTask is { IsCompleted: true })
                {
                    token.ThrowIfCancellationRequested();
                    if (!await CollectEventAsync()) break;
                    eventTask = ReadEventAsync();
                }
                return await operation;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (operation is not null)
                {
                    detached = true;
                    lateSteps.Add(ObserveLateStepAsync(label, operation, stepCancellation));
                }
                throw;
            }
            catch (OperationCanceledException)
            {
                output.WriteLine($"{label} was abandoned");
                return null;
            }
            catch (Exception ex)
            {
                ReportError(ex);
                return null;
            }
            finally
            {
                timers.Cancel();
                if (!detached) stepCancellation.Dispose();
            }
        }

        async Task ObserveLateStepAsync(string label, Task<bool> operation, CancellationTokenSource source)
        {
            try { await operation; } // Successful steps print their own answers, including late ones.
            catch (OperationCanceledException) { output.WriteLine($"{label} was abandoned"); }
            catch (Exception ex) { ReportError(ex); }
            finally { source.Dispose(); }
        }
    }

    private void ReportError(Exception ex) => output.WriteLine(
        $"error: {ex.GetBaseException().Message.Replace('\r', ' ').Replace('\n', ' ')}");

    private static bool PrintsBoard(string kind) =>
        kind is "watch-transition" or "acceptance" or "loop-relaunch" or "goal-escalation";

    private sealed class SessionSteps(OwnerConsoleSession session) : IOwnerConsoleSteps
    {
        public Task StartAsync(DateTimeOffset? lastActivity, CancellationToken cancellationToken) =>
            session.StartAsync(lastActivity, cancellationToken);
        public Task HandleEventAsync(OwnerConductEvent item, CancellationToken cancellationToken) =>
            session.HandleEventAsync(item, cancellationToken);
        public Task<bool> HandleCommandAsync(string line, CancellationToken cancellationToken) =>
            session.HandleCommandAsync(line, cancellationToken);
    }
}
