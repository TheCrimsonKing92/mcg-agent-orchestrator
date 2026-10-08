namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Owns progressive startup and independent pane failures. UI mutations are dispatched as one merged snapshot.
internal sealed class OwnerConsoleStartupLoader(OwnerConsoleViewModelBuilder builder,
    IOwnerConsoleActivityLoader activity, Action<Action> dispatch)
{
    internal async Task<Task> StartAsync(Action<OwnerConsoleViewModel> renderBoard,
        Action<OwnerConsoleViewModel> renderUpdate, OwnerConsoleViewInputs inputs,
        Action<OwnerConsoleActivityLoad> activityLoaded, CancellationToken token)
    {
        var model = (await builder.BuildBoardAsync(token)) with
            { DecisionsState = new(true), ActivityState = new(true) };
        token.ThrowIfCancellationRequested();
        renderBoard(model);
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
                var (rows, hidden) = await builder.ReadDecisionsAsync(token);
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
                var loaded = await activity.LoadAsync(model.Board.Select(row => row.GoalId).ToArray(), token);
                token.ThrowIfCancellationRequested();
                activityLoaded(loaded);
                Update(current => builder.WithActivity(current, inputs with
                    { RecentEvents = loaded.Recent, LastConductEvent = loaded.LastActivity }) with { ActivityState = null });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Update(current => current with { ActivityState = new(Error: ex.Message) }); }
        }, token);
        return Task.WhenAll(decisions, events);
    }
}
