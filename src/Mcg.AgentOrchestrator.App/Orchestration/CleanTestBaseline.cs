using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum CleanBaselineAttestation
{
    AttestedGreen,
    AttestedRed,
    ObservedGreenCandidatePass,
    ObservedRedCorrelation,
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
    IReadOnlyList<string>? FailedCheckNames,
    string? BranchHeadSha = null);

internal static class CleanTestBaseline
{
    private const int MaxNamedCorrelatedChecks = 3;

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
            var correlatedEvidence = failed
                .Where(item => (item.FailedCheckNames ?? [])
                    .Any(check => sharedChecks.Contains(check.Trim(), StringComparer.Ordinal)))
                .ToArray();
            var knownLineages = correlatedEvidence
                .Select(item => NormalizeSha(item.BranchHeadSha))
                .Where(lineage => lineage is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var lineageDetail = knownLineages.Length switch
            {
                0 => "candidate runs without lineage identity",
                1 when correlatedEvidence.All(item => !string.IsNullOrWhiteSpace(item.BranchHeadSha)) =>
                    "the same candidate lineage",
                _ when correlatedEvidence.All(item => !string.IsNullOrWhiteSpace(item.BranchHeadSha)) =>
                    "distinct candidate lineages",
                _ => "candidate runs with partial lineage identity"
            };
            return new CleanTestBaselineReceipt(
                normalizedMain,
                NormalizeSha(mergeBaseSha),
                CleanBaselineAttestation.ObservedRedCorrelation,
                source.GoalId.Value,
                source.At,
                sharedChecks,
                $"observed {sharedChecks.Length} shared check label(s) across {sourceGoalCount} goals from {lineageDetail}; candidate journals do not record an executed baseline, matching failure signature, runtime, policy, or selection");
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
                CleanBaselineAttestation.ObservedGreenCandidatePass,
                green.GoalId.Value,
                green.At,
                [],
                $"observed candidate acceptance pass for goal {Short(green.GoalId.Value)}; a candidate pass does not attest the baseline at main {Short(normalizedMain)}");
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
                var originEvidence = ResolveProvenOriginEvidence(check, failedCheckReceipts);
                if (originEvidence is not null)
                {
                    return new AcceptanceCheckAttribution(
                        check,
                        originEvidence.Origin,
                        CombineEvidence(originEvidence.Evidence, causeEvidence),
                        causeEvidence?.Cause ?? AcceptanceFailureCause.NotClassified);
                }

                if (receipt.SharedFailingChecks.Contains(check, StringComparer.Ordinal))
                {
                    var correlatedWith = evidence
                        .Where(item => item.GoalId != currentGoal)
                        .Where(item =>
                            ShaEquals(item.MainHeadSha, normalizedMain) &&
                            item.AcceptanceOutcome?.Equals("failed", StringComparison.OrdinalIgnoreCase) == true &&
                            (item.FailedCheckNames ?? []).Contains(check, StringComparer.Ordinal))
                        .OrderByDescending(item => item.At)
                        .FirstOrDefault();
                    var observedCorrelation = correlatedWith is null
                        ? receipt.Evidence
                        : $"observed matching candidate check label for goal {Short(correlatedWith.GoalId.Value)} at main {Short(normalizedMain)}; this correlation does not prove failure origin";
                    return new AcceptanceCheckAttribution(
                        check,
                        AcceptanceFailureOrigin.Unattributed,
                        CombineEvidence(observedCorrelation, causeEvidence),
                        causeEvidence?.Cause ?? AcceptanceFailureCause.NotClassified);
                }

                return new AcceptanceCheckAttribution(
                    check,
                    AcceptanceFailureOrigin.Unattributed,
                    CombineEvidence(
                        receipt.Attestation == CleanBaselineAttestation.ObservedGreenCandidatePass
                            ? $"observed candidate pass does not attest baseline health at main {Short(normalizedMain)}"
                            : $"no exact focused baseline attribution for check {check} at main {Short(normalizedMain)}",
                        causeEvidence),
                    causeEvidence?.Cause ?? AcceptanceFailureCause.NotClassified);
            })
            .ToArray();
    }

    // The only authoritative baseline producer is the executed merge-base focused arm:
    // GoalAcceptanceVerifier runs it and AcceptanceFailureAttributionPlanner.ClassifyBaselineFailures
    // records per-identity origins on AcceptanceCheckResult.FailingTestAttributions. Candidate-journal
    // check-name correlation never reaches this path, so it can never produce an attested verdict.
    public static CleanTestBaselineReceipt WithExecutedBaselineAttestation(
        CleanTestBaselineReceipt receipt,
        IReadOnlyList<string> failedChecks,
        GoalId currentGoal,
        IReadOnlyList<AcceptanceCheckResult>? failedCheckReceipts)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(failedChecks);
        var proven = failedChecks
            .Select(check => check.Trim())
            .Where(check => check.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(check => (Check: check, Origin: ResolveProvenOriginEvidence(check, failedCheckReceipts)))
            .ToArray();
        if (proven.Length == 0)
        {
            return receipt;
        }

        var inherited = proven
            .Where(item => item.Origin?.Origin == AcceptanceFailureOrigin.Inherited)
            .ToArray();
        if (inherited.Length > 0)
        {
            return Attested(
                receipt,
                currentGoal,
                CleanBaselineAttestation.AttestedRed,
                $"executed merge-base baseline arm reproduced {inherited.Length} failing check(s) " +
                $"({FormatCheckScope(inherited)}) at main {Short(receipt.MainSha)}; proven only for that " +
                $"executed focused selection; producer evidence: {FormatProducerEvidence(inherited)}");
        }

        // A green verdict needs the executed arm to cover every failing check; partial coverage stays observational.
        return proven.All(item => item.Origin?.Origin == AcceptanceFailureOrigin.Introduced)
            ? Attested(
                receipt,
                currentGoal,
                CleanBaselineAttestation.AttestedGreen,
                $"executed merge-base baseline arm was green for all {proven.Length} failing check(s) " +
                $"({FormatCheckScope(proven)}); this attests only that executed focused selection, not the " +
                $"whole baseline at main {Short(receipt.MainSha)}; producer evidence: {FormatProducerEvidence(proven)}")
            : receipt;
    }

    private static CleanTestBaselineReceipt Attested(
        CleanTestBaselineReceipt receipt,
        GoalId currentGoal,
        CleanBaselineAttestation attestation,
        string evidence) =>
        receipt with
        {
            Attestation = attestation,
            SourceGoalId = currentGoal.Value,
            SourceAt = null,
            Evidence = receipt.SharedFailingChecks.Count == 0
                ? evidence
                : $"{evidence}; retained observation: {receipt.Evidence}"
        };

    private static string FormatCheckScope(
        IReadOnlyList<(string Check, ProvenOriginEvidence? Origin)> scope) =>
        string.Join(", ", scope.Select(item => item.Check));

    private static string FormatProducerEvidence(
        IReadOnlyList<(string Check, ProvenOriginEvidence? Origin)> scope) =>
        string.Join(" | ", scope
            .Select(item => item.Origin!.Evidence.Trim())
            .Distinct(StringComparer.Ordinal));

    private static ProvenOriginEvidence? ResolveProvenOriginEvidence(
        string checkName,
        IReadOnlyList<AcceptanceCheckResult>? failedCheckReceipts)
    {
        var matchingChecks = failedCheckReceipts?
            .Where(check => !check.Passed && check.Name.Equals(checkName, StringComparison.Ordinal))
            .ToArray() ?? [];
        var identities = matchingChecks
            .SelectMany(check => check.FailingTestIdentities ?? [])
            .Select(identity => identity.Trim())
            .Where(identity => identity.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (identities.Length == 0)
        {
            return null;
        }

        var attributions = matchingChecks
            .SelectMany(check => check.FailingTestAttributions ?? [])
            .GroupBy(attribution => attribution.TestIdentity.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (identities.Any(identity =>
                !attributions.TryGetValue(identity, out var values) ||
                values.Length != 1 ||
                values[0].Origin == AcceptanceTestFailureOrigin.Unattributed))
        {
            return null;
        }

        var selected = identities.Select(identity => attributions[identity][0]).ToArray();
        var origins = selected.Select(attribution => attribution.Origin).Distinct().ToArray();
        if (origins.Length != 1)
        {
            return null;
        }

        var origin = origins[0] switch
        {
            AcceptanceTestFailureOrigin.Inherited => AcceptanceFailureOrigin.Inherited,
            AcceptanceTestFailureOrigin.Introduced => AcceptanceFailureOrigin.Introduced,
            _ => AcceptanceFailureOrigin.Unattributed
        };
        return origin == AcceptanceFailureOrigin.Unattributed
            ? null
            : new ProvenOriginEvidence(
                origin,
                string.Join(" | ", selected
                    .Select(attribution => attribution.Evidence.Trim())
                    .Distinct(StringComparer.Ordinal)));
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

    // Only the executed merge-base arm attests a red baseline. Candidate-journal check-name agreement is a
    // correlation, so the routing subject names the correlated check labels instead of asserting main is red.
    // The caller supplies the already-formatted main sha so sha display policy stays with its owner.
    public static string FormatAttentionSubject(CleanTestBaselineReceipt receipt, string displayMainSha)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return receipt.Attestation == CleanBaselineAttestation.AttestedRed
            ? $"Attested red clean-test baseline at {displayMainSha} from executed merge-base evidence"
            : $"Observed clean-test failure correlation at {displayMainSha}" +
              FormatCorrelatedCheckSuffix(receipt.SharedFailingChecks);
    }

    private static string FormatCorrelatedCheckSuffix(IReadOnlyList<string> sharedChecks)
    {
        var named = sharedChecks
            .Select(check => check.Trim())
            .Where(check => check.Length > 0)
            .ToArray();
        if (named.Length == 0)
        {
            return string.Empty;
        }

        return named.Length > MaxNamedCorrelatedChecks
            ? $" for {string.Join(", ", named.Take(MaxNamedCorrelatedChecks))} (+{named.Length - MaxNamedCorrelatedChecks} more)"
            : $" for {string.Join(", ", named)}";
    }

    public static string FormatFailureAttestation(CleanTestBaselineReceipt receipt) =>
        $"{ToWireValue(receipt.Attestation)}; {receipt.Evidence}";

    private static string ToWireValue(CleanBaselineAttestation attestation) =>
        attestation switch
        {
            CleanBaselineAttestation.AttestedGreen => "attested-green",
            CleanBaselineAttestation.AttestedRed => "attested-red",
            CleanBaselineAttestation.ObservedGreenCandidatePass => "observed-green-candidate-pass",
            CleanBaselineAttestation.ObservedRedCorrelation => "observed-red-correlation",
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
                entry.FailedCheckNames,
                entry.BranchHeadSha)))
            .ToArray();

    private sealed record FailedCheckEvidence(GoalId GoalId, DateTimeOffset At, string CheckName);

    private sealed record ProvenOriginEvidence(AcceptanceFailureOrigin Origin, string Evidence);
}
