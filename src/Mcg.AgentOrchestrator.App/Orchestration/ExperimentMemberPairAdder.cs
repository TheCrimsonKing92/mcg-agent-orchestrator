using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ExperimentMemberPairAdder
{
    internal const string PairingRule = "lower-brief-sha256-is-comparison";

    internal static IReadOnlyList<ExperimentMemberEvent> Add(ExperimentStore store, string reference,
        string statePath, string baselineReference, string comparisonReference, string reasonPath, DateTimeOffset at)
    {
        ExperimentRecord? record;
        try { record = store.ResolveAsync(reference).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { throw new InvalidOperationException($"rule experiment-state: {error.Message}", error); }
        if (record is null || record.Outcome != ExperimentOutcomeState.Open ||
            record.Spec.Baseline.Kind != ExperimentBaselineKind.GoalCohort || record.Spec.Baseline.IncrementalMembers != true)
            throw new InvalidOperationException("rule experiment-state: requires an open goal-cohort experiment with incrementalMembers true.");

        var goals = ExperimentGoals.Read(statePath);
        Goal Resolve(string value, string arm)
        {
            value = value.Trim();
            var exact = goals.FirstOrDefault(goal => string.Equals(goal.Id.Value, value, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            var matches = goals.Where(goal => value.Length > 0 && goal.Id.Value.StartsWith(value, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length == 1) return matches[0];
            throw new InvalidOperationException($"rule goal-resolution: {arm} goal '{value}' {(matches.Length == 0 ? "not found" : "is ambiguous")}.");
        }
        var baselineGoal = Resolve(baselineReference, "baseline");
        var comparisonGoal = Resolve(comparisonReference, "comparison");
        if (baselineGoal.Id == comparisonGoal.Id)
            throw new InvalidOperationException("rule goal-resolution: baseline and comparison goals must differ.");
        foreach (var (arm, goal) in new[] { ("baseline", baselineGoal), ("comparison", comparisonGoal) })
            if (goal.Tasks.Any(task => task.DispatchHistory.Count > 0))
                throw new InvalidOperationException($"rule not-dispatched: {arm} goal '{goal.Id.Value}' has dispatch history.");

        foreach (var open in store.ListOpenAsync().GetAwaiter().GetResult())
        {
            var spec = open.Spec.Baseline;
            var references = (spec.BaselineGoalIds ?? []).Concat(spec.ComparisonGoalIds ?? []);
            var events = store.ListMemberEventsAsync(open.Id).GetAwaiter().GetResult();
            foreach (var goal in new[] { baselineGoal, comparisonGoal })
                if (references.Any(id => !string.IsNullOrWhiteSpace(id) && goal.Id.Value.StartsWith(id.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                    events.Any(member => string.Equals(member.GoalId, goal.Id.Value, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"rule single-membership: goal '{goal.Id.Value}' already belongs to open experiment '{open.Id}'.");
        }
        var baselineDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(baselineGoal.Objective)));
        var comparisonDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(comparisonGoal.Objective)));
        if (record.Spec.Baseline.PairingRule is { } rule &&
            (rule != PairingRule || string.CompareOrdinal(comparisonDigest, baselineDigest) >= 0))
            throw new InvalidOperationException($"rule pairing: {rule} requires lower comparison digest; baseline {baselineDigest}, comparison {comparisonDigest}.");
        string reason;
        try { reason = File.ReadAllText(reasonPath).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new ArgumentException($"rule reason: cannot read '{reasonPath}': {error.Message}", error); }
        if (reason.Length == 0) throw new ArgumentException("rule reason: file must contain non-blank text.");
        return store.AddMemberPairAsync(record.Id, baselineGoal.Id.Value, baselineDigest,
            comparisonGoal.Id.Value, comparisonDigest, reason, at).GetAwaiter().GetResult();
    }
}
