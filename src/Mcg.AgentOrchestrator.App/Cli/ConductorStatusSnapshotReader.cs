using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ConductorStatusSnapshotReader
{
    internal sealed record Event(DateTimeOffset At, string Kind, string Detail);

    internal static DateTimeOffset? LatestTick(OrchestratorWorkspace workspace) =>
        LatestTick(ReadEvents(workspace.ConductEventsLogPath), workspace.RunEventStorePath);

    private static DateTimeOffset? LatestTick(IReadOnlyList<Event> events, string runEventStorePath)
    {
        var logTick = events.Where(item => item.Kind.StartsWith("tick", StringComparison.OrdinalIgnoreCase))
            .MaxBy(item => item.At)?.At;
        return LatestTick(logTick, runEventStorePath);
    }

    internal static DateTimeOffset? LatestTick(DateTimeOffset? logTick, string runEventStorePath)
    {
        var dbTick = ReadLatestTick(runEventStorePath);
        return logTick is null ? dbTick : dbTick is null ? logTick : (logTick > dbTick ? logTick : dbTick);
    }

    internal static ConductorStatusSnapshot Read(OrchestratorWorkspace workspace, int? owner,
        DateTimeOffset bootTime, Func<string?>? mainCommit = null, ConductorLockOwnership? ownership = null,
        DateTimeOffset? now = null)
    {
        var events = ReadEvents(workspace.ConductEventsLogPath);
        var lastEvent = events.MaxBy(item => item.At);
        var lifecycleEvents = LatestSource(events, workspace.ConductEventsLogPath,
            item => item.Kind is "loop-start" or "loop-stop");
        var lifecycle = lifecycleEvents.Where(item => item.Kind is "loop-start" or "loop-stop").MaxBy(item => item.At);
        if (lifecycle is not null && (lastEvent is null || lifecycle.At > lastEvent.At)) lastEvent = lifecycle;
        var start = lifecycleEvents.Where(item => item.Kind == "loop-start").MaxBy(item => item.At);
        var stop = lifecycleEvents.Where(item => item.Kind == "loop-stop").MaxBy(item => item.At);
        var ready = Latest(events, workspace.ConductEventsLogPath, item => item.Kind == "loop-ready");
        var since = ConductorLifecycleProjection.Project(
            ownership ?? (owner is { } pid ? new ConductorLockOwnership(pid, null, null) : null),
            start?.At, stop is null ? null : (stop.At,
                Regex.Match(stop.Detail, @"\breason=(?<reason>\S+?)(?=\s|\r?$)").Groups["reason"].Value),
            ready?.At, lastEvent?.At, bootTime, lifecycle?.Kind);

        var build = Latest(events, workspace.ConductEventsLogPath, item => item.Kind == "supervisor-build");
        var buildCommit = ReadBuildCommit(build?.Detail);
        var generation = ReadGeneration(buildCommit, mainCommit);
        var tick = LatestTick(events, workspace.RunEventStorePath);

        ConductorStatusSnapshot.GoalsObservation goalSnapshot;
        IReadOnlyList<ProviderUsageHoldRow>? providerHolds = null;
        if (!File.Exists(workspace.SqliteStatePath))
            goalSnapshot = new(false, null, null, null);
        else
        {
            var repository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            var metadata = repository.ListGoalMetadataAsync().GetAwaiter().GetResult();
            var ids = metadata.Where(item => Enum.TryParse<GoalStatus>(item.Status, true, out var status) &&
                    status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded))
                .Select(item => new GoalId(item.Id)).ToArray();
            var kernel = repository.LoadGoalsAsync(ids).GetAwaiter().GetResult();
            var goals = kernel.Goals.Where(goal => !goal.IsTerminal).OrderBy(goal => goal.Id.Value).ToArray();
            providerHolds = ProviderUsageHoldReader.Read(repository, kernel.Goals.Where(goal =>
                !goal.IsTerminal || goal.Status == GoalStatus.Failed).ToArray(), now ?? DateTimeOffset.UtcNow);
            var observations = goals.Select(goal =>
            {
                var task = goal.Tasks.FirstOrDefault(item => item.Status is
                    WorkTaskStatus.Running or WorkTaskStatus.Assigned or WorkTaskStatus.WaitingForHuman)
                    ?? goal.Tasks.FirstOrDefault(item => item.Status == WorkTaskStatus.Pending);
                return new ConductorStatusSnapshot.GoalObservation(goal.Id.Value, goal.Status.ToString(),
                    task?.RequiredRole.ToString() ?? "none", goal.LandingBlocker);
            }).ToArray();
            var pending = goals.SelectMany(goal => kernel.GetPendingHumanInput(goal.Id)).ToArray();
            var clarifications = pending.Count(item => HumanWaitPolicyDefaults.IsSpecClarificationClass(item.Kind));
            goalSnapshot = new(true, observations, clarifications, pending.Length - clarifications);
        }
        var landing = ReadLatestLanding(workspace.GoalLifecycleEventsDirectory);
        return new(owner, since, buildCommit, generation, tick, goalSnapshot,
            landing is null ? null : new(landing.Value.Goal, landing.Value.At),
            ReadAdoption(workspace.RunEventStorePath), ReadIntents(workspace), providerHolds,
            ReadSupervisorJob(build?.Detail));
    }

    private static ConductorStatusSnapshot.AdoptionObservation? ReadAdoption(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            connection.Open();
            using var summary = connection.CreateCommand();
            summary.CommandText = """
                SELECT detail, payload_json FROM run_events
                WHERE event_type = $type AND operation = 'adoption-summary'
                ORDER BY seq DESC LIMIT 1
                """;
            summary.Parameters.AddWithValue("$type", RunEventTypes.ConductorLifecycle);
            string detail;
            string? generation;
            using (var reader = summary.ExecuteReader())
            {
                if (!reader.Read()) return null;
                detail = reader.GetString(0).Trim();
                generation = ReadGenerationId(reader.IsDBNull(1) ? null : reader.GetString(1));
            }

            var deferred = 0;
            if (generation is not null)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT payload_json FROM run_events
                    WHERE event_type = $type AND operation = 'adoption'
                        AND status = 'deferred-identity-unproven'
                    """;
                command.Parameters.AddWithValue("$type", RunEventTypes.ConductorLifecycle);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    if (string.Equals(generation,
                        ReadGenerationId(reader.IsDBNull(0) ? null : reader.GetString(0)), StringComparison.Ordinal))
                        deferred++;
            }
            return new(detail, deferred);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadGenerationId(string? payload)
    {
        if (payload is null) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("generationId", out var generation) &&
                generation.ValueKind == JsonValueKind.String ? generation.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static ConductorStatusSnapshot.IntentsObservation ReadIntents(OrchestratorWorkspace workspace)
    {
        var path = Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
        if (!File.Exists(path))
            return new("unavailable", "intent database missing", []);
        try
        {
            var store = SqliteOperatorIntentStore.OpenExisting(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var ids = store.ListActionableGoalIdsAsync().GetAwaiter().GetResult()
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var summaries = store.ListActionableSummariesAsync(ids).GetAwaiter().GetResult();
            var goals = ids.Select(id =>
            {
                summaries.TryGetValue(id, out var summary);
                return new ConductorStatusSnapshot.IntentGoalObservation(id, summary?.Count ?? 0, summary?.LatestAt);
            }).ToArray();
            return new(ids.Length == 0 ? "none" : "pending", null, goals);
        }
        catch (InvalidOperationException ex)
        {
            return new("unavailable", StoreSchemaDiagnostics.Message(ex), []);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return new("unavailable", null, []);
        }
    }

    private static ConductorStatusSnapshot.GenerationObservation ReadGeneration(string? buildCommit,
        Func<string?>? mainCommit)
    {
        if (string.IsNullOrWhiteSpace(buildCommit))
            return new("no-build", null, null);
        var main = mainCommit?.Invoke()?.Trim();
        var build = buildCommit.Trim();
        if (string.IsNullOrEmpty(main)) return new("main-unavailable", build, null);
        return new(string.Equals(build, main, StringComparison.OrdinalIgnoreCase) ? "current" : "differs", build, main);
    }

    private static Event? Latest(IReadOnlyList<Event> live, string path, Func<Event, bool> kind) =>
        LatestSource(live, path, kind).Where(kind).MaxBy(item => item.At);

    // Start and stop share a source, preserving live-log precedence over rotated generations.
    private static IReadOnlyList<Event> LatestSource(IReadOnlyList<Event> live, string path, Func<Event, bool> kind) =>
        live.Any(kind) ? live : RotatedGenerations(path).Select(ReadRotatedEvents)
            .FirstOrDefault(events => events.Any(kind)) ?? [];

    private static IEnumerable<string> RotatedGenerations(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        if (!Directory.Exists(directory)) return [];
        var pattern = new Regex($@"^{Regex.Escape(Path.GetFileNameWithoutExtension(path))}-(?<stamp>\d{{14}})(?:-(?<index>\d+))?{Regex.Escape(Path.GetExtension(path))}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Select(file => (File: file, Match: pattern.Match(Path.GetFileName(file))))
                .Where(item => item.Match.Success)
                .OrderByDescending(item => item.Match.Groups["stamp"].Value, StringComparer.Ordinal)
                .ThenByDescending(item => BigInteger.TryParse(item.Match.Groups["index"].Value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : BigInteger.Zero)
                .Select(item => item.File).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IReadOnlyList<Event> ReadRotatedEvents(string path)
    {
        try { return ReadEvents(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IReadOnlyList<Event> ReadEvents(string path)
    {
        if (!File.Exists(path)) return [];
        var result = new List<Event>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                if (ParseEventLine(line) is { } item) result.Add(item);
        }
        catch (FileNotFoundException) { return []; }
        return result;
    }

    internal static Event? ParseEventLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return new(root.GetProperty("timestamp").GetDateTimeOffset(),
                root.GetProperty("eventKind").GetString() ?? "",
                root.GetProperty("detail").GetString() ?? "");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        { return null; }
    }

    private static string? ReadBuildCommit(string? detail)
    {
        if (detail is null) return null;
        var start = detail.IndexOf('{');
        if (start < 0) return null;
        try
        {
            using var document = JsonDocument.Parse(detail[start..]);
            return document.RootElement.TryGetProperty("commitSha", out var commit) ? commit.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static ProcessJobReading ReadSupervisorJob(string? detail)
    {
        if (detail is null) return ProcessJobReading.Unknown;
        var start = detail.IndexOf('{');
        if (start < 0) return ProcessJobReading.Unknown;
        try
        {
            using var document = JsonDocument.Parse(detail[start..]);
            var root = document.RootElement;
            if (!root.TryGetProperty("jobMembership", out var membership)) return ProcessJobReading.Unknown;
            bool? inJob = membership.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(membership.GetString(), out var value) => value,
                _ => null
            };
            uint? flags = null;
            if (root.TryGetProperty("innermostJobLimitFlags", out var limitFlags) &&
                limitFlags.ValueKind == JsonValueKind.String && limitFlags.GetString() is { Length: 10 } text &&
                text.StartsWith("0x", StringComparison.Ordinal) &&
                uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed))
                flags = parsed;
            return new(inJob, flags);
        }
        catch (JsonException) { return ProcessJobReading.Unknown; }
    }

    private static DateTimeOffset? ReadLatestTick(string path)
    {
        if (!File.Exists(path)) return null;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT occurred_at FROM run_events WHERE event_type = $type ORDER BY seq DESC LIMIT 1";
        command.Parameters.AddWithValue("$type", RunEventTypes.ConductorTick);
        var value = command.ExecuteScalar() as string;
        return value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    private static (string Goal, DateTimeOffset At)? ReadLatestLanding(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        (string Goal, DateTimeOffset At)? latest = null;
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
        foreach (var line in File.ReadLines(file))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.GetProperty("eventType").GetString() != "GoalLanded") continue;
                var at = root.GetProperty("timestamp").GetDateTimeOffset();
                if (latest is null || at > latest.Value.At)
                    latest = (root.GetProperty("goalId").GetString() ?? "unknown", at);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException) { }
        }
        return latest;
    }
}
