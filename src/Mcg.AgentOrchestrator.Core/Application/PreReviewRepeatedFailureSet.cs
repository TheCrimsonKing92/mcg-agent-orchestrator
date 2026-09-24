using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.Core;

public sealed record PreReviewRepeatedFailureSummary(int ConsecutiveRounds, ImmutableArray<string> RepeatedTests)
{
    public bool HasRepeat => ConsecutiveRounds >= 2;
    public bool HoldRequired => ConsecutiveRounds >= PreReviewRepeatedFailureSet.HoldRoundThreshold;
}

public sealed record PreReviewRepeatedFailureHold(ImmutableArray<string> RepeatedTests, int RoundCount)
{
    public string Reason =>
        $"PRE_REVIEW_REPEATED_FAILING_SET: rounds={RoundCount}; tests={string.Join(", ", RepeatedTests)}";
}

public static class PreReviewRepeatedFailureSet
{
    public const int HoldRoundThreshold = 3;

    public static PreReviewRepeatedFailureSummary Evaluate(IReadOnlyList<PreReviewEvidenceReceipt> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0 || history[^1] is not { Disposition: PreReviewEvidenceDisposition.Red } newest ||
            newest.FailingTestIdentities.Count == 0)
            return new(0, []);

        var tests = newest.FailingTestIdentities.Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal).ToImmutableArray();
        var count = 1;
        var lastSha = newest.CandidateSha;
        for (var index = history.Count - 2; index >= 0; index--)
        {
            var previous = history[index];
            if (previous.Disposition is PreReviewEvidenceDisposition.MappingNeedsInput or
                PreReviewEvidenceDisposition.NoApplicableTests)
                continue;
            if (previous.Disposition != PreReviewEvidenceDisposition.Red)
                break;
            if (string.Equals(previous.CandidateSha, lastSha, StringComparison.OrdinalIgnoreCase))
                break;
            if (previous.FailingTestIdentities.Count != tests.Length ||
                !previous.FailingTestIdentities.ToHashSet(StringComparer.Ordinal).SetEquals(tests))
                break;
            count++;
            lastSha = previous.CandidateSha;
        }

        // A retry with operator guidance after a hold begins a fresh series.
        return new(((count - 1) % HoldRoundThreshold) + 1, tests);
    }
}
