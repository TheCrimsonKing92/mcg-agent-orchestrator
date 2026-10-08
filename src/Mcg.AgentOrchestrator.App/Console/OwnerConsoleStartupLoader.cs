namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Owns progressive startup and independent pane failures. UI mutations are dispatched as one merged snapshot.
internal sealed class OwnerConsoleStartupLoader(OwnerConsoleViewModelBuilder builder,
    IOwnerConsoleActivityLoader activity, Action<Action> dispatch, TimeProvider? clock = null,
    OwnerConsoleLoopOptions? options = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly OwnerConsoleLoopOptions _options = options ?? OwnerConsoleLoopOptions.Default;

    internal async Task<Task> StartAsync(Action<OwnerConsoleViewModel> renderBoard,
        Action<OwnerConsoleViewModel> renderUpdate, OwnerConsoleViewInputs inputs,
        Action<OwnerConsoleActivityLoad> activityLoaded, CancellationToken token)
    {
        var model = LoadingBoard(await builder.BuildBoardAsync(token));
        token.ThrowIfCancellationRequested();
        renderBoard(model);
        return FillAsync(model, renderUpdate, inputs, activityLoaded, token);
    }

    internal static OwnerConsoleViewModel LoadingBoard(OwnerConsoleViewModel board) =>
        board with { DecisionsState = new(true), ActivityState = new(true) };

    internal Task FillAsync(OwnerConsoleViewModel model, Action<OwnerConsoleViewModel> renderUpdate,
        OwnerConsoleViewInputs inputs, Action<OwnerConsoleActivityLoad> activityLoaded, CancellationToken token)
    {
        var sync = new object();
        void Update(Func<OwnerConsoleViewModel, OwnerConsoleViewModel> change)
        {
            dispatch(() =>
            {
                if (token.IsCancellationRequested) return;
                lock (sync) { model = change(model); renderUpdate(model); }
            });
        }

        var decisions = Task.Run(async () =>
        {
            try
            {
                var (rows, hidden) = await LoadBoundedAsync(builder.ReadDecisionsAsync, "questions", token);
                Update(current => current with { Decisions = rows, DecisionsState = null,
                    Status = current.Status with { LiveDecisions = rows.Length, HiddenQuestions = hidden } });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Update(current => current with { DecisionsState = new(Error: ex.Message) }); }
        }, token);
        var events = Task.Run(async () =>
        {
            try
            {
                var loaded = await LoadBoundedAsync(step => activity.LoadAsync(
                    model.Board.Select(row => row.GoalId).ToArray(), step), "activity", token);
                token.ThrowIfCancellationRequested();
                activityLoaded(loaded);
                Update(current => builder.WithActivity(current, inputs with
                    { RecentEvents = loaded.Recent, LastConductEvent = loaded.LastActivity, LandedToday = loaded.LandedToday }) with { ActivityState = null });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Update(current => current with { ActivityState = new(Error: ex.Message) }); }
        }, token);
        return Task.WhenAll(decisions, events);
    }

    private async Task<T> LoadBoundedAsync<T>(Func<CancellationToken, Task<T>> load, string pane,
        CancellationToken token)
    {
        using var step = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stepToken = step.Token;
        var operation = Task.Run(() => load(stepToken), CancellationToken.None);
        try { return await operation.WaitAsync(_options.OperationBound, _clock, token); }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{pane} did not finish within {_options.OperationBound.TotalSeconds:0.###}s; the console is still running");
        }
        finally
        {
            step.Cancel();
            // Cancellation-ignoring sources cannot keep the UI loading or publish late results.
            // Observe their eventual failure without overwriting the pane's timeout state.
            _ = ObserveAsync(operation);
        }
    }

    private static async Task ObserveAsync(Task operation)
    {
        try { await operation; }
        catch (Exception) { }
    }
}
