using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

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

internal sealed record CleanTestBaselineEvidence(
    GoalId GoalId,
    DateTimeOffset At,
    string? MainHeadSha,
    string? AcceptanceOutcome,
    IReadOnlyList<string>? FailedCheckNames);

internal static class CleanTestBaseline
{
    public static CleanTestBaselineReceipt Resolve(
        IReadOnlyDictionary<GoalId, GoalOperationJournalSummary> journals,
        GoalId currentGoal,
        string mainSha,
        string? mergeBaseSha)
    {
        ArgumentNullException.ThrowIfNull(journals);
        return Resolve(ProjectEvidence(journals), currentGoal, mainSha, mergeBaseSha);
    }

    public static CleanTestBaselineReceipt Resolve(
        IReadOnlyList<CleanTestBaselineEvidence> evidence,
        GoalId currentGoal,
        string mainSha,
        string? mergeBaseSha)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        _ = currentGoal;
        var normalizedMain = NormalizeSha(mainSha) ?? string.Empty;
        var matching = evidence
            .Where(item => ShaEquals(item.MainHeadSha, normalizedMain))
            .ToArray();

        var failed = matching
            .Where(item => item.AcceptanceOutcome?.Equals("failed", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        var sharedChecks = failed
            .SelectMany(item => (item.FailedCheckNames ?? [])
                .Select(check => check.Trim())
                .Where(check => check.Length > 0)
                .Select(check => new FailedCheckEvidence(item.GoalId, item.At, check)))
            .GroupBy(item => item.CheckName, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.GoalId).Distinct().Count() >= 2)
            .Select(group => group.Key)
            .OrderBy(check => check, StringComparer.Ordinal)
            .ToArray();
        if (sharedChecks.Length > 0)
        {
            var source = failed
                .Where(item => (item.FailedCheckNames ?? [])
                    .Any(check => sharedChecks.Contains(check.Trim(), StringComparer.Ordinal)))
                .OrderByDescending(item => item.At)
                .First();
            var sourceGoalCount = failed
                .Where(item => (item.FailedCheckNames ?? [])
                    .Any(check => sharedChecks.Contains(check.Trim(), StringComparer.Ordinal)))
                .Select(item => item.GoalId)
                .Distinct()
                .Count();
            return new CleanTestBaselineReceipt(
                normalizedMain,
                NormalizeSha(mergeBaseSha),
                CleanBaselineAttestation.AttestedRed,
                source.GoalId.Value,
                source.At,
                sharedChecks,
                $"{sharedChecks.Length} identical check(s) failed across {sourceGoalCount} goals");
        }

        var green = matching
            .Where(item => item.AcceptanceOutcome?.Equals("passed", StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(item => item.At)
            .FirstOrDefault();
        if (green is not null)
        {
            return new CleanTestBaselineReceipt(
                normalizedMain,
                NormalizeSha(mergeBaseSha),
                CleanBaselineAttestation.AttestedGreen,
                green.GoalId.Value,
                green.At,
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
        string mainSha,
        IReadOnlyList<AcceptanceCheckResult>? failedCheckReceipts = null)
    {
        ArgumentNullException.ThrowIfNull(journals);
        return Attribute(
            receipt,
            failedChecks,
            ProjectEvidence(journals),
            currentGoal,
            mainSha,
            failedCheckReceipts);
    }

    public static IReadOnlyList<AcceptanceCheckAttribution> Attribute(
        CleanTestBaselineReceipt receipt,
        IReadOnlyList<string> failedChecks,
        IReadOnlyList<CleanTestBaselineEvidence> evidence,
        GoalId currentGoal,
        string mainSha,
        IReadOnlyList<AcceptanceCheckResult>? failedCheckReceipts = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(failedChecks);
        ArgumentNullException.ThrowIfNull(evidence);
        var normalizedMain = NormalizeSha(mainSha) ?? receipt.MainSha;

        return failedChecks
            .Select(check => check.Trim())
            .Where(check => check.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(check =>
            {
                var causeEvidence = ResolveCauseEvidence(check, failedCheckReceipts);
                if (receipt.SharedFailingChecks.Contains(check, StringComparer.Ordinal))
                {
                    var inheritedFrom = evidence
                        .Where(item => item.GoalId != currentGoal)
                        .Where(item =>
                            ShaEquals(item.MainHeadSha, normalizedMain) &&
                            item.AcceptanceOutcome?.Equals("failed", StringComparison.OrdinalIgnoreCase) == true &&
                            (item.FailedCheckNames ?? []).Contains(check, StringComparer.Ordinal))
                        .OrderByDescending(item => item.At)
                        .FirstOrDefault();
                    if (inheritedFrom is not null)
                    {
                        return new AcceptanceCheckAttribution(
                            check,
                            AcceptanceFailureOrigin.Inherited,
                            CombineEvidence(
                                $"also failed for goal {Short(inheritedFrom.GoalId.Value)} at main {Short(normalizedMain)}",
                                causeEvidence),
                            causeEvidence?.Cause ?? AcceptanceFailureCause.NotClassified);
                    }
                }

                return receipt.Attestation == CleanBaselineAttestation.AttestedGreen
                    ? new AcceptanceCheckAttribution(
                        check,
                        AcceptanceFailureOrigin.Introduced,
                        CombineEvidence($"main {Short(normalizedMain)} is attested green", causeEvidence),
                        causeEvidence?.Cause ?? AcceptanceFailureCause.NotClassified)
                    : new AcceptanceCheckAttribution(
                        check,
                        AcceptanceFailureOrigin.Unattributed,
                        CombineEvidence($"no baseline evidence at main {Short(normalizedMain)}", causeEvidence),
                        causeEvidence?.Cause ?? AcceptanceFailureCause.NotClassified);
            })
            .ToArray();
    }

    private static AcceptanceFailureCauseEvidence? ResolveCauseEvidence(
        string checkName,
        IReadOnlyList<AcceptanceCheckResult>? failedCheckReceipts)
    {
        var matching = failedCheckReceipts?
            .Where(check => check.Name.Equals(checkName, StringComparison.Ordinal))
            .ToArray() ?? [];
        if (matching.Length == 0 || matching.Any(check =>
                check.FailureCauseEvidence is null ||
                !Enum.IsDefined(check.FailureCauseEvidence.Cause) ||
                check.FailureCauseEvidence.Cause == AcceptanceFailureCause.NotClassified ||
                string.IsNullOrWhiteSpace(check.FailureCauseEvidence.Evidence)))
        {
            return null;
        }

        var causes = matching
            .Select(check => check.FailureCauseEvidence!.Cause)
            .Distinct()
            .ToArray();
        if (causes.Length != 1)
        {
            return null;
        }

        var evidence = string.Join(
            " | ",
            matching
                .Select(check => check.FailureCauseEvidence!.Evidence.Trim())
                .Distinct(StringComparer.Ordinal));
        return new AcceptanceFailureCauseEvidence(causes[0], evidence);
    }

    private static string CombineEvidence(
        string attributionEvidence,
        AcceptanceFailureCauseEvidence? causeEvidence) =>
        causeEvidence is null
            ? attributionEvidence
            : $"{attributionEvidence}; cause receipt: {causeEvidence.Evidence}";

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

    private static CleanTestBaselineEvidence[] ProjectEvidence(
        IReadOnlyDictionary<GoalId, GoalOperationJournalSummary> journals) =>
        journals
            .SelectMany(pair => pair.Value.Entries.Select(entry => new CleanTestBaselineEvidence(
                pair.Key,
                entry.At,
                entry.MainHeadSha,
                entry.AcceptanceOutcome,
                entry.FailedCheckNames)))
            .ToArray();

    private sealed record FailedCheckEvidence(GoalId GoalId, DateTimeOffset At, string CheckName);
}
