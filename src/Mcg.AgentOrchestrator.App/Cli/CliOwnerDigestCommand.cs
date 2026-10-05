using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliOwnerDigestCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("owner-digest", StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        IClock? clock = null, TextWriter? output = null)
    {
        try
        {
            var (since, until, json, rounds) = Parse(args);
            var digest = Read(workspace, clock ?? new SystemClock(), out var goals, since, until);
            var panel = CliOwnerDigestJudgePanel.Read(workspace, digest, goals);
            var writer = output ?? Console.Out;
            IReadOnlyList<AppliedRetryIntent> intents = rounds
                ? CliOwnerDigestRetryIntents.Read(workspace, digest.Until) : [];
            if (json)
            {
                using var buffer = panel is null ? null : new StringWriter(CultureInfo.InvariantCulture);
                var jsonWriter = buffer ?? writer;
                if (rounds)
                    CliOwnerDigestRounds.WriteJson(jsonWriter, digest, goals, intents);
                else
                    WriteJson(jsonWriter, digest);
                if (panel is not null)
                {
                    var root = new JsonObject();
                    CliOwnerDigestJudgePanel.AddJson(root, panel);
                    // Append to the serialized object without re-encoding existing property values.
                    var legacyJson = buffer!.ToString().TrimEnd();
                    writer.Write(legacyJson.AsSpan(0, legacyJson.Length - 1));
                    writer.Write(',');
                    writer.WriteLine(root.ToJsonString(new JsonSerializerOptions
                        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })[1..]);
                }
            }
            else
            {
                WriteDigestText(writer, digest);
                CliOwnerDigestLessons.WriteText(writer, workspace, digest.Since, digest.Until);
                CliOwnerDigestBoardFill.WriteText(writer, workspace, goals, digest.Until);
                if (rounds)
                    CliOwnerDigestRounds.WriteText(writer, digest, goals, intents);
                if (panel is not null)
                    CliOwnerDigestJudgePanel.WriteText(writer, panel);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    internal static void WriteDigestText(TextWriter writer, OwnerDigestResult digest)
    {
        writer.WriteLine($"Owner digest [{digest.Since:O}, {digest.Until:O}) | reverts={digest.Reverts}");
        writer.WriteLine("Goal | Landed UTC | Interventions H/A/O | Landing | Tail h | Mechanical h H/A/O");
        foreach (var row in digest.Goals)
            writer.WriteLine($"{row.GoalId} | {row.LandedAt:O} | {row.Interventions.Human}/{row.Interventions.Agent}/{row.Interventions.Other} | {(digest.Escapes is not null && row.EscapeSource is not null ? $"escape({row.EscapeSource})" : row.LandingStatus)} | {row.TailHours?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"} | {row.MechanicalHours.Human:0.###}/{row.MechanicalHours.Agent:0.###}/{row.MechanicalHours.Other:0.###}");
        writer.WriteLine($"Totals: landed={digest.Totals.LandedGoals} interventions={digest.Totals.Interventions.Total} H/A/O={digest.Totals.Interventions.Human}/{digest.Totals.Interventions.Agent}/{digest.Totals.Interventions.Other} mean={digest.Totals.MeanInterventionsPerLanding?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a"} correct={digest.Totals.CorrectLandings} escapes={digest.Totals.Escapes} pending={digest.Totals.Pending} correct-rate={digest.Totals.CorrectLandingRate?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a"} tail-median-h={digest.Totals.TailMedianHours?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"} tail-p90-h={digest.Totals.TailP90Hours?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"} tail-known={digest.Totals.KnownTailCount} tail-unknown={digest.Totals.UnknownTailCount} mechanical-h={digest.Totals.MechanicalHours.Total:0.###} H/A/O={digest.Totals.MechanicalHours.Human:0.###}/{digest.Totals.MechanicalHours.Agent:0.###}/{digest.Totals.MechanicalHours.Other:0.###} unresolved-h={digest.Totals.UnresolvedHoldHours:0.###}");
        WriteEscapes(writer, digest);
        writer.WriteLine($"Non-landed goals with interventions in window: {digest.NonLandedGoalsWithInterventions}");
    }

    private static void WriteEscapes(TextWriter writer, OwnerDigestResult digest)
    {
        if (digest.Escapes is not { } records) return;
        writer.WriteLine($"Escapes: records={records.Count}");
        foreach (var record in records)
        {
            var reason = Regex.Replace(record.Reason, @"\s+", " ");
            writer.WriteLine($"{GoalPrefix(record.GoalId)} | {(record.FoundByGoalId is null ? "-" : GoalPrefix(record.FoundByGoalId))} | {record.RecordedAt.ToUniversalTime():O} | {reason[..Math.Min(80, reason.Length)]}");
        }
    }

    private static string GoalPrefix(string id) => id[..Math.Min(8, id.Length)];

    internal static void WriteJson(TextWriter writer, OwnerDigestResult digest)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        if (digest.Escapes is null)
            writer.WriteLine(JsonSerializer.Serialize(digest, options));
        else
        {
            var root = JsonSerializer.SerializeToNode(digest, options)!.AsObject();
            AddEscapeSources(root, digest);
            writer.WriteLine(root.ToJsonString(options));
        }
    }

    internal static void AddEscapeSources(JsonObject root, OwnerDigestResult digest)
    {
        if (digest.Escapes is null) return;
        var rows = root["goals"]!.AsArray();
        for (var index = 0; index < digest.Goals.Count; index++)
            if (digest.Goals[index].EscapeSource is { } source)
                rows[index]!.AsObject()["escapeSource"] = source;
    }

    internal static OwnerDigestResult Read(OrchestratorWorkspace workspace, IClock clock,
        DateTimeOffset? since = null, DateTimeOffset? until = null)
        => Read(workspace, clock, out _, since, until);

    internal static OwnerDigestResult Read(OrchestratorWorkspace workspace, IClock clock,
        out IReadOnlyList<Goal> goals, DateTimeOffset? since = null, DateTimeOffset? until = null)
    {
        if (!File.Exists(workspace.SqliteStatePath))
            throw new FileNotFoundException("State database is missing.", workspace.SqliteStatePath);
        var kernel = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
            .LoadAsync().GetAwaiter().GetResult();
        goals = kernel.Goals.ToArray();
        var inputs = kernel.Goals.ToDictionary(g => g.Id.Value,
            g => new OwnerDigestGoalInput(g.Id.Value, null, null, g.Timeline),
            StringComparer.OrdinalIgnoreCase);
        var malformed = 0;
        if (Directory.Exists(workspace.GoalLifecycleEventsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl"))
            {
                foreach (var line in File.ReadLines(file))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        if (!root.TryGetProperty("eventType", out var kind) || kind.GetString() != "GoalLanded")
                            continue;
                        var id = root.GetProperty("goalId").GetString()!;
                        var at = root.GetProperty("timestamp").GetDateTimeOffset();
                        var sha = root.TryGetProperty("mainSha", out var mainSha) ? mainSha.GetString() : null;
                        if (inputs.TryGetValue(id, out var existing))
                        {
                            if (existing.LandedAt is null || at < existing.LandedAt)
                                inputs[id] = existing with { LandedAt = at, LandingSha = sha };
                            else if (existing.LandingSha is null && sha is not null)
                                inputs[id] = existing with { LandingSha = sha };
                        }
                        else
                            inputs[id] = new OwnerDigestGoalInput(id, at, sha, []);
                    }
                    catch (JsonException) { malformed++; }
                    catch (FormatException) { malformed++; }
                    catch (KeyNotFoundException) { malformed++; }
                    catch (InvalidOperationException) { malformed++; }
                }
            }
        }

        var receipts = ReadCanaryReceipts(workspace.RunEventStorePath);
        var escapes = new SqliteOperatorEscapeStore(workspace.OperatorEscapesStorePath).List()
            .Select(r => new OwnerDigestEscapeRecord(r.GoalId, r.FoundByGoalId, r.RecordedAt, r.Reason)).ToArray();
        return OwnerDigestReport.Build(inputs.Values.ToArray(), receipts, clock, since, until, malformed, escapes);
    }

    private static IReadOnlyList<OwnerDigestCanaryReceipt> ReadCanaryReceipts(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("Warning: run-events database is missing; canary outcomes are unavailable.");
            return [];
        }
        var rows = new List<OwnerDigestCanaryReceipt>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT occurred_at, status, payload_json FROM run_events WHERE event_type = $type AND operation = 'receipt' ORDER BY seq";
        command.Parameters.AddWithValue("$type", RunEventTypes.PostLandingCanary);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var status = reader.GetString(1);
            if (status is not "Passed" and not "Failed")
                continue;
            using var payload = JsonDocument.Parse(reader.GetString(2));
            var sha = payload.RootElement.TryGetProperty("landingSha", out var value) ? value.GetString() : null;
            rows.Add(new OwnerDigestCanaryReceipt(sha,
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                status == "Passed"));
        }
        return rows;
    }

    internal static (DateTimeOffset? Since, DateTimeOffset? Until, bool Json, bool Rounds) Parse(IReadOnlyList<string> args)
    {
        DateTimeOffset? since = null, until = null;
        var json = false;
        var rounds = false;
        for (var i = 1; i < args.Count; i++)
        {
            var flag = args[i];
            if (flag.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                json = true;
                continue;
            }
            if (flag.Equals("--rounds", StringComparison.OrdinalIgnoreCase))
            {
                rounds = true;
                continue;
            }
            if (i + 1 >= args.Count ||
                !Regex.IsMatch(args[i + 1], @"^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) ||
                !DateTimeOffset.TryParse(args[i + 1], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                throw new ArgumentException(CliCommandHelp.OwnerDigestUsage);
            if (flag.Equals("--since", StringComparison.OrdinalIgnoreCase)) since = date;
            else if (flag.Equals("--until", StringComparison.OrdinalIgnoreCase)) until = date;
            else throw new ArgumentException(CliCommandHelp.OwnerDigestUsage);
            i++;
        }
        return (since, until, json, rounds);
    }
}
