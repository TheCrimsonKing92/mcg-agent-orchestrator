using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum CleanBaselineAttestation
{
    AttestedGreen,
    AttestedRed,
    Unattested
}

internal sealed record CleanTestBaselineReceipt(
    string MainSha,
    string? MergeBaseSha,
    CleanBaselineAttestation Attestation,
    string? SourceGoalId,
    DateTimeOffset? SourceAt,
    IReadOnlyList<string> SharedFailingChecks,
    string Evidence);

internal static class CleanTestBaseline
{
    public static CleanTestBaselineReceipt Resolve(
        IReadOnlyDictionary<GoalId, GoalOperationJournalSummary> journals,
        GoalId currentGoal,
        string mainSha,
        string? mergeBaseSha)
    {
        ArgumentNullException.ThrowIfNull(journals);
        _ = currentGoal;
        var normalizedMain = NormalizeSha(mainSha) ?? string.Empty;
        var matching = journals
            .SelectMany(pair => pair.Value.Entries.Select(entry => new JournalEvidence(pair.Key, entry)))
            .Where(item => ShaEquals(item.Entry.MainHeadSha, normalizedMain))
            .ToArray();

        var failed = matching
            .Where(item => item.Entry.AcceptanceOutcome?.Equals("failed", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        var sharedChecks = failed
            .SelectMany(item => (item.Entry.FailedCheckNames ?? [])
                .Select(check => check.Trim())
                .Where(check => check.Length > 0)
                .Select(check => new FailedCheckEvidence(item.GoalId, item.Entry.At, check)))
            .GroupBy(item => item.CheckName, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.GoalId).Distinct().Count() >= 2)
            .Select(group => group.Key)
            .OrderBy(check => check, StringComparer.Ordinal)
            .ToArray();
        if (sharedChecks.Length > 0)
        {
            var source = failed
                .Where(item => (item.Entry.FailedCheckNames ?? [])
                    .Any(check => sharedChecks.Contains(check.Trim(), StringComparer.Ordinal)))
                .OrderByDescending(item => item.Entry.At)
                .First();
            var sourceGoalCount = failed
                .Where(item => (item.Entry.FailedCheckNames ?? [])
                    .Any(check => sharedChecks.Contains(check.Trim(), StringComparer.Ordinal)))
                .Select(item => item.GoalId)
                .Distinct()
                .Count();
            return new CleanTestBaselineReceipt(
                normalizedMain,
                NormalizeSha(mergeBaseSha),
                CleanBaselineAttestation.AttestedRed,
                source.GoalId.Value,
                source.Entry.At,
                sharedChecks,
                $"{sharedChecks.Length} identical check(s) failed across {sourceGoalCount} goals");
        }

        var green = matching
            .Where(item => item.Entry.AcceptanceOutcome?.Equals("passed", StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(item => item.Entry.At)
            .FirstOrDefault();
        if (green is not null)
        {
            return new CleanTestBaselineReceipt(
                normalizedMain,
                NormalizeSha(mergeBaseSha),
                CleanBaselineAttestation.AttestedGreen,
                green.GoalId.Value,
                green.Entry.At,
                [],
                $"integrated acceptance passed for goal {Short(green.GoalId.Value)}");
        }

        return Unattested(normalizedMain, mergeBaseSha);
    }

    public static IReadOnlyList<AcceptanceCheckAttribution> Attribute(
        CleanTestBaselineReceipt receipt,
        IReadOnlyList<string> failedChecks,
        IReadOnlyDictionary<GoalId, GoalOperationJournalSummary> journals,
        GoalId currentGoal,
        string mainSha)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(failedChecks);
        ArgumentNullException.ThrowIfNull(journals);
        var normalizedMain = NormalizeSha(mainSha) ?? receipt.MainSha;

        return failedChecks
            .Select(check => check.Trim())
            .Where(check => check.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(check =>
            {
                if (receipt.SharedFailingChecks.Contains(check, StringComparer.Ordinal))
                {
                    var inheritedFrom = journals
                        .Where(pair => pair.Key != currentGoal)
                        .SelectMany(pair => pair.Value.Entries.Select(entry => new JournalEvidence(pair.Key, entry)))
                        .Where(item =>
                            ShaEquals(item.Entry.MainHeadSha, normalizedMain) &&
                            item.Entry.AcceptanceOutcome?.Equals("failed", StringComparison.OrdinalIgnoreCase) == true &&
                            (item.Entry.FailedCheckNames ?? []).Contains(check, StringComparer.Ordinal))
                        .OrderByDescending(item => item.Entry.At)
                        .FirstOrDefault();
                    if (inheritedFrom is not null)
                    {
                        return new AcceptanceCheckAttribution(
                            check,
                            AcceptanceFailureOrigin.Inherited,
                            $"also failed for goal {Short(inheritedFrom.GoalId.Value)} at main {Short(normalizedMain)}");
                    }
                }

                return receipt.Attestation == CleanBaselineAttestation.AttestedGreen
                    ? new AcceptanceCheckAttribution(
                        check,
                        AcceptanceFailureOrigin.Introduced,
                        $"main {Short(normalizedMain)} is attested green")
                    : new AcceptanceCheckAttribution(
                        check,
                        AcceptanceFailureOrigin.Unattributed,
                        $"no baseline evidence at main {Short(normalizedMain)}");
            })
            .ToArray();
    }

    public static CleanTestBaselineReceipt Unattested(string? mainSha, string? mergeBaseSha = null) =>
        new(
            NormalizeSha(mainSha) ?? string.Empty,
            NormalizeSha(mergeBaseSha),
            CleanBaselineAttestation.Unattested,
            null,
            null,
            [],
            "no qualifying cross-goal acceptance receipt");

    public static string FormatJournalDetail(CleanTestBaselineReceipt receipt) =>
        $"main_sha={receipt.MainSha} attestation={ToWireValue(receipt.Attestation)} " +
        $"source_goal={receipt.SourceGoalId ?? "none"} shared_checks={receipt.SharedFailingChecks.Count} evidence={receipt.Evidence}";

    public static string FormatFailureAttestation(CleanTestBaselineReceipt receipt) =>
        $"{ToWireValue(receipt.Attestation)}; {receipt.Evidence}";

    private static string ToWireValue(CleanBaselineAttestation attestation) =>
        attestation switch
        {
            CleanBaselineAttestation.AttestedGreen => "attested-green",
            CleanBaselineAttestation.AttestedRed => "attested-red",
            _ => "unattested"
        };

    private static bool ShaEquals(string? left, string? right) =>
        string.Equals(NormalizeSha(left), NormalizeSha(right), StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeSha(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Short(string value)
    {
        var normalized = value.Trim();
        return normalized[..Math.Min(8, normalized.Length)];
    }

    private sealed record JournalEvidence(GoalId GoalId, GoalOperationJournalEntry Entry);

    private sealed record FailedCheckEvidence(GoalId GoalId, DateTimeOffset At, string CheckName);
}
