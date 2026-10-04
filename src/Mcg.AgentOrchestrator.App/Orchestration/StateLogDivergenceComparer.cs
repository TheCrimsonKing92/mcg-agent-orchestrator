using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum StateLogDivergenceClass { Lost, Repeated, StoredOnly, ByDesign }

internal sealed record StateLogEntry(string Kind, long OccurredAtUtcTicks, string Message, string? TaskId)
{
    internal static StateLogEntry FromProgressEvent(ProgressEvent entry) =>
        new(entry.Kind.ToString(), entry.OccurredAt.UtcTicks, entry.Message, entry.TaskId?.Value);

    internal (string Kind, long Ticks, string Message) ExactKey => (Kind, OccurredAtUtcTicks, Message);
    internal (string Kind, string? TaskId, string Message) RepeatKey => (Kind, TaskId, Message);
}

internal sealed record StateLogLine(long Cursor, StateLogEntry Entry);
internal sealed record StateLogDivergence(StateLogDivergenceClass Class, StateLogEntry Entry, long? Cursor);

internal sealed record StateLogDivergenceReport(IReadOnlyList<StateLogDivergence> Items)
{
    internal int Lost => Count(StateLogDivergenceClass.Lost);
    internal int Repeated => Count(StateLogDivergenceClass.Repeated);
    internal int StoredOnly => Count(StateLogDivergenceClass.StoredOnly);
    internal int ByDesign => Count(StateLogDivergenceClass.ByDesign);
    internal bool HasDivergence => Lost + Repeated + StoredOnly > 0;
    internal long FirstLogCursor => Items.Where(item => item.Class is
        StateLogDivergenceClass.Lost or StateLogDivergenceClass.Repeated)
        .Select(item => item.Cursor!.Value).DefaultIfEmpty(0).Min();
    internal (int Lost, int Repeated, int StoredOnly, long FirstCursor) Signature =>
        (Lost, Repeated, StoredOnly, FirstLogCursor);

    private int Count(StateLogDivergenceClass kind) => Items.Count(item => item.Class == kind);

    internal string FormatDetail(string goalId)
    {
        var kinds = string.Join(",", Items.Where(item => item.Class != StateLogDivergenceClass.ByDesign)
            .GroupBy(item => item.Entry.Kind).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}:{group.Count()}"));
        return FormattableString.Invariant($"STATE_LOG_DIVERGENCE goal={goalId[..Math.Min(8, goalId.Length)]} lost={Lost} repeated={Repeated} stored_only={StoredOnly} kinds={kinds} first_log_cursor={FirstLogCursor} by_design={ByDesign}");
    }
}

// Pure comparison: projection-only lines are not timeline events, and exact keys deliberately
// omit task id. Task id participates only in the repeated-at-another-time classification.
internal static class StateLogDivergenceComparer
{
    internal static StateLogDivergenceReport Compare(
        IEnumerable<StateLogEntry> storedTimeline, IEnumerable<StateLogLine> goalEvents,
        long? pendingAfterUtcTicks = null)
    {
        var stored = storedTimeline.ToArray();
        var logged = goalEvents.ToArray();
        var storedKeys = stored.Select(entry => entry.ExactKey).ToHashSet();
        var loggedKeys = logged.Select(line => line.Entry.ExactKey).ToHashSet();
        var repeatKeys = stored.Select(entry => entry.RepeatKey).ToHashSet();
        var items = new List<StateLogDivergence>();
        foreach (var line in logged)
        {
            if (storedKeys.Contains(line.Entry.ExactKey)) continue;
            // ConductorBatchLoop.cs checkpoints after its per-goal walk, while timeline
            // events are logged immediately. Newer-than-checkpoint lines may still be in flight.
            if (pendingAfterUtcTicks is { } cutoff && line.Entry.OccurredAtUtcTicks > cutoff) continue;
            var kind = IsHeldDispositionTelemetry(line.Entry)
                ? StateLogDivergenceClass.ByDesign
                : repeatKeys.Contains(line.Entry.RepeatKey)
                    ? StateLogDivergenceClass.Repeated : StateLogDivergenceClass.Lost;
            items.Add(new(kind, line.Entry, line.Cursor));
        }
        items.AddRange(stored.Where(entry => !loggedKeys.Contains(entry.ExactKey))
            .Select(entry => new StateLogDivergence(StateLogDivergenceClass.StoredOnly, entry, null)));
        return new(items);
    }

    private static bool IsHeldDispositionTelemetry(StateLogEntry entry) =>
        // ConductorBatchLoop.GoalKernelChange.cs::Capture snapshots driver-owned mutation
        // before disposition telemetry. ConductorBatchLoop.cs:1432-1435 excludes held
        // outcomes from changedGoalIds; ::FormatOutcome (:2547) emits "held at".
        // The persisted "held:" call sites (:1168, :1217) are deliberately not exempt.
        entry.Kind == nameof(ProgressKind.GoalPolicyDecision) &&
        Regex.IsMatch(entry.Message, @"\ABatch loop tick [0-9]+: held at ", RegexOptions.CultureInvariant);

    internal static StateLogLine? ParseLine(string line)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Goal event must be an object.");
        // GoalLifecycleEventWriter.cs::AppendTimelineEvent is the only progressKind writer;
        // its other projection methods have no progressKind and are outside this comparison.
        if (!root.TryGetProperty("progressKind", out var kind)) return null;
        if (kind.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(kind.GetString()) ||
            !root.TryGetProperty("occurredAt", out var occurredAt) || occurredAt.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(occurredAt.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp) ||
            !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("cursor", out var cursor) || !cursor.TryGetInt64(out var position) || position < 0)
            throw new FormatException("Goal timeline event has invalid comparison fields.");
        string? taskId = null;
        if (root.TryGetProperty("taskId", out var task))
        {
            if (task.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new FormatException("Goal timeline event has invalid task id.");
            taskId = task.ValueKind == JsonValueKind.String ? task.GetString() : null;
        }
        return new(position, new(kind.GetString()!, timestamp.UtcTicks, message.GetString()!, taskId));
    }
}
