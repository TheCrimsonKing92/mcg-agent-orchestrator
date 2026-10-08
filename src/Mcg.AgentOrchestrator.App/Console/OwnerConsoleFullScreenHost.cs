using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleFullScreenHost
{
    // Shared startup seam: headless views use the same log-to-model path as the production host.
    internal static async Task<(OwnerConsoleViewModel Model, List<OwnerConductEvent> Recent)> BuildInitialViewModelAsync(
        OwnerConsoleViewModelBuilder builder, string conductLogPath, DateTimeOffset opened,
        DateTimeOffset? last, CancellationToken token)
    {
        var recent = OwnerConsoleStartupActivity.ReadRecent(conductLogPath).ToList();
        var model = await builder.BuildAsync(new(opened, last, recent, 0), token);
        return (model, recent);
    }

    internal static async Task<int> RunAsync(OrchestratorWorkspace workspace, CancellationToken cancellationToken)
    {
        var clock = TimeProvider.System;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        IConductEventSource? events = null;
        using var rebuild = new SemaphoreSlim(1, 1);
        using IApplication app = Terminal.Gui.App.Application.Create();
        OwnerConsoleFullScreenView? view = null;
        Task? reader = null;
        try
        {
            events = new OwnerConsoleStartupEventSource(workspace.ConductEventsLogPath, clock);
            IOrchestratorStateQueries state = File.Exists(workspace.SqliteStatePath)
                ? SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath) : new EmptyOwnerConsoleStateQueries();
            var questions = new SerializedOwnerQuestionSource(new OwnerQuestionReadModel(state, workspace.OrchestratorDirectory));
            var builder = new OwnerConsoleViewModelBuilder(state, questions,
                new ConductorLeaseLiveness(workspace.OrchestratorDirectory), new PortfolioGoalEpicLookup(workspace.PortfolioStorePath), clock);
            var opened = clock.GetUtcNow();
            var last = events.LastActivity;
            var landings = 0;
            var (initial, recent) = await Task.Run(() => BuildInitialViewModelAsync(
                builder, workspace.ConductEventsLogPath, opened, last, token), token)
                .WaitAsync(OwnerConsoleLoopOptions.Default.OperationBound, clock, token);
            app.Init();
            var controller = new OwnerConsoleScreenController(questions, new AttentionAnswerHandlerAdapter(workspace),
                new TerminalGuiOwnerConsoleDialogs(app, token), state, new GoalEventFileTail(workspace.GoalLifecycleEventsDirectory),
                new CliConductorConsoleAdapter(workspace), new CliOwnerDigestConsoleAdapter(workspace),
                new OwnerDigestSummaryAdapter(workspace), clock);
            var refreshOperation = new OwnerConsoleScreenOperation(clock,
                label => app.Invoke(() => { if (!token.IsCancellationRequested) view!.SetWorking("refresh", label); }),
                message => app.Invoke(() => { if (!token.IsCancellationRequested) view!.ShowRefreshFailure(message); }));

            async Task RefreshAsync(OwnerConductEvent? item = null)
            {
                await rebuild.WaitAsync(token);
                try
                {
                    if (item is not null)
                    {
                        last = item.Timestamp;
                        if (OwnerConsoleViewModelBuilder.IsLanding(item)) landings++;
                        OwnerConsoleStartupActivity.Append(recent, item);
                    }
                    var inputs = new OwnerConsoleViewInputs(opened, last, recent.ToArray(), landings);
                    await refreshOperation.RunAsync("refresh after conductor event", async stepToken =>
                    {
                        var model = await builder.BuildAsync(inputs, stepToken);
                        stepToken.ThrowIfCancellationRequested();
                        app.Invoke(() => { if (!token.IsCancellationRequested) view!.Render(model); });
                    }, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                { app.Invoke(() => view!.ShowRefreshFailure(ex.Message)); }
                finally { rebuild.Release(); }
            }

            view = new(app, controller, () => RefreshAsync(), token);
            view.Render(initial);
            using var stop = token.Register(() => app.Invoke(() =>
            {
                app.RequestStop();
                app.RequestStop(view.Window);
            }));
            reader = Task.Run(() => new OwnerConsoleEventPump(events, clock,
                message => app.Invoke(() => { if (!token.IsCancellationRequested) view.ShowRefreshFailure(message); }))
                .RunAsync(item => RefreshAsync(item), token), token);
            app.Run(view.Window);
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return 0; }
        catch (Exception ex)
        {
            // The application has restored the terminal before reporting startup failure.
            app.Dispose();
            System.Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        finally
        {
            linked.Cancel();
            if (reader is not null)
            {
                try { await reader.WaitAsync(OwnerConsoleLoopOptions.Default.ShutdownBound, clock, CancellationToken.None); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
            }
            if (events is not null)
            {
                try { await events.DisposeAsync().AsTask().WaitAsync(OwnerConsoleLoopOptions.Default.ShutdownBound, clock, CancellationToken.None); }
                catch (TimeoutException) { }
            }
            view?.Dispose();
        }
    }
}
