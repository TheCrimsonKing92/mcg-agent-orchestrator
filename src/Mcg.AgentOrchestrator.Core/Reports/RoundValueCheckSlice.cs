using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public sealed record RoundValueCheckOutcomeCount(string OutcomeClass, int Rounds);

public sealed record RoundValueCheckRow(string Check, int Rounds, int Productive, int Overhead,
    int Wasted, int GoalsLanded, int GoalsLost, IReadOnlyList<RoundValueCheckOutcomeCount> OutcomeClasses);

/// <summary>One recorded check attribution per rework round in the terminal goal cohort.</summary>
public sealed record RoundValueCheckSlice(DateTimeOffset Since, DateTimeOffset Until,
    IReadOnlyList<RoundValueCheckRow> Rows)
{
    public const string Unattributed = "unattributed";
    private const string StructuralPrefix = "developer-completion structural pre-check failed: ";
    private const string AcceptancePrefix = "Acceptance criteria unmet; retrying task with feedback";
    // Same recorded size-line shape as MechanicalReworkClassifier, applied to historical rounds.
    private static readonly Regex SizeLine = new(
        @"^(?<path>\S+) has \d+ lines, exceeding the recorded ceiling of \d+\.", RegexOptions.CultureInvariant);
    private static readonly Regex ClassLine = new(
        @"^class:(?<name>\S+) has \d+, exceeding the ", RegexOptions.CultureInvariant);

    public static RoundValueCheckSlice Build(IEnumerable<Goal> goals, DateTimeOffset since, DateTimeOffset until,
        IReadOnlyCollection<AppliedRetryIntent>? intents = null)
    {
        if (since >= until) throw new ArgumentException("The round-value window must end after it starts.");
        var goalMap = goals.ToDictionary(g => g.Id.Value, StringComparer.Ordinal);
        var cohort = RoundValueClassifier.Classify(goalMap.Values, intents ?? []).GroupBy(r => r.Round.GoalId)
            .Where(g => g.First().Outcome != RoundGoalOutcome.Pending &&
                g.Max(r => r.Round.DispatchedAt) >= since && g.Max(r => r.Round.DispatchedAt) < until);
        var rows = new Dictionary<string, Counts>(StringComparer.Ordinal) { [Unattributed] = new() };
        foreach (var value in cohort.SelectMany(g => g).Where(r => r.Round.RoundIndex > 1))
        {
            var round = value.Round;
            var goal = goalMap[round.GoalId];
            var task = goal.Tasks.Single(t => t.Id.Value == round.TaskId);
            var dispatches = task.DispatchHistory.OrderBy(d => d.DispatchedAt).DistinctBy(d => d.DispatchedAt).ToArray();
            var key = Attribute(goal, task, round, dispatches[round.RoundIndex - 2].DispatchedAt);
            if (!rows.TryGetValue(key, out var counts)) rows[key] = counts = new();
            counts.Rounds++;
            switch (value.ValueClass)
            {
                case RoundValueClass.Productive: counts.Productive++; break;
                case RoundValueClass.ExpectedOverhead: counts.Overhead++; break;
                case RoundValueClass.Wasted: counts.Wasted++; break;
                default: throw new InvalidOperationException($"Unknown round value class: {value.ValueClass}");
            }
            if (value.Outcome == RoundGoalOutcome.Landed) counts.Landed.Add(round.GoalId);
            if (value.Outcome == RoundGoalOutcome.Lost) counts.Lost.Add(round.GoalId);
            var next = round.RoundIndex < dispatches.Length ? dispatches[round.RoundIndex].DispatchedAt : (DateTimeOffset?)null;
            var outcome = goal.Timeline.Where(e => e.TaskId == task.Id &&
                    e.Kind is ProgressKind.TaskNote or ProgressKind.OperatorTaskNote &&
                    e.OccurredAt >= round.DispatchedAt && (next is null || e.OccurredAt < next))
                .OrderBy(e => e.OccurredAt).Select(e => TaskOutcomeClassifier.TryExtractClass(e.Message))
                .LastOrDefault(c => c is not null);
            if (outcome is { } recorded)
            {
                var name = TaskOutcomeClassifier.FormatClass(recorded);
                counts.Outcomes[name] = counts.Outcomes.GetValueOrDefault(name) + 1;
            }
        }
        return new(since, until, rows.OrderBy(r => r.Key == Unattributed).ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new RoundValueCheckRow(r.Key, r.Value.Rounds, r.Value.Productive, r.Value.Overhead,
                r.Value.Wasted, r.Value.Landed.Count, r.Value.Lost.Count,
                r.Value.Outcomes.OrderBy(o => o.Key, StringComparer.Ordinal)
                    .Select(o => new RoundValueCheckOutcomeCount(o.Key, o.Value)).ToArray())).ToArray());
    }

    private static string Attribute(Goal goal, TaskSpec task, WorkerRoundRecord round, DateTimeOffset previous)
    {
        var retry = goal.Timeline.Where(e => e.TaskId == task.Id && e.Kind == ProgressKind.TaskRetried &&
                e.OccurredAt > previous && e.OccurredAt <= round.DispatchedAt)
            .OrderBy(e => e.OccurredAt).LastOrDefault()?.Message;
        if (StructuralKey(retry) is { } structural) return structural;
        var dispatches = task.DispatchHistory.OrderBy(d => d.DispatchedAt).DistinctBy(d => d.DispatchedAt)
            .Select(d => (d.DispatchedAt, (string?)d.Command)).ToArray();
        var prior = FalseFailBridgeDetector.PairVerifications(
            task.VerificationHistory.OrderBy(v => v.CompletedAt).ToArray(), dispatches)[round.RoundIndex - 2];
        if (prior is { Succeeded: false } && !string.IsNullOrWhiteSpace(prior.CompletionVerdictRule))
            return prior.CompletionVerdictRule;
        var receipt = task.RetryAdmissionHistory.Where(r => r.LinkedDispatchAt == round.DispatchedAt)
            .OrderBy(r => r.RecordedAt).LastOrDefault();
        var findings = goal.Tasks.SelectMany(t => t.VerificationHistory)
            .Where(v => v.CompletedAt <= round.DispatchedAt).OrderBy(v => v.CompletedAt)
            .SelectMany(v => v.MergedReviewFindings ?? []).ToArray();
        foreach (var id in receipt?.StableFindingIds ?? [])
        {
            if (findings.FirstOrDefault(f => f.StableId == id) is { } finding)
                return "reviewer:" + FindingCategoryJsonConverter.ToWireValue(finding.Category);
        }
        if (AcceptanceKey(retry) is { } acceptance) return acceptance;
        return round.ReworkCause is ReworkCauseFamily.Unclassified or ReworkCauseFamily.FirstPass
            ? Unattributed : "family:" + round.ReworkCause;
    }

    private static string? StructuralKey(string? message)
    {
        if (message is null || !message.StartsWith(StructuralPrefix, StringComparison.Ordinal)) return null;
        var body = message[StructuralPrefix.Length..];
        foreach (var line in body.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("config/acceptance-manifest.json: ", StringComparison.Ordinal)) return "acceptance-manifest";
            var size = SizeLine.Match(line);
            if (size.Success) return "source-size-ratchet:" + size.Groups["path"].Value;
            var type = ClassLine.Match(line);
            if (type.Success) return "source-class-ratchet:" + type.Groups["name"].Value;
        }
        return null;
    }

    private static string? AcceptanceKey(string? message)
    {
        if (message is null || !message.StartsWith(AcceptancePrefix, StringComparison.Ordinal)) return null;
        var start = message.IndexOf("): ", StringComparison.Ordinal);
        if (start < 0) return null;
        var lines = message[(start + 3)..].Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var summary = Array.IndexOf(lines, "Acceptance criteria summary:");
        var line = lines.Skip(summary + 1).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (line is null || line == "Concrete acceptance failure evidence:") return null;
        var separator = line.IndexOf(": ", StringComparison.Ordinal);
        var name = separator < 0 ? line : line[..separator];
        return string.IsNullOrWhiteSpace(name) ? null : "acceptance:" + name;
    }

    private sealed class Counts
    {
        internal int Rounds, Productive, Overhead, Wasted;
        internal readonly HashSet<string> Landed = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Lost = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> Outcomes = new(StringComparer.Ordinal);
    }
}
