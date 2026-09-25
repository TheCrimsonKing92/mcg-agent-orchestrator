using System.Globalization;
using System.Text.Json;
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
            var (since, until, json) = Parse(args);
            var digest = Read(workspace, clock ?? new SystemClock(), since, until);
            var writer = output ?? Console.Out;
            if (json)
            {
                writer.WriteLine(JsonSerializer.Serialize(digest, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
            }
            else
            {
                writer.WriteLine($"Owner digest [{digest.Since:O}, {digest.Until:O}) | reverts={digest.Reverts}");
                writer.WriteLine("Goal | Landed UTC | Interventions H/A/O | Landing | Tail h | Mechanical h H/A/O");
                foreach (var row in digest.Goals)
                    writer.WriteLine($"{row.GoalId} | {row.LandedAt:O} | {row.Interventions.Human}/{row.Interventions.Agent}/{row.Interventions.Other} | {row.LandingStatus} | {row.TailHours?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"} | {row.MechanicalHours.Human:0.###}/{row.MechanicalHours.Agent:0.###}/{row.MechanicalHours.Other:0.###}");
                writer.WriteLine($"Totals: landed={digest.Totals.LandedGoals} interventions={digest.Totals.Interventions.Total} H/A/O={digest.Totals.Interventions.Human}/{digest.Totals.Interventions.Agent}/{digest.Totals.Interventions.Other} mean={digest.Totals.MeanInterventionsPerLanding?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a"} correct={digest.Totals.CorrectLandings} escapes={digest.Totals.Escapes} pending={digest.Totals.Pending} correct-rate={digest.Totals.CorrectLandingRate?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a"} tail-median-h={digest.Totals.TailMedianHours?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"} tail-p90-h={digest.Totals.TailP90Hours?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"} tail-known={digest.Totals.KnownTailCount} tail-unknown={digest.Totals.UnknownTailCount} mechanical-h={digest.Totals.MechanicalHours.Total:0.###} H/A/O={digest.Totals.MechanicalHours.Human:0.###}/{digest.Totals.MechanicalHours.Agent:0.###}/{digest.Totals.MechanicalHours.Other:0.###} unresolved-h={digest.Totals.UnresolvedHoldHours:0.###}");
                writer.WriteLine($"Non-landed goals with interventions in window: {digest.NonLandedGoalsWithInterventions}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    internal static OwnerDigestResult Read(OrchestratorWorkspace workspace, IClock clock,
        DateTimeOffset? since = null, DateTimeOffset? until = null)
    {
        if (!File.Exists(workspace.SqliteStatePath))
            throw new FileNotFoundException("State database is missing.", workspace.SqliteStatePath);
        var kernel = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
            .LoadAsync().GetAwaiter().GetResult();
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
        return OwnerDigestReport.Build(inputs.Values.ToArray(), receipts, clock, since, until, malformed);
    }

    private static IReadOnlyList<OwnerDigestCanaryReceipt> ReadCanaryReceipts(string path)
    {
        if (!File.Exists(path))
            return [];
        var rows = new List<OwnerDigestCanaryReceipt>();
        var storeFiles = new[] { path, path + "-wal", path + "-shm" };
        var before = SnapshotStoreFiles(storeFiles);
        var connectionString = new SqliteConnectionStringBuilder
        {
            // An ordinary read-only WAL connection may create -wal and -shm files.
            DataSource = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1",
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
        if (before != SnapshotStoreFiles(storeFiles))
            throw new IOException("Run event store changed while reading the owner digest.");
        return rows;
    }

    private static string SnapshotStoreFiles(IEnumerable<string> paths) =>
        string.Join("|", paths.Select(path =>
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
        }));

    private static (DateTimeOffset? Since, DateTimeOffset? Until, bool Json) Parse(IReadOnlyList<string> args)
    {
        DateTimeOffset? since = null, until = null;
        var json = false;
        for (var i = 1; i < args.Count; i++)
        {
            var flag = args[i];
            if (flag.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                json = true;
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
        return (since, until, json);
    }
}
