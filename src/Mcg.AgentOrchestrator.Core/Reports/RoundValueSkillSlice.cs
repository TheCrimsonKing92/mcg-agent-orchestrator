namespace Mcg.AgentOrchestrator.Core;

public sealed record RoundValueSkillRow(string Skill, int Selected, int Read, int Claimed,
    int Productive, int Overhead, int Wasted);

/// <summary>Read-only attribution of the terminal goal cohort to observed skill names.</summary>
public sealed record RoundValueSkillSlice(DateTimeOffset Since, DateTimeOffset Until,
    IReadOnlyList<RoundValueSkillRow> Rows, int ReadUnavailable)
{
    public const string NotePrefix = "SKILLS ";

    public static RoundValueSkillSlice Build(IEnumerable<Goal> goals, DateTimeOffset since, DateTimeOffset until,
        IReadOnlyCollection<AppliedRetryIntent>? intents = null)
    {
        if (since >= until) throw new ArgumentException("The round-value window must end after it starts.");
        var goalMap = goals.ToDictionary(g => g.Id.Value, StringComparer.Ordinal);
        var cohort = RoundValueClassifier.Classify(goalMap.Values, intents ?? []).GroupBy(r => r.Round.GoalId)
            .Where(g => g.First().Outcome != RoundGoalOutcome.Pending &&
                g.Max(r => r.Round.DispatchedAt) >= since && g.Max(r => r.Round.DispatchedAt) < until);
        var rows = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        var unavailable = 0;
        foreach (var round in cohort.SelectMany(g => g))
        {
            var goal = goalMap[round.Round.GoalId];
            var task = goal.Tasks.Single(t => t.Id.Value == round.Round.TaskId);
            var next = task.DispatchHistory.Where(d => d.DispatchedAt > round.Round.DispatchedAt)
                .Select(d => (DateTimeOffset?)d.DispatchedAt).Min();
            var note = goal.Timeline.LastOrDefault(e => e.TaskId?.Value == round.Round.TaskId &&
                e.Kind == ProgressKind.TaskNote && e.OccurredAt >= round.Round.DispatchedAt &&
                (next is null || e.OccurredAt < next) && e.Message.StartsWith(NotePrefix, StringComparison.Ordinal));
            if (note is null) { Count("unrecorded", false, false, false, round.ValueClass); continue; }
            var fields = note.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1).Select(s => s.Split('=', 2)).Where(p => p.Length == 2)
                .GroupBy(p => p[0], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last()[1]);
            var selected = Names("selected");
            var read = Names("read");
            var claimed = Names("claimed");
            if (fields.GetValueOrDefault("read") == "unavailable") unavailable++;
            foreach (var skill in selected.Concat(read).Concat(claimed).Distinct(StringComparer.OrdinalIgnoreCase))
                Count(skill, selected.Contains(skill), read.Contains(skill), claimed.Contains(skill), round.ValueClass);

            HashSet<string> Names(string field) => (fields.GetValueOrDefault(field) ?? "none")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !s.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                    !s.Equals("unavailable", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return new(since, until, rows.OrderBy(r => r.Key == "unrecorded").ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new RoundValueSkillRow(r.Key, r.Value[0], r.Value[1], r.Value[2],
                r.Value[3], r.Value[4], r.Value[5])).ToArray(), unavailable);

        void Count(string name, bool selected, bool read, bool claimed, RoundValueClass value)
        {
            if (!rows.TryGetValue(name, out var counts)) rows[name] = counts = new int[6];
            if (selected) counts[0]++;
            if (read) counts[1]++;
            if (claimed) counts[2]++;
            counts[value switch { RoundValueClass.Productive => 3, RoundValueClass.ExpectedOverhead => 4, _ => 5 }]++;
        }
    }
}
