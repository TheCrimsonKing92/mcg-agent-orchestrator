using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliConductorStatusReader
{
    private sealed record Event(DateTimeOffset At, string Kind, string Detail);

    internal static void Print(OrchestratorWorkspace workspace, int? owner, DateTimeOffset bootTime, TextWriter output,
        Func<string?>? mainCommit = null)
    {
        var events = ReadEvents(workspace.ConductEventsLogPath);
        var lastEvent = events.MaxBy(item => item.At);
        var lifecycle = Latest(events, workspace.ConductEventsLogPath, item => item.Kind is "loop-start" or "loop-stop");
        if (lifecycle is not null && (lastEvent is null || lifecycle.At > lastEvent.At)) lastEvent = lifecycle;
        output.WriteLine(owner is null ? "Conductor: stopped" : $"Conductor: running (pid {owner})");
        if (owner is null && lastEvent is not null && bootTime > lastEvent.At)
            output.WriteLine($"stopped since host restart at {bootTime:O}");
        else if (lifecycle is { Kind: "loop-stop" })
        {
            var reason = Regex.Match(lifecycle.Detail, @"\breason=(?<reason>\S+?)(?=\s|\r?$)").Groups["reason"].Value;
            output.WriteLine($"Stopped since {lifecycle.At:O}; reason={reason}");
        }
        else if (lifecycle is { Kind: "loop-start" })
            output.WriteLine($"Running since {lifecycle.At:O}");
        else
            output.WriteLine("Since: unavailable");

        var build = Latest(events, workspace.ConductEventsLogPath, item => item.Kind == "supervisor-build");
        var buildCommit = ReadBuildCommit(build?.Detail);
        output.WriteLine($"Supervisor build: {buildCommit ?? "unavailable"}");
        output.WriteLine(GenerationLine(buildCommit, mainCommit));
        var logTick = events.Where(item => item.Kind.StartsWith("tick", StringComparison.OrdinalIgnoreCase))
            .MaxBy(item => item.At)?.At;
        var dbTick = ReadLatestTick(workspace.RunEventStorePath);
        var tick = logTick is null ? dbTick : dbTick is null ? logTick :
            (logTick > dbTick ? logTick : dbTick);
        output.WriteLine($"Latest tick: {tick?.ToString("O", CultureInfo.InvariantCulture) ?? "unavailable"}");

        if (!File.Exists(workspace.SqliteStatePath))
        {
            output.WriteLine("Goals: unavailable (state database missing)");
            output.WriteLine("Pending clarifications: unavailable; human waits: unavailable");
        }
        else
        {
            var repository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            var metadata = repository.ListGoalMetadataAsync().GetAwaiter().GetResult();
            var ids = metadata.Where(item => Enum.TryParse<GoalStatus>(item.Status, true, out var status) &&
                    status is not (GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded))
                .Select(item => new GoalId(item.Id)).ToArray();
            var kernel = repository.LoadGoalsAsync(ids).GetAwaiter().GetResult();
            var goals = kernel.Goals.Where(goal => !goal.IsTerminal).OrderBy(goal => goal.Id.Value).ToArray();
            output.WriteLine($"Non-terminal goals: {goals.Length}");
            foreach (var goal in goals)
            {
                var task = goal.Tasks.FirstOrDefault(item => item.Status is
                    WorkTaskStatus.Running or WorkTaskStatus.Assigned or WorkTaskStatus.WaitingForHuman)
                    ?? goal.Tasks.FirstOrDefault(item => item.Status == WorkTaskStatus.Pending);
                output.WriteLine($"  {goal.Id.Value} | {goal.Status} | role={task?.RequiredRole.ToString() ?? "none"}");
            }
            var pending = goals.SelectMany(goal => kernel.GetPendingHumanInput(goal.Id)).ToArray();
            var clarifications = pending.Count(item => HumanWaitPolicyDefaults.IsSpecClarificationClass(item.Kind));
            output.WriteLine($"Pending clarifications: {clarifications}; human waits: {pending.Length - clarifications}");
        }
        var landing = ReadLatestLanding(workspace.GoalLifecycleEventsDirectory);
        output.WriteLine(landing is null
            ? "Most recent landing: unavailable"
            : $"Most recent landing: {landing.Value.Goal} at {landing.Value.At:O}");
        output.WriteLine(ReadAdoptionLine(workspace.RunEventStorePath));
        WriteIntentSection(workspace, owner, output);
    }

    private static string ReadAdoptionLine(string path)
    {
        const string unavailable = "Worker adoption: unavailable";
        if (!File.Exists(path)) return unavailable;
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
                if (!reader.Read()) return unavailable;
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
            return $"Worker adoption: {detail} deferred-identity-unproven={deferred}";
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return unavailable;
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

    private static void WriteIntentSection(OrchestratorWorkspace workspace, int? owner, TextWriter output)
    {
        var path = Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
        if (!File.Exists(path))
        {
            output.WriteLine("Pending operator intents: unavailable (intent database missing)");
            return;
        }
        try
        {
            var store = SqliteOperatorIntentStore.OpenExisting(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var ids = store.ListActionableGoalIdsAsync().GetAwaiter().GetResult()
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var summaries = store.ListActionableSummariesAsync(ids).GetAwaiter().GetResult();
            output.WriteLine(ids.Length == 0 ? "Pending operator intents: none" :
                $"Pending operator intents: {ids.Length} goal(s)");
            foreach (var id in ids)
            {
                summaries.TryGetValue(id, out var summary);
                var latest = summary?.LatestAt?.ToString("O", CultureInfo.InvariantCulture) ?? "none";
                var hint = owner is null ? $" | apply with: conductor apply-intents {id[..Math.Min(8, id.Length)]}" : "";
                output.WriteLine($"  {id} | intents={summary?.Count ?? 0} | latest={latest}{hint}");
            }
        }
        catch (InvalidOperationException ex)
        {
            output.WriteLine($"Pending operator intents: unavailable (setup required: {ex.Message})");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine("Pending operator intents: unavailable");
        }
    }

    private static string GenerationLine(string? buildCommit, Func<string?>? mainCommit)
    {
        if (string.IsNullOrWhiteSpace(buildCommit))
            return "Generation: unavailable (no supervisor build recorded)";
        var main = mainCommit?.Invoke()?.Trim();
        if (string.IsNullOrEmpty(main)) return "Generation: main unavailable";
        var build = buildCommit.Trim();
        return string.Equals(build, main, StringComparison.OrdinalIgnoreCase)
            ? $"Generation: current with main {main}"
            : $"Generation: differs from main (running {build}, main {main})";
    }

    private static Event? Latest(IReadOnlyList<Event> live, string path, Func<Event, bool> kind) =>
        live.Where(kind).MaxBy(item => item.At) ?? RotatedGenerations(path)
            .Select(file => ReadRotatedEvents(file).Where(kind).MaxBy(item => item.At))
            .FirstOrDefault(item => item is not null);

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
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                result.Add(new Event(root.GetProperty("timestamp").GetDateTimeOffset(),
                    root.GetProperty("eventKind").GetString() ?? "",
                    root.GetProperty("detail").GetString() ?? ""));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException) { }
        }
        return result;
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
