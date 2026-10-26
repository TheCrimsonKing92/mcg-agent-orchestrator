using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Reads bounded attempt metadata; it never adopts, reconciles or repairs an attempt.
internal static class ConductorFinishedAttemptScanner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static IReadOnlyList<ConductorFinishedAttempt> Scan(IReadOnlyList<string> roots,
        IReadOnlyList<(GoalId Id, GoalStatus Status)> goals, DateTimeOffset now,
        IReadOnlyDictionary<string, List<ConductorGoalLine>>? goalLines = null)
        => Scan(roots, goals, now, Directories, goalLines);

    internal static IReadOnlyList<ConductorFinishedAttempt> Scan(IReadOnlyList<string> roots,
        IReadOnlyList<(GoalId Id, GoalStatus Status)> goals, DateTimeOffset now,
        Func<string, IReadOnlyList<string>> directories,
        IReadOnlyDictionary<string, List<ConductorGoalLine>>? goalLines = null)
    {
        var active = goals.Where(goal => goal.Status is not (GoalStatus.Parked or GoalStatus.Completed or
            GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded))
            .Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal);
        var newest = new Dictionary<(string Goal, string Kind), (ConductorParallelAcceptanceAttempt Attempt, string Path)>();
        var cutoff = (now - ConductorInvariantThresholds.FinishedAttemptScanWindow).UtcDateTime;
        foreach (var root in roots)
        foreach (var directory in directories(root))
        foreach (var path in AttemptPaths(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(stream, JsonOptions);
                if (attempt is null || string.IsNullOrWhiteSpace(attempt.AttemptId)) continue;
                var goalId = string.IsNullOrWhiteSpace(attempt.GoalId) ? Path.GetFileName(directory) : attempt.GoalId;
                if (!active.Contains(goalId)) continue;
                attempt = attempt with { GoalId = goalId };
                var key = (goalId, attempt.Kind);
                if (!newest.TryGetValue(key, out var previous) || attempt.StartedAt > previous.Attempt.StartedAt ||
                    (attempt.StartedAt == previous.Attempt.StartedAt &&
                     string.CompareOrdinal(attempt.AttemptId, previous.Attempt.AttemptId) > 0))
                    newest[key] = (attempt, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        }

        var finished = new Dictionary<string, ConductorFinishedAttempt>(StringComparer.Ordinal);
        foreach (var (attempt, path) in newest.Values)
        {
            if (attempt.ReconciledAt is not null) continue;
            try
            {
                var directory = Path.GetDirectoryName(path)!;
                string Resolve(string value) => Path.GetFullPath(value, directory);
                var candidates = new[] { attempt.ExitCodePath, attempt.ResultPath,
                    attempt.AttemptId + ".exit.txt", attempt.AttemptId + ".result.json" };
                var finishPath = candidates.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(Resolve).FirstOrDefault(File.Exists);
                if (finishPath is null) continue;
                var finishedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(finishPath));
                if (attempt.CompletedAt is { } completed && completed < finishedAt) finishedAt = completed;
                IReadOnlyList<ConductorGoalLine> lines = goalLines is not null &&
                    goalLines.TryGetValue(attempt.GoalId, out var retained) ? retained : [];
                ConductorGoalLine? latest = null;
                foreach (var line in lines)
                    if (latest is null || line.At >= latest.At) latest = line;
                finished[attempt.AttemptId] = new(attempt.AttemptId, attempt.GoalId, attempt.Kind,
                    attempt.StartedAt, finishedAt, ConductorFinishedAttemptDiagnosis.Describe(
                        attempt.AttemptId, finishedAt, path, lines), latest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return finished.Values.OrderBy(attempt => attempt.AttemptId, StringComparer.Ordinal).ToArray();
    }

    private static string[] Directories(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string[] AttemptPaths(string directory)
    {
        try { return Directory.GetFiles(directory, "*.attempt.json"); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
