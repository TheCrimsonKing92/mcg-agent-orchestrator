using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleFullScreenHost
{
    // Eager history/model helper retained for callers that need a complete snapshot.
    // Production progressive startup uses OwnerConsoleStartupLoader.FillAsync.
    internal static async Task<(OwnerConsoleViewModel Model, List<OwnerConductEvent> Recent)> BuildInitialViewModelAsync(
        OwnerConsoleViewModelBuilder builder, string conductLogPath, DateTimeOffset opened,
        DateTimeOffset? last, CancellationToken token)
    {
        var history = OwnerConsoleStartupActivity.ReadHistory(conductLogPath, null, TimeProvider.System, token);
        var recent = history.Recent.ToList();
        var model = await builder.BuildAsync(new(opened, last, recent, history.LandedToday), token);
        return (model, recent);
    }

    internal static async Task<int> RunAsync(OrchestratorWorkspace workspace, CancellationToken cancellationToken)
    {
        var clock = TimeProvider.System;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        IConductEventSource? events = null;
        OwnerConsoleActivityLoader? activityLoader = null;
        Task? fill = null;
        using var rebuild = new SemaphoreSlim(1, 1);
        using IApplication app = Terminal.Gui.App.Application.Create();
        OwnerConsoleFullScreenView? view = null;
        Task? reader = null;
        Task periodicRefresh = Task.CompletedTask;
        try
        {
            IOrchestratorStateQueries state = File.Exists(workspace.SqliteStatePath)
                ? SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath) : new EmptyOwnerConsoleStateQueries();
            var questions = new SerializedOwnerQuestionSource(new OwnerQuestionReadModel(state, workspace.OrchestratorDirectory));
            var cohortEvidence = new OwnerActivityEvidenceReader(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            var gateEvidence = new OwnerGateFailureEvidence(workspace.OrchestratorDirectory);
            var jointEvidence = new OwnerJointGateFailureEvidence(workspace.OrchestratorDirectory);
            var builder = new OwnerConsoleViewModelBuilder(state, questions,
                new ConductorLeaseLiveness(workspace.OrchestratorDirectory), new PortfolioGoalEpicLookup(workspace.PortfolioStorePath), clock,
                item => item.EventKind == "acceptance-cohort" ? jointEvidence.Read(item, cohortEvidence.Read(item)) :
                    gateEvidence.Read(item) ?? jointEvidence.Read(item));
            var opened = clock.GetUtcNow();
            DateTimeOffset? last = null;
            var landings = 0;
            var landingDate = clock.GetLocalNow().Date;
            var countedLandings = new HashSet<OwnerConductEvent>();
            var recent = new List<OwnerConductEvent>();
            IReadOnlyCollection<string> boardIds = [];
            activityLoader = new(workspace.ConductEventsLogPath, workspace.GoalLifecycleEventsDirectory, clock);
            var refreshOperation = new OwnerConsoleScreenOperation(clock,
                label => app.Invoke(() => { if (!token.IsCancellationRequested) view!.SetWorking("refresh", label); }),
                message => app.Invoke(() => { if (!token.IsCancellationRequested) view!.ShowRefreshFailure(message); }));

            async Task RefreshAsync(OwnerConductEvent? item = null, bool skipIfBusy = false)
            {
                if (skipIfBusy)
                {
                    if (!await rebuild.WaitAsync(0, token)) return;
                }
                else await rebuild.WaitAsync(token);
                try
                {
                    if (clock.GetLocalNow().Date != landingDate)
                    { landingDate = clock.GetLocalNow().Date; landings = 0; countedLandings.Clear(); }
                    if (item is not null)
                    {
                        last = item.Timestamp;
                        if (OwnerActivityNarrator.IsLanding(item) &&
                            TimeZoneInfo.ConvertTime(item.Timestamp, clock.LocalTimeZone).Date == landingDate && countedLandings.Add(item)) landings++;
                        OwnerConsoleStartupActivity.Append(recent, item, OwnerConsoleStartupActivity.MaxRawEvents);
                    }
                    var inputs = new OwnerConsoleViewInputs(opened, last, recent.ToArray(), landings);
                    await refreshOperation.RunAsync(item is null ? "refresh" : "refresh after conductor event", async stepToken =>
                    {
                        var board = await builder.BuildBoardAsync(stepToken);
                        boardIds = board.Board.Select(row => row.GoalId).ToArray();
                        foreach (var lifecycle in activityLoader.ReadNew(boardIds)) OwnerConsoleStartupActivity.Append(recent, builder.EnrichEvent(lifecycle), OwnerConsoleStartupActivity.MaxRawEvents);
                        var (decisions, hidden) = await builder.ReadDecisionsAsync(stepToken);
                        var model = builder.WithActivity(builder.WithDecisions(board, decisions, hidden),
                            inputs with { RecentEvents = recent.ToArray() });
                        stepToken.ThrowIfCancellationRequested();
                        app.Invoke(() => { if (!token.IsCancellationRequested) view!.Render(model); });
                    }, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                { app.Invoke(() => view!.ShowRefreshFailure(ex.Message)); }
                finally { rebuild.Release(); }
            }

            rebuild.Wait(token);
            try
            {
                // Keep Init, initial Render and Run on this host thread. Metadata is
                // read on the pool before GUI initialization, with the existing bound.
                var board = OwnerConsoleStartupLoader.LoadingBoard(Task.Run(() => builder.BuildBoardAsync(token), token)
                    .WaitAsync(OwnerConsoleLoopOptions.Default.OperationBound, clock, token).GetAwaiter().GetResult());
                app.Init();
                var controller = new OwnerConsoleScreenController(questions, new AttentionAnswerHandlerAdapter(workspace),
                    new TerminalGuiOwnerConsoleDialogs(app, token), state, new GoalEventFileTail(workspace.GoalLifecycleEventsDirectory),
                    new CliConductorConsoleAdapter(workspace), new CliOwnerDigestConsoleAdapter(workspace),
                    new OwnerDigestSummaryAdapter(workspace), clock, new WorkspaceEpicProgressSource(workspace),
                    resolutions: new OwnerQuestionResolutionReader(state, new GoalEventFileTail(workspace.GoalLifecycleEventsDirectory),
                        workspace.OrchestratorDirectory, workspace.LogDirectory, clock));
                view = new(app, controller, () => RefreshAsync(), token, clock, OwnerConsoleLoopOptions.Default);
                boardIds = board.Board.Select(row => row.GoalId).ToArray();
                view.Render(board);
                var startup = new OwnerConsoleStartupLoader(builder, activityLoader, action => app.Invoke(action), clock);
                fill = startup.FillAsync(board, model => view.Render(model), new(opened, last, [], 0), loaded =>
                {
                    recent = loaded.Recent.Select(builder.EnrichEvent).ToList();
                    last = loaded.LastActivity;
                    landings = loaded.LandedToday;
                    foreach (var landing in loaded.Recent.Where(OwnerActivityNarrator.IsLanding)) countedLandings.Add(landing);
                }, token);
            }
            catch { rebuild.Release(); throw; }
            using var stop = token.Register(() => app.Invoke(() =>
            {
                app.RequestStop();
                app.RequestStop(view!.Window);
            }));
            reader = Task.Run(async () =>
            {
                try { await fill; }
                finally { rebuild.Release(); }
                token.ThrowIfCancellationRequested();
                events = activityLoader.Events ?? new OwnerConsoleStartupEventSource(workspace.ConductEventsLogPath, clock);
                await new OwnerConsoleEventPump(events, clock,
                    message => app.Invoke(() => { if (!token.IsCancellationRequested) view!.ShowRefreshFailure(message); }))
                    .RunAsync(item => RefreshAsync(item), token);
            });
            periodicRefresh = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(OwnerConsoleLoopOptions.Default.RefreshInterval, clock, token);
                    app.Invoke(() => { if (!token.IsCancellationRequested) view.RefreshStatus(); });
                    // Skip rather than queue another refresh behind startup or an event.
                    await RefreshAsync(skipIfBusy: true);
                }
            }, token);
            app.Run(view!.Window);
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
            try { await periodicRefresh.WaitAsync(OwnerConsoleLoopOptions.Default.ShutdownBound, clock, CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
            if (reader is not null)
            {
                try { await reader.WaitAsync(OwnerConsoleLoopOptions.Default.ShutdownBound, clock, CancellationToken.None); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
            }
            if (events is not null || activityLoader is not null)
            {
                try { await (events?.DisposeAsync() ?? activityLoader!.DisposeAsync()).AsTask().WaitAsync(OwnerConsoleLoopOptions.Default.ShutdownBound, clock, CancellationToken.None); }
                catch (TimeoutException) { }
            }
            view?.Dispose();
        }
    }
}
