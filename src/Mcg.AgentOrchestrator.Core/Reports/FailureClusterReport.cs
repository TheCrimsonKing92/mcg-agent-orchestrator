using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public sealed record FailureClusterGoalEvent(string EventType, string Message, string GoalId,
    string? TaskId, DateTimeOffset Timestamp);
public sealed record FailureClusterConductEvent(DateTimeOffset Timestamp, string EventKind,
    string? GoalId, string Detail);
public sealed record FailureClusterOperatorTouch(string GoalId, string? TaskId, DateTimeOffset At);
public sealed record FailureCluster(string Key, string EventKind, ReworkCauseFamily? Family,
    string MessageFamily, string? Marker, string? WorkerCli, int RootEvents, int PaidRounds,
    int KnockOnRounds, int OperatorTouches, double GateMinutes, double TotalCost,
    IReadOnlyList<string> GoalsAffected, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string Query);

/// <summary>Pure cost attribution. A dispatch boundary, rather than another log line, opens a new root.</summary>
public static class FailureClusterReport
{
    public const string DailyEventKind = "failure-clusters";
    public const double PaidRoundWeight = 1;
    public const double KnockOnWeight = 0.5;
    public const double OperatorTouchWeight = 3;
    public const double GateMinuteWeight = 0.1;
    public static IReadOnlyList<string> SteadyStateHoldPrefixes { get; } = Array.AsReadOnly(new[]
    {
        "At worker cap", "acceptance width ", "acceptance verification running in background",
        "Background finding-requested focused evidence is running",
        "Background finding-baseline-arm focused evidence is running",
        "Background finding-candidate-rerun focused evidence is running",
        "Background deferred-no-change focused evidence is running",
        "Background pre-tester-deferred focused evidence is running",
        "Background verification-inconclusive-timed-out-selection-rerun focused evidence is running"
    });

    public static IReadOnlyList<FailureCluster> Build(IEnumerable<FailureClusterGoalEvent> goalEvents,
        IEnumerable<FailureClusterConductEvent> conductEvents,
        IEnumerable<FailureClusterOperatorTouch> operatorTouches, DateTimeOffset since, DateTimeOffset until)
    {
        if (since >= until) throw new ArgumentException("The report window must have since < until.");
        var events = goalEvents.Where(e => e.Timestamp < until).OrderBy(e => e.Timestamp).ToArray();
        var clusters = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var families = new Dictionary<string, ReworkCauseFamily>();
        foreach (var e in RootEvents(events))
        {
            if (!IsFailure(e) || e.EventType == DailyEventKind || IsSteadyHold(e.Message)) continue;
            var message = Normalize(e.Message, e.GoalId, e.TaskId);
            var cli = e.EventType == "TaskFailed" ? WorkerCli(e.Message) : null;
            var marker = Marker(e.Message) ?? cli ?? FailingTest(e.Message);
            ReworkCauseFamily? family = null;
            if (e.EventType == "TaskRetried" || e.Message.StartsWith("Invalidated ", StringComparison.Ordinal))
            {
                if (!families.TryGetValue(e.Message, out var classified))
                    families[e.Message] = classified = Classify(e.Message);
                family = classified;
            }
            var key = Key(e.EventType, family, message, marker);
            // Also consume pre-window roots: a repeated hold after Since is still the same older root.
            if (e.Timestamp < since) continue;
            var row = Get(clusters, key, e.EventType, family, message, marker, cli);
            row.Add(e.GoalId, e.Timestamp);
            if (e.Message.StartsWith("Invalidated ", StringComparison.Ordinal)) row.KnockOn++;
            else if (e.EventType == "TaskRetried") row.Paid++;
        }

        AddGates(clusters, conductEvents.Where(e => e.EventKind != DailyEventKind &&
            e.Timestamp >= since && e.Timestamp < until).OrderBy(e => e.Timestamp).ToArray(),
            events.Select(e => e.GoalId).Distinct(StringComparer.Ordinal).ToArray());
        var touches = operatorTouches.Where(t => t.At >= since && t.At < until).ToArray();
        var dispatches = events.Where(IsDispatch).GroupBy(e => e.GoalId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Timestamp).ToArray());
        foreach (var row in clusters.Values)
        foreach (var (goal, first) in row.FirstByGoal)
        {
            var next = dispatches.GetValueOrDefault(goal)?.FirstOrDefault(t => t > first) ?? default;
            var end = next == default ? until : next;
            row.Touches += touches.Count(t => t.GoalId == goal && t.At >= first && t.At < end);
        }
        return clusters.Values.Select(row => row.Finish(since, until))
            .OrderByDescending(row => row.TotalCost).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
    }

    public static bool IsDispatch(FailureClusterGoalEvent e) =>
        e.EventType is "TaskDispatched" or "TaskStarted" or "TaskProcessStarted";

    /// <summary>Identifies roots independently of whether their kind carries failure cost.</summary>
    public static IEnumerable<FailureClusterGoalEvent> RootEvents(IEnumerable<FailureClusterGoalEvent> events)
    {
        var roots = new HashSet<(string Goal, string Task, string Key)>();
        foreach (var e in events.OrderBy(e => e.Timestamp))
        {
            if (IsDispatch(e))
            {
                roots.RemoveWhere(r => r.Goal == e.GoalId && (r.Task == (e.TaskId ?? "") || r.Task == ""));
                continue;
            }
            if (e.EventType == DailyEventKind) continue;
            var cli = e.EventType == "TaskFailed" ? WorkerCli(e.Message) : null;
            var key = Key(e.EventType, null, Normalize(e.Message, e.GoalId, e.TaskId),
                Marker(e.Message) ?? cli ?? FailingTest(e.Message));
            if (roots.Add((e.GoalId, e.TaskId ?? "", key))) yield return e;
        }
    }

    private static bool IsFailure(FailureClusterGoalEvent e) =>
        e.EventType is "TaskRetried" or "TaskFailed" or "GoalEscalated" ||
        e.Message.StartsWith("Invalidated ", StringComparison.Ordinal) ||
        (e.EventType == "GoalLifecycleDecision" &&
            Regex.IsMatch(e.Message, @"\b(?:held|escalated) at\b", RegexOptions.IgnoreCase));

    public static bool IsSteadyHold(string message)
    {
        var reason = Regex.Replace(message, @"^.*?\b(?:held|escalated) at [^—]+—\s*", "",
            RegexOptions.IgnoreCase).Trim();
        return SteadyStateHoldPrefixes.Any(p => reason.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    public static string Normalize(string message, string? goalId = null, string? taskId = null)
    {
        foreach (var id in new[] { goalId, taskId }.Where(id => !string.IsNullOrEmpty(id)).OrderByDescending(id => id!.Length))
            message = Regex.Replace(message, @"(?<![\w])" + Regex.Escape(id!) + @"(?![\w])", "<id>");
        message = Regex.Replace(message, @"\b\d{4}-\d{2}-\d{2}T[^\s;,]+", "<time>");
        message = Regex.Replace(message, @"\b[a-fA-F0-9]{8}(?:-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}\b", "<id>");
        message = Regex.Replace(message, @"\b[a-fA-F0-9]{7,64}\b", "<id>");
        message = Regex.Replace(message, @"(?:[A-Za-z]:[\\/]|(?<!\w)[/\\]|\b[\w.-]+[/\\])[^\s;,]+", "<path>");
        message = Regex.Replace(message, @"\b\d+(?:\.\d+)?(?:ms|s|m|h|seconds?|minutes?)?\b", "<n>");
        return Regex.Replace(message, @"\s+", " ").Trim();
    }

    private static string? Token(string message, string name)
    {
        var match = Regex.Match(message, @"(?:^|[\s;=])" + Regex.Escape(name) + @"=([^\s;,""']+)");
        return match.Success ? match.Groups[1].Value : null;
    }
    private static string? Marker(string message) => Token(message, "rule") is { } rule ? "rule=" + rule :
        Token(message, "reason") is { } reason ? "reason=" + reason : null;
    private static string? WorkerCli(string message)
    {
        var match = Regex.Match(message, @"^Dispatch failed:.*?exit code \d+:\s*(claude|codex|grok)\b");
        return match.Success ? match.Groups[1].Value : null;
    }
    private static string? FailingTest(string message)
    {
        var match = Regex.Match(message, @"(?:Failed\s+|failing[_ -]test[=:]\s*)([\w.]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static ReworkCauseFamily Classify(string message)
    {
        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        var task = new TaskSnapshot("task", "Report", AgentRole.Developer, WorkTaskStatus.Completed,
            null, null, null, [], null, null);
        var goal = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot("goal", "Report", GoalStatus.Active, [task],
                [new ProgressEventSnapshot("goal", "task", ProgressKind.TaskRetried, message, at)])], [])).Goals.Single();
        return ReworkCauseClassifier.Classify(goal, goal.Tasks.Single(), 2, at.AddSeconds(-1), at, []);
    }

    private static string Key(string kind, ReworkCauseFamily? family, string message, string? marker) =>
        $"{kind}/{family?.ToString() ?? "-"}/{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message))).ToLowerInvariant()}/{marker ?? "-"}";

    private static Accumulator Get(Dictionary<string, Accumulator> rows, string key, string kind,
        ReworkCauseFamily? family, string message, string? marker, string? cli)
    {
        if (!rows.TryGetValue(key, out var row)) rows[key] = row = new(key, kind, family, message, marker, cli);
        return row;
    }

    private static void AddGates(Dictionary<string, Accumulator> rows, FailureClusterConductEvent[] events, string[] goalIds)
    {
        var exits = events.Where(e => e.Detail.StartsWith("ACCEPTANCE_COHORT_EXIT ", StringComparison.Ordinal)).ToArray();
        var seen = new HashSet<(string Goal, string Identity)>();
        string Canonical(string id)
        {
            var matches = goalIds.Where(goal => SameGoal(goal, id)).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : id;
        }
        static string[] Members(string detail)
        {
            var match = Regex.Match(detail, @"(?:^|\s)members=([^\s;]+)");
            return match.Success ? match.Groups[1].Value.Split(',') : [];
        }
        foreach (var phase in events)
        {
            var outcome = Token(phase.Detail, "outcome");
            if (!phase.Detail.StartsWith("PHASE_PROGRESS ", StringComparison.Ordinal) ||
                Token(phase.Detail, "phase") != "gate-phase-breakdown" ||
                Token(phase.Detail, "scope") != "verifier-run" ||
                outcome?.ToLowerInvariant() is not ("failed" or "faulted" or "cancelled" or "infrastructurefailure") ||
                !double.TryParse(Token(phase.Detail, "elapsed_ms"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var ms) || !double.IsFinite(ms) || ms < 0) continue;
            var goal = Canonical(phase.GoalId ?? Token(phase.Detail, "goal") ?? "unknown");
            var attempt = Token(phase.Detail, "attempt") ?? Token(phase.Detail, "gate_attempt_id");
            var phaseMembers = Members(phase.Detail).Select(Canonical).Order(StringComparer.Ordinal).ToArray();
            var exit = exits.FirstOrDefault(e => e.Timestamp >= phase.Timestamp &&
                (SameGoal(goal, e.GoalId ?? Token(e.Detail, "goal")) || Members(e.Detail).Any(id => SameGoal(goal, id))) &&
                (phaseMembers.Length == 0 || phaseMembers.SequenceEqual(Members(e.Detail).Select(Canonical).Order(StringComparer.Ordinal))) &&
                (attempt is null || (Token(e.Detail, "attempt") ?? Token(e.Detail, "gate_attempt_id")) is not { } other || other == attempt));
            // Cohort identity describes its candidates and can recur on a later attempt.
            var identity = attempt ?? (Token(phase.Detail, "cohort") + "/" +
                (exit?.Timestamp ?? phase.Timestamp).ToString("O", CultureInfo.InvariantCulture));
            var marker = exit is null ? "outcome=" + outcome : Marker(exit.Detail) ?? "outcome=" + outcome;
            var message = "gate-phase-breakdown " + marker;
            var row = Get(rows, Key("GateAttempt", null, message, marker), "GateAttempt", null, message, marker, null);
            var members = Members(exit?.Detail ?? phase.Detail).Select(Canonical).Order(StringComparer.Ordinal).ToArray();
            var owner = members.Length > 0 ? string.Join(',', members) : goal;
            // Cohort progress is emitted once per member. Its elapsed time belongs to one attempt.
            if (!seen.Add((owner, identity))) continue;
            row.Add(goal, phase.Timestamp);
            foreach (var member in members) row.AddGoal(member, phase.Timestamp);
            if (exit is not null) row.Last = exit.Timestamp > row.Last ? exit.Timestamp : row.Last;
            row.Minutes += ms / 60000;
        }
    }

    private static bool SameGoal(string left, string? right) => right is not null &&
        (left == right || (Math.Min(left.Length, right.Length) >= 8 &&
            (left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal))));

    private sealed class Accumulator(string key, string kind, ReworkCauseFamily? family,
        string message, string? marker, string? cli)
    {
        internal readonly Dictionary<string, DateTimeOffset> FirstByGoal = new(StringComparer.Ordinal);
        internal int Roots, Paid, KnockOn, Touches;
        internal double Minutes;
        internal DateTimeOffset First = DateTimeOffset.MaxValue, Last = DateTimeOffset.MinValue;
        internal void Add(string goal, DateTimeOffset at)
        {
            Roots++;
            if (at < First) First = at;
            if (at > Last) Last = at;
            AddGoal(goal, at);
        }
        internal void AddGoal(string goal, DateTimeOffset at)
        {
            if (!FirstByGoal.TryGetValue(goal, out var old) || at < old) FirstByGoal[goal] = at;
        }
        internal FailureCluster Finish(DateTimeOffset since, DateTimeOffset until) => new(key, kind, family,
            message, marker, cli, Roots, Paid, KnockOn, Touches, Minutes,
            Paid * PaidRoundWeight + KnockOn * KnockOnWeight + Touches * OperatorTouchWeight + Minutes * GateMinuteWeight,
            FirstByGoal.Keys.Order(StringComparer.Ordinal).ToArray(), First, Last,
            $"failure-clusters --since {since:O} --until {until:O} --json # key={key}");
    }
}
