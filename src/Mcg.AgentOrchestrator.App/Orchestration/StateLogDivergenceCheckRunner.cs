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
    Func<DateTimeOffset>? utcNow = null)
{
    internal const int MaxGoalsPerRun = 50;
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, (int Lost, int Repeated, int StoredOnly, long FirstCursor)> _emitted = new();
    private Task<StateLogDivergenceRunResult>? _currentRun;
    private DateTimeOffset? _lastStarted;
    private string? _lastSkipSummary;

    internal static StateLogDivergenceCheckRunner ForWorkspace(OrchestratorWorkspace workspace) =>
        new(() => SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath),
            workspace.GoalLifecycleEventsDirectory, workspace.ConductEventsLogPath);

    internal Task<StateLogDivergenceRunResult>? CurrentRun
    {
        get { lock (_gate) return _currentRun; }
    }

    internal bool OnTick()
    {
        lock (_gate)
        {
            if (_currentRun is { IsCompleted: false }) return false;
            var now = _utcNow();
            if (_lastStarted is { } previous && now - previous < Interval) return false;
            _lastStarted = now;
            // Repository and journal construction also perform IO, so both occur off the tick.
            _currentRun = Task.Run(RunOnceAsync);
            return true;
        }
    }

    internal async Task WaitForCurrentRunAsync()
    {
        if (CurrentRun is { } run) await run.ConfigureAwait(false);
    }

    private async Task<StateLogDivergenceRunResult> RunOnceAsync()
    {
        var skips = new Dictionary<string, int>(StringComparer.Ordinal);
        var checkedGoals = 0;
        var emittedEvents = 0;
        void Skip(string reason) => skips[reason] = skips.GetValueOrDefault(reason) + 1;
        try
        {
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
                if (!report.HasDivergence) { _emitted.Remove(summary.Id); continue; }
                if (_emitted.TryGetValue(summary.Id, out var signature) && signature == report.Signature) continue;
                new ConductEventLogWriter(conductEventsLogPath).Append("state-log-divergence", summary.Id,
                    report.FormatDetail(summary.Id), _utcNow());
                // Remember only a successfully written event, so failed journaling is not suppressed.
                _emitted[summary.Id] = report.Signature;
                emittedEvents++;
            }
            var skipSummary = string.Join(",", skips.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}:{pair.Value}"));
            if (skips.Count > 0 && skipSummary != _lastSkipSummary)
                new ConductEventLogWriter(conductEventsLogPath).Append("state-log-divergence-skipped", null,
                    $"STATE_LOG_DIVERGENCE_SKIPPED skipped={skips.Values.Sum()} reasons={skipSummary}", _utcNow());
            _lastSkipSummary = skipSummary;
        }
        catch (Exception ex)
        {
            // Read or journal failures are advisory; never fault the tick or command-exit join.
            try
            {
                new ConductEventLogWriter(conductEventsLogPath).Append("state-log-divergence-failed", null,
                    $"STATE_LOG_DIVERGENCE_FAILED exception={ex.GetType().Name}", _utcNow());
            }
            catch { /* The journal itself may be unavailable. */ }
        }
        return new(checkedGoals, emittedEvents, skips);
    }

    private static DateTimeOffset ParseUpdatedAt(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result : DateTimeOffset.MinValue;
}
