using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceFailureAttributionPlanner
{
    internal sealed record CandidateSelectionPlan(
        bool Succeeded,
        IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck> Checks,
        FocusedEvidenceCoverage Coverage,
        IReadOnlyDictionary<string, string> CheckNamesBySelector,
        string? FailureEvidence = null);

    internal sealed record BaselineSourceSelectionPlan(
        IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck> ExecutableChecks,
        IReadOnlyList<AcceptanceCheckResult> SourceClassificationChecks);

    internal sealed record BoundedIdentitySelection(
        IReadOnlyList<string> All,
        IReadOnlyList<string> Selected,
        IReadOnlyList<string> Omitted);

    internal sealed class FocusedInvocationBudget(int cap)
    {
        private int _remaining = cap;

        internal BoundedIdentitySelection Select(IEnumerable<string> identities)
        {
            var ordered = identities
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var selectedSelectors = ordered
                .Select(NormalizeIdentity)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(_remaining)
                .ToHashSet(StringComparer.Ordinal);
            return new BoundedIdentitySelection(
                ordered,
                ordered.Where(identity => selectedSelectors.Contains(NormalizeIdentity(identity))).ToArray(),
                ordered.Where(identity => !selectedSelectors.Contains(NormalizeIdentity(identity))).ToArray());
        }

        internal void Consume(BoundedIdentitySelection selection)
        {
            var consumed = selection.Selected
                .Select(NormalizeIdentity)
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (consumed > _remaining)
            {
                throw new InvalidOperationException("Focused attribution selection exceeded its gate-wide budget.");
            }

            _remaining -= consumed;
        }
    }

    internal static BoundedIdentitySelection SelectBoundedIdentities(
        IEnumerable<string> identities,
        int cap)
    {
        var ordered = identities
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new BoundedIdentitySelection(ordered, ordered.Take(cap).ToArray(), ordered.Skip(cap).ToArray());
    }

    internal static IReadOnlyList<AcceptanceTestFailureAttribution> IncludeOmittedAsUnattributed(
        IEnumerable<AcceptanceTestFailureAttribution> selected,
        IEnumerable<string> omitted,
        int cap) =>
        selected.Concat(omitted.Select(identity => new AcceptanceTestFailureAttribution(
            identity,
            AcceptanceTestFailureOrigin.Unattributed,
            $"merge-base focused attribution omitted by deterministic cap {cap}; identity remains actionable")))
        .ToArray();

    internal static CandidateSelectionPlan BuildCandidateSelections(
        string projectLabel,
        IReadOnlyList<string> selectors,
        AcceptanceGateEngineSettings engineSettings,
        string worktreePath)
    {
        var checks = new List<GoalAcceptanceVerifier.AcceptanceManifestCheck>();
        var coverageTargets = new List<FocusedEvidenceTargetCoverage>();
        var checkNamesBySelector = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var selector in selectors.Distinct(StringComparer.Ordinal))
        {
            var request = $"{projectLabel}: FullyQualifiedName~{selector}";
            if (!GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(
                    request,
                    engineSettings,
                    worktreePath,
                    out var selectorChecks,
                    out var coverage,
                    out var rejection))
            {
                return Failed($"focused baseline selection unavailable: {rejection.Detail}");
            }

            if (selectorChecks.Count != 1)
            {
                return Failed(
                    $"focused baseline selection produced {selectorChecks.Count} checks for exact identity '{selector}'");
            }

            checks.Add(selectorChecks[0]);
            coverageTargets.AddRange(coverage.TargetToChecks);
            checkNamesBySelector.Add(selector, selectorChecks[0].Name);
        }

        return new CandidateSelectionPlan(
            true,
            checks,
            new FocusedEvidenceCoverage(coverageTargets),
            checkNamesBySelector);

        CandidateSelectionPlan Failed(string evidence) => new(
            false,
            [],
            new FocusedEvidenceCoverage([]),
            new Dictionary<string, string>(),
            evidence);
    }

    internal static BaselineSourceSelectionPlan BuildBaselineSourceSelections(
        string baselineSha,
        IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck> candidateChecks,
        Func<string, string> projectLabel,
        AcceptanceGateEngineSettings engineSettings,
        string baselinePath)
    {
        var executable = new List<GoalAcceptanceVerifier.AcceptanceManifestCheck>();
        var classified = new List<AcceptanceCheckResult>();
        foreach (var candidateCheck in candidateChecks)
        {
            var request = candidateCheck.Project is not null && candidateCheck.FocusedEvidenceTokens.Count > 0
                ? $"{projectLabel(candidateCheck.Project)}: " +
                  string.Join("|", candidateCheck.FocusedEvidenceTokens.Select(token => token.CanonicalToken))
                : string.Empty;
            IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck> resolvedChecks = [];
            FocusedEvidenceRejection? rejection = null;
            var resolved = request.Length > 0 && GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(
                request,
                engineSettings,
                baselinePath,
                out resolvedChecks,
                out _,
                out rejection);
            if (resolved && resolvedChecks.Count == 1)
            {
                executable.Add(resolvedChecks[0]);
                continue;
            }

            var absent = request.Length > 0 &&
                rejection?.Code == FocusedEvidenceRejectionCode.UnresolvableSelection;
            classified.Add(new AcceptanceCheckResult(
                candidateCheck.Name,
                Passed: false,
                ExitCode: null,
                OutputTail: absent
                    ? $"Focused identity does not exist in baseline source at {baselineSha}."
                    : $"Focused baseline source selection was unavailable at {baselineSha}: " +
                      $"{rejection?.Detail ?? "invalid exact selector"}",
                FailureClassification: absent
                    ? AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline
                    : AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
                ExecutedTestCount: absent ? 0 : null,
                TestProjectPath: candidateCheck.Project));
        }

        return new BaselineSourceSelectionPlan(executable, classified);
    }

    internal static FocusedEvidenceArmRunResult CombineBaselineArm(
        IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck> candidateChecks,
        FocusedEvidenceArmRunResult executedArm,
        IReadOnlyList<AcceptanceCheckResult> sourceClassificationChecks,
        Func<IReadOnlyList<AcceptanceCheckResult>, FindingEvidenceArmDisposition> classifyArm)
    {
        if (sourceClassificationChecks.Count == 0)
        {
            return executedArm;
        }

        var checksByName = executedArm.Checks
            .Concat(sourceClassificationChecks)
            .ToDictionary(check => check.Name, StringComparer.Ordinal);
        var combinedChecks = candidateChecks.Select(check => checksByName[check.Name]).ToArray();
        var disposition = classifyArm(combinedChecks);
        return executedArm with
        {
            Disposition = disposition,
            Passed = disposition == FindingEvidenceArmDisposition.Green,
            Summary = $"{executedArm.Summary}; baseline-source-classified={sourceClassificationChecks.Count}",
            Checks = combinedChecks
        };
    }

    internal static IReadOnlyList<AcceptanceTestFailureAttribution> ClassifyBaselineFailures(
        IReadOnlyList<string> identities,
        IReadOnlyList<string> selectors,
        IReadOnlyDictionary<string, string> checkNamesBySelector,
        FocusedEvidenceArmRunResult baseline)
    {
        var baselineChecksByName = baseline.Checks
            .GroupBy(check => check.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        return identities.Select((identity, index) =>
        {
            var selector = selectors[index];
            if (!checkNamesBySelector.TryGetValue(selector, out var checkName) ||
                !baselineChecksByName.TryGetValue(checkName, out var check))
            {
                return Attribution(identity, AcceptanceTestFailureOrigin.Unattributed,
                    $"focused baseline result was missing at merge-base {baseline.Sha}");
            }

            if (check.FailureClassification == AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline)
            {
                return Attribution(identity, AcceptanceTestFailureOrigin.Introduced,
                    $"focused identity was absent at merge-base {baseline.Sha}");
            }

            if (check.FailureClassification is AcceptanceFailureClassifications.FocusedSelectionApparatusFailure or
                AcceptanceFailureClassifications.FocusedSelectionReceiptUnreadable)
            {
                return Attribution(identity, AcceptanceTestFailureOrigin.Unattributed,
                    $"focused baseline apparatus was unavailable at merge-base {baseline.Sha}");
            }

            var baselineFailures = (check.FailingTestIdentities ?? [])
                .Select(identity => identity.Trim())
                .ToHashSet(StringComparer.Ordinal);
            if (!check.Passed && baselineFailures.Contains(identity.Trim()))
            {
                return Attribution(identity, AcceptanceTestFailureOrigin.Inherited,
                    $"same focused identity failed at merge-base {baseline.Sha}");
            }

            var sameSelectorFailures = baselineFailures
                .Where(failure => NormalizeIdentity(failure).Equals(selector, StringComparison.Ordinal))
                .ToArray();
            if (!check.Passed &&
                HasDataCaseIdentity(identity) &&
                sameSelectorFailures is { Length: > 0 } &&
                sameSelectorFailures.All(HasDataCaseIdentity))
            {
                return Attribution(identity, AcceptanceTestFailureOrigin.Introduced,
                    $"same focused method failed at merge-base {baseline.Sha}, but this data-case identity did not");
            }

            if (!check.Passed && sameSelectorFailures.Length > 0)
            {
                return Attribution(identity, AcceptanceTestFailureOrigin.Unattributed,
                    $"same focused method failed at merge-base {baseline.Sha}, but exact data-case identity was unavailable");
            }

            return check.Passed
                ? Attribution(identity, AcceptanceTestFailureOrigin.Introduced,
                    $"focused identity was green at merge-base {baseline.Sha}")
                : Attribution(identity, AcceptanceTestFailureOrigin.Unattributed,
                    $"focused baseline was inconclusive at merge-base {baseline.Sha}");
        }).ToArray();
    }

    private static AcceptanceTestFailureAttribution Attribution(
        string identity,
        AcceptanceTestFailureOrigin origin,
        string evidence) => new(identity, origin, evidence);

    internal static string NormalizeIdentity(string identity) =>
        AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity);

    private static bool HasDataCaseIdentity(string identity) =>
        !NormalizeIdentity(identity).Equals(identity.Trim(), StringComparison.Ordinal);
}
