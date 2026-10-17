using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal enum OwnerConsoleGateLaneState { Queued, Running, Done, Cached }
internal sealed record OwnerConsoleGateMember(string Id, string Title);
internal sealed record OwnerConsoleGateLane(string Name, OwnerConsoleGateLaneState State,
    string Outcome, string Location, TimeSpan Elapsed, double? MedianSeconds, bool IsEstimate,
    double? QuietSeconds = null, bool IsManifestLane = true,
    string? Target = null, string? HeartbeatPath = null, bool IsAttribution = false,
    IReadOnlyList<string>? FailingTestNames = null);
internal sealed record OwnerConsoleGateManifestLane(string Name, double EstimatedSerialSeconds);
internal sealed record OwnerConsoleGateDuration(string LaneName, double DurationSeconds, DateTimeOffset RecordedAt);

// A snapshot of observation, never an authority for scheduling or acceptance.
internal sealed record OwnerConsoleGateProgress(bool Live, string Kind,
    IReadOnlyList<OwnerConsoleGateMember> Members, DateTimeOffset? StartedAt, TimeSpan Elapsed,
    IReadOnlyList<OwnerConsoleGateLane> Lanes, double Fraction, int RemainingSeconds,
    int DoneCount, int TotalCount, TimeSpan? IdleAge = null,
    IReadOnlyList<OwnerConsoleGateProgress>? OtherLiveGates = null,
    IReadOnlyList<OwnerConsoleHostActivity>? HostActivity = null)
{
    internal bool Failing => Lanes.Any(lane => !lane.IsAttribution &&
        lane.State == OwnerConsoleGateLaneState.Done && lane.Outcome == "failed");

    private const string AttemptPattern = @"[0-9a-f]{8}-gate-[0-9]{17}-[0-9a-f]{32}";
    private static readonly Regex AttemptId = new("^" + AttemptPattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HeartbeatAttempt = new("^" + AttemptPattern + @"(?=\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Fields = new(
        """(?:^|\s)(?<key>[\w]+)=(?<value>"(?:\\.|[^"\\])*"|[^\s"]+)""",
        RegexOptions.CultureInvariant);
    internal static OwnerConsoleGateProgress Idle(TimeSpan? age = null) =>
        new(false, "idle", [], null, TimeSpan.Zero, [], 0, 0, 0, 0, age);

    internal OwnerConsoleGateProgress? ForMember(string id) =>
        Live && Members.Any(member => Prefix(member.Id) == Prefix(id)) ? this :
            OtherLiveGates?.FirstOrDefault(gate => gate.Members.Any(member => Prefix(member.Id) == Prefix(id)));

    internal static OwnerConsoleGateProgress Build(DateTimeOffset now, IEnumerable<OwnerConductEvent> events,
        IReadOnlyList<OwnerConsoleGateManifestLane> manifest, IEnumerable<OwnerConsoleGateDuration> durations,
        Func<string, string?>? title = null, IReadOnlyDictionary<string, double>? quietSeconds = null)
    {
        var parsed = events.OrderBy(item => item.Timestamp).Select(item => new Observation(item, Tokens(item.Detail))).ToArray();
        var breakdownConcurrency = BreakdownConcurrency(parsed);
        var gates = new Dictionary<string, Gate>(StringComparer.Ordinal);
        DateTimeOffset? lastEnd = null;
        foreach (var item in parsed)
        {
            var detail = item.Event.Detail;
            var cohort = detail.StartsWith("ACCEPTANCE_COHORT", StringComparison.Ordinal);
            if (detail.StartsWith("ACCEPTANCE_COHORT_CHILD_COMPLETED", StringComparison.Ordinal)) lastEnd = item.Event.Timestamp;
            var members = item.Get("members").Split([',', '+'], StringSplitOptions.RemoveEmptyEntries)
                .Select(Prefix).Distinct(StringComparer.Ordinal).ToArray();
            if (members.Length == 0)
            {
                var goal = item.Get("goal");
                if (goal.Length == 0) goal = item.Event.GoalId ?? "";
                if (goal.Length == 0 || goal == "unknown") continue;
                members = [Prefix(goal)];
                // Solo-attributed progress for an existing cohort belongs to that cohort.
                var existing = gates.Values.LastOrDefault(gate => gate.Live && gate.Members.Contains(members[0]));
                // Goal-only cohort diagnostics describe waiting or fault handling, not a new gate.
                if (cohort && existing is null) continue;
                if (existing is not null) members = existing.Members;
            }
            var key = string.Join("+", members.Order(StringComparer.Ordinal));
            var solo = detail.StartsWith("ACCEPTANCE ", StringComparison.Ordinal);
            var progress = item.Event.EventKind == "gate-progress";
            if (!cohort && !solo && !progress) continue;
            var outcome = item.Get("outcome");
            var terminal = cohort && (detail.StartsWith("ACCEPTANCE_COHORT_CHILD_COMPLETED", StringComparison.Ordinal) ||
                detail.StartsWith("ACCEPTANCE_COHORT_RECONCILED_DEAD", StringComparison.Ordinal) ||
                detail.StartsWith("ACCEPTANCE_COHORT_ADOPTION_REFUSED", StringComparison.Ordinal) ||
                detail.Contains("ABANDON", StringComparison.Ordinal) ||
                (detail.StartsWith("ACCEPTANCE_COHORT_EXIT ", StringComparison.Ordinal) ||
                 detail.StartsWith("ACCEPTANCE_COHORT ", StringComparison.Ordinal)) &&
                    outcome.Length > 0 && outcome is not ("inflight" or "gathering" or "unpaired")) ||
                solo && item.Get("result") is not ("" or "started" or "running");
            if (terminal)
            {
                if (!gates.TryGetValue(key, out var ended))
                {
                    ended = new(members, item.Event.Timestamp);
                    gates[key] = ended;
                }
                ended.Live = false;
                continue;
            }
            var starts = detail.StartsWith("ACCEPTANCE_COHORT_ENTRY ", StringComparison.Ordinal) || solo && item.Get("result") == "started";
            if (!gates.TryGetValue(key, out var gate) || starts || !gate.Live)
            {
                // Late diagnostics must not resurrect a gate whose terminal event was observed.
                if (gate is { Live: false } && !starts) continue;
                gate = new(members, item.Event.Timestamp);
                gates[key] = gate;
            }
            gate.Events.Add(item);
            gate.Last = item.Event.Timestamp;
        }
        var history = durations.Where(item => double.IsFinite(item.DurationSeconds) && item.DurationSeconds > 0).ToArray();
        var live = gates.Values.Where(gate => gate.Live && now - gate.Last <= TimeSpan.FromMinutes(15))
            .OrderBy(gate => gate.Start).Select(Snapshot).ToArray();
        return live.Length == 0 ? Idle(lastEnd is null ? null : Nonnegative(now - lastEnd.Value)) :
            live[0] with { OtherLiveGates = live.Skip(1).ToArray() };

        OwnerConsoleGateProgress Snapshot(Gate gate)
        {
            var attempts = gate.Events.Select(item => AttemptFromHeartbeat(item.Get("heartbeat")))
                .Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Legacy receipts and gates without attempt evidence retain interval/goal binding.
            var remote = parsed.Where(item => item.Event.EventKind == "remote-lane" &&
                (attempts.Count > 0 && AttemptId.IsMatch(item.Get("attempt"))
                    ? attempts.Contains(item.Get("attempt"))
                    : item.Event.Timestamp >= gate.Start &&
                        (string.IsNullOrEmpty(item.Event.GoalId) || gate.Members.Contains(Prefix(item.Event.GoalId))))).ToArray();
            var names = manifest.Select(lane => lane.Name).ToList();
            foreach (var item in gate.Events.Where(item => IsLanePhase(item.Get("phase"))))
            {
                var target = MatchLane(item.Get("target"), manifest);
                if (target.Length > 0 && !names.Contains(target)) names.Add(target);
            }
            foreach (var item in remote.Where(item => item.Get("decision") == "offer"))
            {
                var target = MatchLane(item.Get("lane"), manifest);
                if (target.Length > 0 && !names.Contains(target)) names.Add(target);
            }
            var rows = names.Select(name =>
            {
                var lane = manifest.FirstOrDefault(lane => lane.Name == name);
                var samples = history.Where(item => MatchLane(item.LaneName, manifest) == name)
                    .OrderByDescending(item => item.RecordedAt).Take(10).Select(item => item.DurationSeconds).Order().ToArray();
                double? median = samples.Length == 0 ? lane?.EstimatedSerialSeconds :
                    (samples[(samples.Length - 1) / 2] + samples[samples.Length / 2]) / 2;
                var observations = gate.Events.Where(item => IsLanePhase(item.Get("phase")) &&
                    MatchLane(item.Get("target"), manifest) == name).ToArray();
                var latest = observations.LastOrDefault();
                var remoteLane = remote.Where(item => MatchLane(item.Get("lane"), manifest) == name).ToArray();
                var offer = remoteLane.LastOrDefault(item => item.Get("decision") == "offer");
                if (offer is not null && (latest is null || offer.Event.Timestamp > latest.Event.Timestamp)) latest = offer;
                var state = latest?.Get("phase") switch
                {
                    "verification-check" or "remote-shard-started" => OwnerConsoleGateLaneState.Running,
                    "shard-complete" or "remote-shard-complete" or "check-complete" => OwnerConsoleGateLaneState.Done,
                    "cache-hit" or "reuse" => OwnerConsoleGateLaneState.Cached,
                    _ => offer is null ? OwnerConsoleGateLaneState.Queued : OwnerConsoleGateLaneState.Running
                };
                var elapsed = TimeSpan.FromMilliseconds(latest?.Number("elapsed_ms") ?? 0);
                if (state == OwnerConsoleGateLaneState.Running)
                    elapsed += Nonnegative(now - (latest?.Event.Timestamp ?? offer!.Event.Timestamp));
                var executor = latest?.Get("phase") is "remote-shard-started" or "remote-shard-complete"
                    ? remoteLane.LastOrDefault(item => item.Get("executor").Length > 0)?.Get("executor") ??
                        observations.LastOrDefault(item => item.Get("executor").Length > 0)?.Get("executor") : null;
                var slot = Regex.Match(latest?.Get("heartbeat") ?? "", @"activity-(\d+)");
                var location = executor ?? (slot.Success ? "local slot " + slot.Groups[1].Value :
                    latest?.Get("decision") == "offer" || latest?.Get("phase") is "remote-shard-started" or "remote-shard-complete" ? "remote" : "local");
                double? quiet = null;
                if (state == OwnerConsoleGateLaneState.Running && latest?.Get("phase") == "verification-check")
                {
                    if (quietSeconds?.TryGetValue(name, out var supplied) == true ||
                        quietSeconds?.TryGetValue(latest.Get("target"), out supplied) == true) quiet = supplied;
                    else quiet = SecondsSinceOutput(now, observations);
                }
                return new OwnerConsoleGateLane(name, state, latest?.Get("outcome") is "passed" or "failed" ?
                    latest.Get("outcome") : "unknown", location, elapsed, median, samples.Length == 0, quiet, lane is not null,
                    latest?.Get("target"), observations.LastOrDefault(item => AttemptFromHeartbeat(item.Get("heartbeat")).Length > 0)?.Get("heartbeat"),
                    IsAttributionTarget(latest?.Get("target") ?? name));
            }).ToArray();
            var counted = rows.Where(row => row.IsManifestLane || row.MedianSeconds is not null).ToArray();
            var total = counted.Sum(row => row.MedianSeconds ?? 0);
            var done = counted.Where(IsDone).ToArray();
            var unfinished = counted.Where(row => !IsDone(row)).ToArray();
            var concurrency = Math.Max(breakdownConcurrency, rows.Count(row => row.IsManifestLane && row.State == OwnerConsoleGateLaneState.Running));
            var remaining = OwnerConsoleGateRemainingEstimate.Makespan(unfinished, concurrency);
            var kind = gate.Events.Any(item => item.Get("train").Length > 0 || item.Get("scope") == "merge-train") ? "train" :
                gate.Members.Length > 1 ? "cohort" : "solo";
            return new(true, kind, gate.Members.Select(id => new OwnerConsoleGateMember(id, title?.Invoke(id) ?? "")).ToArray(),
                gate.Start, Nonnegative(now - gate.Start), rows, total > 0 ? done.Sum(row => row.MedianSeconds ?? 0) / total : 0,
                (int)remaining, done.Count(row => row.IsManifestLane), manifest.Count);
        }
    }

    internal static OwnerConductEvent? ParseEvent(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return new(root.GetProperty("timestamp").GetDateTimeOffset(), root.GetProperty("eventKind").GetString() ?? "",
                root.TryGetProperty("goalId", out var goal) ? goal.GetString() : null, root.GetProperty("detail").GetString() ?? "");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    private static double? SecondsSinceOutput(DateTimeOffset now, Observation[] observations)
    {
        var samples = observations.Where(item => item.Get("phase") == "verification-check" &&
            item.Get("output_bytes").Length > 0 && item.Get("child_pid") is not ("" or "unknown")).ToArray();
        if (samples.Length == 0) return null;
        var newest = samples[^1];
        var start = newest.Event.Timestamp;
        foreach (var item in samples.Reverse())
        {
            if (item.Get("child_pid") != newest.Get("child_pid") || item.Get("output_bytes") != newest.Get("output_bytes")) break;
            start = item.Event.Timestamp;
        }
        return Math.Max(0, (now - start).TotalSeconds);
    }

    private static bool IsLanePhase(string phase) => phase is "verification-check" or "shard-complete" or "remote-shard-complete" or "check-complete" or "remote-shard-started" or "cache-hit" or "reuse";
    private static string AttemptFromHeartbeat(string path) => HeartbeatAttempt.Match(path.Split(['/', '\\']).Last()).Value;
    private static bool IsAttributionTarget(string target) =>
        target.StartsWith("reviewer focused evidence: ", StringComparison.Ordinal) ||
        target.StartsWith("reviewer mapped project evidence: ", StringComparison.Ordinal);
    private static bool IsDone(OwnerConsoleGateLane row) => row.State is OwnerConsoleGateLaneState.Done or OwnerConsoleGateLaneState.Cached;
    private static TimeSpan Nonnegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
    private static string Prefix(string id) => id.Trim()[..Math.Min(8, id.Trim().Length)];
    private static string MatchLane(string target, IReadOnlyList<OwnerConsoleGateManifestLane> manifest) =>
        manifest.FirstOrDefault(lane => target == lane.Name || target == "infrastructure tests: " + lane.Name)?.Name ??
        manifest.OrderByDescending(lane => lane.Name.Length).FirstOrDefault(lane =>
            target.StartsWith(lane.Name + ":", StringComparison.Ordinal) ||
            target.StartsWith("infrastructure tests: " + lane.Name + ":", StringComparison.Ordinal))?.Name ?? target;

    private static int BreakdownConcurrency(Observation[] parsed)
    {
        var newest = parsed.LastOrDefault(item => item.Event.Detail.StartsWith("PHASE_PROGRESS ", StringComparison.Ordinal) &&
            item.Get("phase") == "gate-phase-breakdown");
        foreach (var field in (newest?.Get("target") ?? "").Split(';'))
        {
            var separator = field.IndexOf('=');
            if (separator < 0 || !field[..separator].Equals("shard_concurrency_effective", StringComparison.Ordinal)) continue;
            return int.TryParse(field[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
                value >= 1 ? value : 1;
        }
        return 1;
    }

    private static Dictionary<string, string> Tokens(string detail) => Fields.Matches(detail).Cast<Match>()
        .GroupBy(match => match.Groups["key"].Value).ToDictionary(group => group.Key, group =>
        {
            var value = group.Last().Groups["value"].Value;
            return value.StartsWith('"') ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal) : value;
        }, StringComparer.Ordinal);
    private sealed record Observation(OwnerConductEvent Event, Dictionary<string, string> Fields)
    {
        internal string Get(string key) => Fields.GetValueOrDefault(key, "");
        internal double Number(string key) => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            double.IsFinite(value) && value >= 0 ? value : 0;
    }
    private sealed class Gate(string[] members, DateTimeOffset start)
    {
        internal string[] Members { get; } = members;
        internal DateTimeOffset Start { get; } = start;
        internal DateTimeOffset Last { get; set; } = start;
        internal bool Live { get; set; } = true;
        internal List<Observation> Events { get; } = [];
    }
}
