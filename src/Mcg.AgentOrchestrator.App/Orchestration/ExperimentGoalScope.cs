using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A conservative set of goal inputs needed by effective experiment records.
internal sealed class ExperimentGoalScope
{
    private readonly bool _all;
    private readonly string[] _references;

    private ExperimentGoalScope(bool all, string[] references, DateTimeOffset? activeSince)
    {
        _all = all;
        _references = references;
        ActiveSince = activeSince;
    }

    internal static ExperimentGoalScope All { get; } = new(true, [], null);
    internal static ExperimentGoalScope Empty { get; } = new(false, [], null);
    internal DateTimeOffset? ActiveSince { get; }
    internal bool IsEmpty => !_all && _references.Length == 0 && ActiveSince is null;

    internal static ExperimentGoalScope For(IReadOnlyCollection<ExperimentRecord> effectiveRecords)
    {
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? activeSince = null;
        foreach (var record in effectiveRecords)
        {
            var baseline = record.Spec.Baseline;
            AddReference(baseline.TwinGoalId);
            AddReference(baseline.ComparisonGoalId);
            foreach (var reference in baseline.BaselineGoalIds ?? []) AddReference(reference);
            foreach (var reference in baseline.ComparisonGoalIds ?? []) AddReference(reference);

            var incremental = record.Spec.StopRule.Unit == ExperimentStopUnit.Goals &&
                baseline.Kind == ExperimentBaselineKind.GoalCohort && baseline.IncrementalMembers == true;
            if (baseline.Kind != ExperimentBaselineKind.BeforeAfterWindow &&
                (record.Spec.StopRule.Unit != ExperimentStopUnit.Goals || incremental)) continue;

            var comparisonStart = ExperimentReading.ComparisonStart(record);
            var since = baseline.Since ?? comparisonStart;
            var bound = since < comparisonStart ? since : comparisonStart;
            if (activeSince is null || bound < activeSince.Value) activeSince = bound;
        }
        return new(false, references.ToArray(), activeSince);

        void AddReference(string? reference)
        {
            if (!string.IsNullOrWhiteSpace(reference)) references.Add(reference.Trim());
        }
    }

    internal bool Includes(string goalId, string updatedAt)
    {
        if (_all || _references.Any(reference => goalId.StartsWith(reference, StringComparison.OrdinalIgnoreCase)))
            return true;
        return ActiveSince is { } since &&
            (!DateTimeOffset.TryParse(updatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated) ||
                updated >= since.AddHours(-24));
    }
}
