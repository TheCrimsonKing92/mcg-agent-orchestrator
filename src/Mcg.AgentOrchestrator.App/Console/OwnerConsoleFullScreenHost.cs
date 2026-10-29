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

    internal static async Task<int> RunAsync(OrchestratorWorkspace workspace, CancellationToken cancellationToken,
        string? inputTracePath = null, OwnerConsoleHostRowOwner? owners = null, OwnerConsoleBoardSource? boardSource = null)
    {
        if (inputTracePath is null) return await RunCoreAsync(workspace, cancellationToken, null, owners, boardSource);
        try
        {
            using var destination = OwnerConsoleInputTraceOptions.OpenDestination(inputTracePath);
            var diagnostics = new OwnerConsoleInputDiagnostics();
            var result = await RunCoreAsync(workspace, cancellationToken, diagnostics, owners, boardSource);
            // RunCoreAsync has disposed the application and restored the terminal before export.
            diagnostics.Export(destination);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            System.Console.Error.WriteLine($"Input trace failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(OrchestratorWorkspace workspace, CancellationToken cancellationToken,
        OwnerConsoleInputDiagnostics? diagnostics, OwnerConsoleHostRowOwner? owners, OwnerConsoleBoardSource? boardSource)
    {
        var clock = TimeProvider.System;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        IConductEventSource? events = null;
        OwnerConsoleActivityLoader? activityLoader = null;
        Task? fill = null;
        using var rebuild = new SemaphoreSlim(1, 1);
        using IApplication app = OwnerConsoleFrameOutputInstaller.CreateApplication();
        OwnerConsoleFullScreenView? view = null;
        Task? reader = null;
        Task periodicRefresh = Task.CompletedTask;
        Task streamRefresh = Task.CompletedTask;
        OwnerConsoleRefreshCoalescer? coalescer = null;
        var timings = new OwnerConsoleRefreshTimings(clock);
        var timingsFile = new OwnerConsoleRefreshTimingsFile(
            Path.Combine(workspace.LogDirectory, "owner-console-refresh-timings.txt"), clock, Environment.ProcessId);
        void Post(string phase, Action action) => app.Invoke(diagnostics?.ObservePost(phase, action) ?? action);
        try
        {
            IOrchestratorStateQueries state = File.Exists(workspace.SqliteStatePath)
                ? SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath) : new EmptyOwnerConsoleStateQueries();
            var refreshFilter = new OwnerConsoleEventRefreshFilter();
            var snapshots = new OwnerConsoleRefreshGoalSnapshots(state);
            var questions = new SerializedOwnerQuestionSource(new OwnerQuestionReadModel(state, workspace.OrchestratorDirectory, snapshots));
            var cohortEvidence = new OwnerActivityEvidenceReader(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            var gateEvidence = new OwnerGateFailureEvidence(workspace.OrchestratorDirectory);
            var jointEvidence = new OwnerJointGateFailureEvidence(workspace.OrchestratorDirectory);
            var conductTail = new OwnerConsoleJsonlTail(workspace.ConductEventsLogPath);
            var durationPath = AcceptanceLaneDurationStore.ResolveStorePath(workspace.RootDirectory);
            var durationTail = new OwnerConsoleJsonlTail(durationPath);
            var gateMotion = new OwnerGateMotionReader(workspace.ConductEventsLogPath, clock, conductTail);
            var latestTick = new OwnerConsoleLatestTickReader(conductTail, workspace.RunEventStorePath);
            var gateProgress = new OwnerConsoleGateProgressLoader(workspace.ConductEventsLogPath,
                Path.Combine(workspace.RootDirectory, "config", "acceptance-manifest.json"),
                durationPath, clock,
                executorConfigPath: RemoteLaneExecutorConfiguration.ResolveStorePath(workspace.RootDirectory),
                conductTail: conductTail, durationTail: durationTail, owners: owners);
            var changes = new ChangeStreamFileReader(Path.Combine(workspace.LogDirectory, Orchestration.ChangeStreamWriter.FileName));
            var builder = new OwnerConsoleViewModelBuilder(state, questions,
                new ConductorLeaseLiveness(workspace.OrchestratorDirectory), new PortfolioGoalEpicLookup(workspace.PortfolioStorePath), clock,
                item => item.EventKind == "acceptance-cohort" ? jointEvidence.Read(item, cohortEvidence.Read(item)) :
                    gateEvidence.Read(item) ?? jointEvidence.Read(item), id => timings.Time("gate-motion", () => gateMotion.Describe(id)),
                latestTick: () => timings.Time("latest-tick", latestTick.Read),
                gateProgress: titles => timings.Time("gate-progress", () => gateProgress.Load(titles)), snapshots: snapshots);
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
                message => app.Invoke(() => { if (!token.IsCancellationRequested) view!.ShowNotice(message, OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Refresh); }));

            async Task RefreshAsync(IReadOnlyList<OwnerConductEvent>? items = null, bool skipIfBusy = false, OwnerConsoleRefreshScope? scope = null)
            {
                using var trace = diagnostics?.Begin("refresh-work", detail: skipIfBusy ? "skip-if-busy" : "queued");
                if (skipIfBusy)
                {
                    if (!await rebuild.WaitAsync(0, token)) return;
                }
                else await rebuild.WaitAsync(token);
                try
                {
                    if (clock.GetLocalNow().Date != landingDate)
                    { landingDate = clock.GetLocalNow().Date; landings = 0; countedLandings.Clear(); }
                    foreach (var item in items ?? [])
                    {
                        last = item.Timestamp;
                        if (OwnerActivityNarrator.IsLanding(item) &&
                            TimeZoneInfo.ConvertTime(item.Timestamp, clock.LocalTimeZone).Date == landingDate && countedLandings.Add(item)) landings++;
                        OwnerConsoleStartupActivity.Append(recent, item, OwnerConsoleStartupActivity.MaxRawEvents);
                    }
                    last = refreshFilter.FoldLast(last);
                    var inputs = new OwnerConsoleViewInputs(opened, last, recent.ToArray(), landings);
                    await refreshOperation.RunAsync(items is not { Count: > 0 } ? "refresh" : "refresh after conductor event", async stepToken =>
                    {
                        using var goalLoads = snapshots.BeginRefresh();
                        var board = await timings.TimeAsync("board", () => builder.BuildBoardAsync(stepToken));
                        boardIds = board.Board.Select(row => row.GoalId).ToArray();
                        if (boardSource is not null) board = await boardSource.ComposeAsync(board, stepToken);
                        foreach (var lifecycle in timings.Time("lifecycle-tail", () => activityLoader.ReadNew(boardIds, scope ?? OwnerConsoleRefreshScope.All).ToArray())) OwnerConsoleStartupActivity.Append(recent, builder.EnrichEvent(lifecycle), OwnerConsoleStartupActivity.MaxRawEvents);
                        var programHealth = OwnerConsoleProgramHealthNotice.Current(recent); app.Invoke(() => { if (!token.IsCancellationRequested) view!.NoticeStrip.SetProgramHealth(programHealth); });
                        var (decisions, hidden) = await timings.TimeAsync("decisions", () => builder.ReadDecisionsAsync(stepToken));
                        var landedToday = timings.Time("landed-today", () => OwnerConsoleLandedToday.Count(workspace.GoalLifecycleEventsDirectory, clock));
                        var model = timings.Time("activity", () => builder.WithActivity(builder.WithDecisions(board, decisions, hidden),
                            inputs with { RecentEvents = recent.ToArray(),
                                LandedToday = Math.Max(landings, landedToday) }));
                        stepToken.ThrowIfCancellationRequested();
                        Post("refresh-render", () =>
                        {
                            if (!token.IsCancellationRequested)
                                timings.Time("render", () => { view!.Render(model); return true; });
                        });
                    }, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                { app.Invoke(() => view!.ShowNotice(ex.Message, OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Refresh)); }
                finally
                {
                    rebuild.Release();
                    diagnostics?.Mark("refresh-loaders", detail: timings.Detail());
                    timingsFile.Write(timings.Detail());
                }
            }

            coalescer = new((items, scope) => RefreshAsync(items, scope: scope));

            rebuild.Wait(token);
            try
            {
                // Keep Init, initial Render and Run on this host thread. Metadata is
                // read on the pool before GUI initialization, with the existing bound.
                var board = OwnerConsoleStartupLoader.LoadingBoard(Task.Run(async () =>
                {
                    var own = await builder.BuildBoardAsync(token);
                    boardIds = own.Board.Select(row => row.GoalId).ToArray();
                    return boardSource is null ? own : await boardSource.ComposeAsync(own, token);
                }, token)
                    .WaitAsync(OwnerConsoleLoopOptions.Default.OperationBound, clock, token).GetAwaiter().GetResult());
                app.Init();
                var notifications = OwnerConsoleNotificationSettings.Load(
                    Path.Combine(workspace.OrchestratorDirectory, "owner-console-settings.json"));
                var controller = new OwnerConsoleScreenController(questions, new AttentionAnswerHandlerAdapter(workspace),
                    new TerminalGuiOwnerConsoleDialogs(app, token), new OwnerGoalDetailQuery(state,
                        new GoalEventFileTail(workspace.GoalLifecycleEventsDirectory), summary: OwnerGoalChangeSummaryQuery.ForWorkspace(workspace),
                        evidence: new(Path.Combine(workspace.OrchestratorDirectory, "pre-review-evidence-attempts"), clock)),
                    new CliConductorConsoleAdapter(workspace), new CliOwnerDigestConsoleAdapter(workspace),
                    new OwnerDigestSummaryAdapter(workspace), clock, new WorkspaceEpicProgressSource(workspace),
                    resolutions: new OwnerQuestionResolutionReader(state, new GoalEventFileTail(workspace.GoalLifecycleEventsDirectory),
                        workspace.OrchestratorDirectory, workspace.LogDirectory, clock),
                    notifier: new SystemOwnerConsoleNotifier(new WindowsOwnerConsoleSoundOutput(), notifications, File.Exists),
                    notifications: notifications,
                    fleet: () => OwnerFleetViewModelBuilder.Load(workspace, new ConductorLeaseLiveness(workspace.OrchestratorDirectory), owners: owners),
                    landings: argument => OwnerLandingsPage.Load(state, workspace.OrchestratorDirectory, clock, argument));
                view = new(app, controller, () => RefreshAsync(), token, clock, OwnerConsoleLoopOptions.Default, diagnostics,
                    projectLabel: OwnerConsoleProjectLabel.ForWorkspace(workspace), boardSource: boardSource);
                view.Render(board);
                var startup = new OwnerConsoleStartupLoader(builder, activityLoader, action => Post("startup-render", action), clock);
                fill = startup.FillAsync(board, model => view.Render(model), new(opened, last, [], 0), loaded =>
                {
                    recent = loaded.Recent.Select(builder.EnrichEvent).ToList();
                    last = loaded.LastActivity;
                    landings = loaded.LandedToday;
                    foreach (var landing in loaded.Recent.Where(OwnerActivityNarrator.IsLanding)) countedLandings.Add(landing);
                }, token, ownBoardGoalIds: boardIds);
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
                    message => app.Invoke(() => { if (!token.IsCancellationRequested) view!.ShowNotice(message, OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Refresh); }))
                    .RunAsync(item => { refreshFilter.Route(item, coalescer); return Task.CompletedTask; }, token);
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
            streamRefresh = Task.Run(() => new OwnerConsoleStreamRefresh(changes,
                scope => { coalescer.Request(null, scope); return Task.CompletedTask; }, clock).RunAsync(token), token);
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
            try { await streamRefresh.WaitAsync(OwnerConsoleLoopOptions.Default.ShutdownBound, clock, CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
            if (reader is not null)
            {
                try { await reader.WaitAsync(OwnerConsoleLoopOptions.Default.ShutdownBound, clock, CancellationToken.None); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
            }
            if (coalescer is not null)
            {
                try { await coalescer.Completion.WaitAsync(OwnerConsoleLoopOptions.Default.ShutdownBound, clock, CancellationToken.None); }
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
