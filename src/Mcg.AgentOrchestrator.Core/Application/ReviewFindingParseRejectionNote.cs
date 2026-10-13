namespace Mcg.AgentOrchestrator.Core;

internal static class ReviewFindingParseRejectionNote
{
    private const int FragmentLimit = 480;

    internal static string BuildAdvisoryWarning(AgentRole role, ReviewFindingParseDiagnostic diagnostic) =>
        $"Dropped malformed {role} advisory finding: item_index={diagnostic.ItemIndex}; " +
        $"stable_id={BoundFragment(diagnostic.StableId)}; severity=advisory; diagnostic={Bound(diagnostic.Reason)}.";

    internal static string Build(
        AgentRole role,
        TaskVerificationRecord verification,
        string diagnostic)
    {
        WorkerResultBlockers.TryReadReviewFindingFragments(
            verification,
            out var findingsJson,
            out var touchedAnchorsJson);

        return $"Rejected {role} structured finding result: diagnostic={Bound(diagnostic)}; " +
            $"findings_fragment={BoundFragment(findingsJson)}; " +
            $"touched_anchors_fragment={BoundFragment(touchedAnchorsJson)}.";
    }

    private static string BoundFragment(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<missing>" : Bound(value);

    private static string Bound(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= FragmentLimit
            ? normalized
            : normalized[..FragmentLimit] + "...";
    }
}
