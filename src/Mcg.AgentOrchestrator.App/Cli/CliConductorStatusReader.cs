using System.Globalization;
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
        var lifecycle = events.Where(item => item.Kind is "loop-start" or "loop-stop").MaxBy(item => item.At);
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

        var build = events.Where(item => item.Kind == "supervisor-build").MaxBy(item => item.At);
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
