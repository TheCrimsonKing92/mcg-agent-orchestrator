using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>One non-passing check observed in the frozen round's focused evidence.</summary>
public sealed record FailedGoalRoundCheck(string Name, string? Classification);

public sealed record FailedGoalTimedOutSelectionFacts(
    ImmutableArray<FailedGoalRoundCheck> NonPassingChecks, ImmutableArray<string> CompletedRerunKeys);

public sealed record FailedGoalTimedOutSelectionRerun(
    string CandidateIdentity, ImmutableArray<string> Selections, string Key);

public static class FailedGoalTimedOutSelectionRerunRule
{
    public const string ReasonSlug = "verification-inconclusive-timed-out-selection-rerun";
    public const string TimedOutClassification = "timed-out";
    private const string TimeoutPrefix = "acceptance-check-timeout: ";

    public static FailedGoalRecoveryDecision? TryDecide(
        FailedGoalRecoveryFacts facts, FailedGoalRecoveryTaskFacts task, FailedGoalInconclusiveRoundPair rounds)
    {
        if (!rounds.InputsUnchanged || task.TimedOutSelections is not { } observed ||
            observed.NonPassingChecks.IsDefaultOrEmpty) return null;
        var selections = new List<string>();
        foreach (var check in observed.NonPassingChecks)
        {
            if (check.Classification != TimedOutClassification || !TryParseSelection(check.Name, out var selection))
                return null;
            selections.Add(selection);
        }
        var sorted = selections.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        var key = BuildKey(rounds.Current.CandidateIdentity, sorted);
        if (!observed.CompletedRerunKeys.IsDefault && observed.CompletedRerunKeys.Contains(key, StringComparer.Ordinal))
            return null;
        return new(FailedGoalRecoveryAction.RerunTimedOutSelections,
            new(facts.GoalId, task.TaskId, task.AttemptIdentity, facts.ContextVersion), 3, ReasonSlug,
            $"{ReasonSlug}: rerun {string.Join(", ", sorted)} once for {rounds.Current.CandidateIdentity}.",
            TimedOutRerun: new(rounds.Current.CandidateIdentity, sorted, key));
    }

    public static bool TryParseSelection(string name, out string selection)
    {
        selection = string.Empty;
        if (!name.StartsWith(TimeoutPrefix, StringComparison.Ordinal)) return false;
        var end = name.LastIndexOf(" elapsed=", StringComparison.Ordinal);
        if (end <= TimeoutPrefix.Length || !name[(end + 1)..].Contains(" budget=", StringComparison.Ordinal))
            return false;
        selection = name[TimeoutPrefix.Length..end].Trim();
        return selection.Length > 0;
    }

    public static string BuildKey(string candidate, IEnumerable<string> selections) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            candidate,
            selections = selections.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        })))).ToLowerInvariant();

    public static ImmutableArray<string> ReadRecordedKeys(Goal goal) => goal.Timeline
        .Where(evt => (evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.StartsWith(ReasonSlug + "; phase=request; key=", StringComparison.Ordinal)) ||
            (evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.StartsWith(ReasonSlug + "; phase=receipt; key=", StringComparison.Ordinal)))
        .Select(evt => evt.Message.Split(';', StringSplitOptions.TrimEntries)
            .Single(field => field.StartsWith("key=", StringComparison.Ordinal))[4..])
        .Distinct(StringComparer.Ordinal).ToImmutableArray();
}
