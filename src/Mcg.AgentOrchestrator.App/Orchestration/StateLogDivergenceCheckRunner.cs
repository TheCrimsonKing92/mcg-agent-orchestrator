using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record StateLogDivergenceRunResult(int CheckedGoals, int EmittedEvents,
    IReadOnlyDictionary<string, int> SkippedReasons);

// One instance per conduct command. Only the command-exit path joins background IO.
internal sealed class StateLogDivergenceCheckRunner(
    Func<IOrchestratorStateQueries> queries,
    string goalEventsDirectory,
    string conductEventsLogPath,
    Func<DateTimeOffset>? utcNow = null,
    string? statePath = null)
{
    internal const int MaxGoalsPerRun = 50;
    internal const string StateFileName = "state-log-divergence-state.json";
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    // Preload off the tick too, so the first tick can honor the persisted hourly gate.
    private readonly Task<StateLogDivergenceState> _preload = statePath is null
        ? Task.FromResult(new StateLogDivergenceState())
        : Task.Run(() => StateLogDivergenceStateStore.Load(statePath));
    private StateLogDivergenceState _state = new();
    private Task<StateLogDivergenceRunResult>? _currentRun;
    private DateTimeOffset? _lastStarted;

    internal Task StateLoaded => _preload;

    internal static StateLogDivergenceCheckRunner ForWorkspace(OrchestratorWorkspace workspace) =>
        new(() => SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath),
            workspace.GoalLifecycleEventsDirectory, workspace.ConductEventsLogPath,
            statePath: Path.Combine(workspace.OrchestratorDirectory, StateFileName));

    internal Task<StateLogDivergenceRunResult>? CurrentRun
    {
        get { lock (_gate) return _currentRun; }
    }

    internal bool OnTick()
    {
        lock (_gate)
        {
            if (!_preload.IsCompleted) return false;
            if (_currentRun is { IsCompleted: false }) return false;
            var now = _utcNow();
            if (_preload.Result.LastStartedUtc is { } persisted &&
                (_lastStarted is null || persisted > _lastStarted)) _lastStarted = persisted;
            if (_lastStarted is { } previous && now - previous < Interval) return false;
            _lastStarted = now;
            // Repository and journal construction also perform IO, so both occur off the tick.
            _currentRun = Task.Run(() => RunOnceAsync(now));
            return true;
        }
    }

    internal async Task WaitForCurrentRunAsync()
    {
        await StateLoaded.ConfigureAwait(false);
        if (CurrentRun is { } run) await run.ConfigureAwait(false);
    }

    private async Task<StateLogDivergenceRunResult> RunOnceAsync(DateTimeOffset startedAt)
    {
        var skips = new Dictionary<string, int>(StringComparer.Ordinal);
        var checkedGoals = 0;
        var emittedEvents = 0;
        void Skip(string reason) => skips[reason] = skips.GetValueOrDefault(reason) + 1;
        try
        {
            if (statePath is not null) _state = StateLogDivergenceStateStore.Load(statePath);
            // A different instance may have run since the preload completed.
            if (_state.LastStartedUtc is { } previous && startedAt - previous < Interval)
                return new(0, 0, skips);
            _state.LastStartedUtc = startedAt;
            IOrchestratorStateQueries repository = queries();
            var metadata = await repository.ListGoalMetadataAsync().ConfigureAwait(false);
            var recent = metadata.OrderByDescending(summary => ParseUpdatedAt(summary.UpdatedAt))
                .ThenBy(summary => summary.Id, StringComparer.Ordinal).DistinctBy(summary => summary.Id)
                .Take(MaxGoalsPerRun);
            foreach (var summary in recent)
            {
                if (!DateTimeOffset.TryParse(summary.UpdatedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var updatedAt))
                { Skip("updated-at-unparseable"); continue; }
                var terminal = IsTerminal(summary.Status);
                if (terminal && _state.Goals.TryGetValue(summary.Id, out var remembered) && remembered.Terminal)
                { Skip("terminal-reported"); continue; }
                Goal? goal;
                try
                {
                    var kernel = await repository.LoadGoalsAsync([new GoalId(summary.Id)]).ConfigureAwait(false);
                    goal = kernel.Goals.SingleOrDefault(candidate => candidate.Id.Value == summary.Id);
                    if (goal is null || goal.IsMetadataOnly) { Skip("snapshot-unreadable"); continue; }
                }
                catch { Skip("snapshot-unreadable"); continue; }

                var lines = new List<StateLogLine>();
                try
                {
                    // Goal ids name a single log, never a path supplied by the snapshot.
                    if (summary.Id != Path.GetFileName(summary.Id) || summary.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        throw new IOException("Invalid goal log name.");
                    using var file = new FileStream(Path.Combine(goalEventsDirectory, summary.Id + ".jsonl"),
                        FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(file);
                    while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        if (StateLogDivergenceComparer.ParseLine(line) is { } entry) lines.Add(entry);
                    }
                }
                catch (FileNotFoundException) { Skip("log-missing"); continue; }
                catch (DirectoryNotFoundException) { Skip("log-missing"); continue; }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
                { Skip("log-malformed"); continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Skip("log-unreadable"); continue; }

                var report = StateLogDivergenceComparer.Compare(goal.Timeline.Select(StateLogEntry.FromProgressEvent),
                    lines, updatedAt.UtcTicks);
                checkedGoals++;
                if (!report.HasDivergence) { _state.Goals.Remove(summary.Id); continue; }
                if (_state.Goals.TryGetValue(summary.Id, out var emitted) && emitted.Signature == report.Signature) continue;
                new ConductEventLogWriter(conductEventsLogPath).Append("state-log-divergence", summary.Id,
                    report.FormatDetail(summary.Id), _utcNow());
                // Remember only a successfully written event, so failed journaling is not suppressed.
                _state.Goals[summary.Id] = new(report.Lost, report.Repeated, report.StoredOnly,
                    report.FirstLogCursor, report.KindsText, statePath is not null && terminal);
                emittedEvents++;
            }
            var skipSummary = string.Join(",", skips.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}:{pair.Value}"));
            if (skips.Count > 0 && skipSummary != _state.LastSkipSummary)
                new ConductEventLogWriter(conductEventsLogPath).Append("state-log-divergence-skipped", null,
                    $"STATE_LOG_DIVERGENCE_SKIPPED skipped={skips.Values.Sum()} reasons={skipSummary}", _utcNow());
            _state.LastSkipSummary = skipSummary;
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
        finally
        {
            if (statePath is not null)
            {
                try { StateLogDivergenceStateStore.Save(statePath, _state); }
                catch (Exception ex) { ReportFailure(ex); }
            }
        }
        return new(checkedGoals, emittedEvents, skips);
    }

    private void ReportFailure(Exception ex)
    {
        // Read or journal failures are advisory; never fault the tick or command-exit join.
        try
        {
            new ConductEventLogWriter(conductEventsLogPath).Append("state-log-divergence-failed", null,
                $"STATE_LOG_DIVERGENCE_FAILED exception={ex.GetType().Name}", _utcNow());
        }
        catch { /* The journal itself may be unavailable. */ }
    }

    private static bool IsTerminal(string status) => Enum.TryParse<GoalStatus>(status, true, out var parsed) &&
        parsed is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;

    private static DateTimeOffset ParseUpdatedAt(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result : DateTimeOffset.MinValue;
}
