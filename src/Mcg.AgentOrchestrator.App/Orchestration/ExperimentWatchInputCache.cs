using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ExperimentWatchInputCache
{
    internal static readonly TimeSpan FullRefreshInterval = TimeSpan.FromMinutes(60);
    private Dictionary<string, (string UpdatedAt, Goal? Goal)> _goals = new(StringComparer.Ordinal);
    private Dictionary<string, JournalEntry> _journals = new(StringComparer.Ordinal);
    private string? _statePath;
    private DateTimeOffset? _lastFullRead;

    internal int LastHydratedGoalCount { get; private set; }
    internal int LastParsedJournalCount { get; private set; }
    internal int RetainedGoalCount => _goals.Values.Count(entry => entry.Goal is not null);
    internal IReadOnlyCollection<string> RetainedGoalIds => _goals.Where(entry => entry.Value.Goal is not null)
        .Select(entry => entry.Key).ToArray();

    internal void Clear()
    {
        _goals.Clear();
        _lastFullRead = null;
        LastHydratedGoalCount = 0;
    }

    internal IReadOnlyCollection<Goal> ReadGoals(string statePath, DateTimeOffset asOf) =>
        ReadGoals(statePath, asOf, ExperimentGoalScope.All);

    internal IReadOnlyCollection<Goal> ReadGoals(string statePath, DateTimeOffset asOf, ExperimentGoalScope scope)
    {
        if (scope.IsEmpty || !File.Exists(statePath))
        {
            Clear();
            return [];
        }

        var path = Path.GetFullPath(statePath);
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(statePath);
        // Capture versions before hydration so a concurrent save forces another load next tick.
        var metadata = repository.ListGoalMetadataAsync().GetAwaiter().GetResult()
            .Where(row => scope.Includes(row.Id, row.UpdatedAt)).ToArray();
        var fullRead = _lastFullRead is null || _statePath != path ||
            asOf < _lastFullRead.Value || asOf - _lastFullRead.Value >= FullRefreshInterval;
        IReadOnlyCollection<Goal> loaded;
        int hydrated;
        if (fullRead)
        {
            if (ReferenceEquals(scope, ExperimentGoalScope.All))
            {
                loaded = ExperimentGoals.Read(statePath);
                hydrated = loaded.Count;
            }
            else
            {
                var ids = metadata.Select(row => new GoalId(row.Id)).ToArray();
                loaded = ids.Length == 0 ? [] : repository.LoadGoalsAsync(ids).GetAwaiter().GetResult().Goals;
                hydrated = ids.Length;
            }
        }
        else
        {
            var changed = metadata.Where(row => !_goals.TryGetValue(row.Id, out var cached) ||
                cached.UpdatedAt != row.UpdatedAt).Select(row => new GoalId(row.Id)).ToArray();
            loaded = changed.Length == 0 ? [] :
                repository.LoadGoalsAsync(changed).GetAwaiter().GetResult().Goals;
            hydrated = changed.Length;
        }

        var byId = loaded.ToDictionary(goal => goal.Id.Value, StringComparer.Ordinal);
        var next = new Dictionary<string, (string UpdatedAt, Goal? Goal)>(StringComparer.Ordinal);
        foreach (var row in metadata)
        {
            Goal? goal = null;
            if (!byId.TryGetValue(row.Id, out goal) && !fullRead &&
                _goals.TryGetValue(row.Id, out var cached) && cached.UpdatedAt == row.UpdatedAt)
                goal = cached.Goal;
            // A listed but unreadable goal stays absent until its version changes or a full refresh.
            next[row.Id] = (row.UpdatedAt, goal);
        }

        // LoadGoalsAsync's primary-key IN query returns ordinal id order, unlike metadata order.
        var result = fullRead ? loaded : next.Values.Where(entry => entry.Goal is not null)
            .Select(entry => entry.Goal!).OrderBy(goal => goal.Id.Value, StringComparer.Ordinal).ToArray();
        _goals = next;
        _statePath = path;
        if (fullRead) _lastFullRead = asOf;
        LastHydratedGoalCount = hydrated;
        return result;
    }

    internal IReadOnlyDictionary<string, DateTimeOffset> ReadLandings(string directory)
    {
        var next = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);
        var parsed = 0;
        if (Directory.Exists(directory))
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.jsonl"))
            {
                // Snapshot before reading: appends during parsing must invalidate the next tick.
                var length = file.Length;
                var lastWrite = file.LastWriteTimeUtc;
                if (_journals.TryGetValue(file.FullName, out var cached) &&
                    cached.Length == length && cached.LastWriteTimeUtc == lastWrite)
                    next[file.FullName] = cached;
                else
                {
                    next[file.FullName] = new(length, lastWrite, ParseJournal(file.FullName));
                    parsed++;
                }
            }
        }

        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var journal in next.Values)
        foreach (var (id, at) in journal.Landings)
            if (!result.TryGetValue(id, out var previous) || at < previous) result[id] = at;

        _journals = next;
        LastParsedJournalCount = parsed;
        return result;
    }

    private static IReadOnlyDictionary<string, DateTimeOffset> ParseJournal(string path)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("eventType", out var kind) || kind.GetString() != "GoalLanded") continue;
                var id = root.GetProperty("goalId").GetString();
                var at = root.GetProperty("timestamp").GetDateTimeOffset();
                if (id is not null && (!result.TryGetValue(id, out var previous) || at < previous)) result[id] = at;
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        return result;
    }

    private sealed record JournalEntry(long Length, DateTime LastWriteTimeUtc,
        IReadOnlyDictionary<string, DateTimeOffset> Landings);
}
