using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Simulation;

internal static class BoardHistoryTraceReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static BoardHistoryTrace Read(string orchestratorDirectory,
        DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        if (windowEnd <= windowStart) throw new ArgumentException("The trace window must be positive.");
        var gates = new List<RecordedGateRun>();
        var skipped = new List<SkippedTraceInput>();
        var events = new List<(string Goal, string Kind, DateTimeOffset At)>();
        void ReadInput(string path, Action read)
        {
            try { read(); }
            catch (Exception ex) when (IsInputFailure(ex)) { skipped.Add(new(path, ex.Message)); }
        }
        void AddGate(RecordedGateRun gate)
        {
            if (gate.EndAt < gate.BeginAt || gate.Members.Count == 0 ||
                gate.Members.Any(m => string.IsNullOrWhiteSpace(m.GoalId)))
                throw new FormatException("Gate has an invalid interval or members.");
            if (gate.BeginAt < windowEnd && gate.EndAt >= windowStart) gates.Add(gate);
        }

        var trainPath = Path.Combine(orchestratorDirectory, "merge-train-acceptance.db");
        if (File.Exists(trainPath)) ReadInput(trainPath, () =>
        {
            using var connection = OpenReadOnly(trainPath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT train_id FROM merge_train_receipts ORDER BY train_id";
            var ids = new List<string>();
            using (var rows = command.ExecuteReader()) while (rows.Read()) ids.Add(rows.GetString(0));
            var store = MergeTrainAcceptanceStore.OpenReadOnly(trainPath);
            foreach (var id in ids) ReadInput(trainPath + "#" + id, () =>
            {
                MergeTrainReceipt? receipt;
                try { receipt = store.TryReadReceipt(id); }
                catch (NullReferenceException ex)
                {
                    // The existing DTO reader dereferences a JSON null payload. Classify that
                    // malformed external row here without changing the store's contract.
                    throw new FormatException("Malformed train receipt payload.", ex);
                }
                if (receipt is null) throw new FormatException("Train receipt could not be read.");
                if (receipt.Outcome == MergeTrainGateOutcome.Invalidated) return;
                AddGate(new(GateRunKind.Train, receipt.Identity.Members.Select(m =>
                    new RecordedGateMember(m.GoalId.Value, m.CandidateRevision, m.LandingPaths, m.ResourceKeys)).ToArray(),
                    receipt.CompletedAt.AddMilliseconds(-receipt.GateElapsedMilliseconds), receipt.CompletedAt,
                    receipt.Outcome switch
                    {
                        MergeTrainGateOutcome.Passed => GateRunOutcome.Passed,
                        MergeTrainGateOutcome.Failed => GateRunOutcome.Failed,
                        _ => GateRunOutcome.Infrastructure
                    }));
            });
        });

        var cohortPath = Path.Combine(orchestratorDirectory, "cohort-acceptance.db");
        if (File.Exists(cohortPath)) ReadInput(cohortPath, () =>
        {
            using var connection = OpenReadOnly(cohortPath);
            using var command = connection.CreateCommand();
            // LEFT JOIN also exposes malformed receipts with no members instead of silently losing them.
            command.CommandText = """
                SELECT r.cohort_id, r.completed_at, r.gate_elapsed_ms, r.outcome,
                       m.goal_id, m.candidate_revision, m.landing_paths_json, m.resource_keys_json
                FROM cohort_receipts r LEFT JOIN cohort_members m ON r.cohort_id = m.cohort_id
                ORDER BY r.cohort_id, m.member_ordinal
                """;
            using var rows = command.ExecuteReader();
            var groups = new Dictionary<string, List<object[]>>(StringComparer.Ordinal);
            while (rows.Read())
            {
                var values = new object[8];
                rows.GetValues(values);
                var id = rows.GetString(0);
                if (!groups.TryGetValue(id, out var group)) groups[id] = group = [];
                group.Add(values);
            }
            foreach (var (id, group) in groups) ReadInput(cohortPath + "#" + id, () =>
            {
                var first = group[0];
                var outcome = Enum.Parse<AcceptanceCohortGateOutcome>((string)first[3]);
                if (outcome == AcceptanceCohortGateOutcome.Invalidated) return;
                var end = DateTimeOffset.Parse((string)first[1], CultureInfo.InvariantCulture);
                if (group.Count != 2) throw new FormatException("A cohort must have two members.");
                var members = group.Select(r => new RecordedGateMember((string)r[4], (string)r[5],
                    JsonSerializer.Deserialize<string[]>((string)r[6], Json) ?? throw new FormatException("Null landing paths."),
                    JsonSerializer.Deserialize<string[]>((string)r[7], Json) ?? throw new FormatException("Null resource keys."))).ToArray();
                AddGate(new(GateRunKind.Cohort, members, end.AddMilliseconds(-Convert.ToInt64(first[2])), end,
                    outcome switch
                    {
                        AcceptanceCohortGateOutcome.Passed => GateRunOutcome.Passed,
                        AcceptanceCohortGateOutcome.Failed => GateRunOutcome.Failed,
                        _ => GateRunOutcome.Infrastructure
                    }));
            });
        });

        var attempts = Path.Combine(orchestratorDirectory, "acceptance-gate-attempts");
        foreach (var path in Files(attempts, "*.attempt.json", SearchOption.AllDirectories, skipped))
            ReadInput(path, () =>
            {
                var attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(path), Json)
                    ?? throw new FormatException("Null gate attempt.");
                if (attempt.Kind != "gate" || attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running) return;
                var scope = RepositoryLandingScopeNormalization.Normalize(attempt.ScopePaths ?? [], reserveUnknownScope: true);
                AddGate(new(GateRunKind.Solo,
                    [new(attempt.GoalId, attempt.BranchHeadSha ?? "", scope.Paths, scope.ResourceKeys)],
                    attempt.StartedAt, attempt.CompletedAt ?? attempt.LastHeartbeatAt,
                    attempt.Outcome switch
                    {
                        ConductorParallelAcceptanceAttemptOutcome.Passed => GateRunOutcome.Passed,
                        ConductorParallelAcceptanceAttemptOutcome.Failed => GateRunOutcome.Failed,
                        _ => GateRunOutcome.Infrastructure
                    }));
            });

        var eventDirectory = Path.Combine(orchestratorDirectory, "goal-events");
        foreach (var path in Files(eventDirectory, "*.jsonl", SearchOption.TopDirectoryOnly, skipped))
            ReadInput(path, () =>
            {
                if (File.GetLastWriteTimeUtc(path) < windowStart.UtcDateTime) return;
                var lineNumber = 0;
                foreach (var line in File.ReadLines(path))
                {
                    lineNumber++;
                    ReadInput(path + ":" + lineNumber, () =>
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var kind = root.GetProperty("eventType").GetString();
                        if (kind is not ("TaskDispatched" or "TaskVerified" or "GoalLanded")) return;
                        var at = root.TryGetProperty("occurredAt", out var occurred) && occurred.ValueKind != JsonValueKind.Null
                            ? occurred.GetDateTimeOffset() : root.GetProperty("timestamp").GetDateTimeOffset();
                        var goal = root.GetProperty("goalId").GetString();
                        if (string.IsNullOrWhiteSpace(goal)) throw new FormatException("Missing event goal id.");
                        events.Add((goal, kind, at));
                    });
                }
            });

        var orderedGates = gates.OrderBy(g => g.BeginAt).ThenBy(g => g.Members[0].GoalId, StringComparer.Ordinal).ToArray();
        var epochs = new List<RecordedReadinessEpoch>();
        foreach (var goal in orderedGates.SelectMany(g => g.Members.Select(m => (Gate: g, Member: m))).GroupBy(x => x.Member.GoalId))
        {
            var dispatches = events.Where(e => e.Goal == goal.Key && e.Kind == "TaskDispatched")
                .Select(e => e.At).Distinct().Order().ToArray();
            foreach (var epoch in goal.GroupBy(x => dispatches.Where(t => t < x.Gate.BeginAt).Cast<DateTimeOffset?>().LastOrDefault()))
            {
                var first = epoch.First();
                var lastBegin = epoch.Max(x => x.Gate.BeginAt);
                var verified = events.Where(e => e.Goal == goal.Key && e.Kind == "TaskVerified" &&
                    (!epoch.Key.HasValue || e.At > epoch.Key.Value) && e.At <= first.Gate.BeginAt)
                    .Select(e => e.At).Cast<DateTimeOffset?>().Max();
                var leave = dispatches.Where(t => t > lastBegin).Cast<DateTimeOffset?>().FirstOrDefault();
                epochs.Add(new(goal.Key, verified ?? first.Gate.BeginAt, leave, first.Member.CandidateRevision,
                    first.Member.LandingPaths, first.Member.ResourceKeys));
            }
        }
        var landings = events.Where(e => e.Kind == "GoalLanded" && e.At >= windowStart && e.At < windowEnd)
            .GroupBy(e => e.Goal).Select(g => new RecordedLanding(g.Key, g.Min(e => e.At)))
            .OrderBy(l => l.LandedAt).ThenBy(l => l.GoalId, StringComparer.Ordinal).ToArray();
        return new(windowStart, windowEnd, orderedGates,
            epochs.OrderBy(e => e.ReadyAt).ThenBy(e => e.GoalId, StringComparer.Ordinal).ToArray(), landings, skipped);
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static string[] Files(string directory, string pattern, SearchOption search, List<SkippedTraceInput> skipped)
    {
        if (!Directory.Exists(directory)) return [];
        try { return Directory.GetFiles(directory, pattern, search).Order(StringComparer.Ordinal).ToArray(); }
        catch (Exception ex) when (IsInputFailure(ex)) { skipped.Add(new(directory, ex.Message)); return []; }
    }

    private static bool IsInputFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        JsonException or SqliteException or InvalidOperationException or ArgumentException or FormatException or
        KeyNotFoundException or InvalidCastException or OverflowException;
}
