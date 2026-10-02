using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IConductorCohortPartitionRunner
{
    ConductorCohortFocusedPassResult RunFocused(AcceptanceCohortMemberBinding member, int ordinal,
        AcceptanceCohortIdentity identity, ConductorCohortFocusedSelection selection,
        ConductEventLogWriter writer, CancellationToken cancellationToken);

    AcceptanceCohortPartitionReceipt RunFull(AcceptanceCohortMemberBinding member, int ordinal,
        AcceptanceCohortIdentity identity, ConductEventLogWriter writer, CancellationToken cancellationToken);
}

internal enum ConductorCohortFocusedPassKind { Executed, InfrastructureFailure, SelectionRejected }

internal sealed record ConductorCohortFocusedSelection(string Request, IReadOnlyList<string> Identities);

internal sealed record ConductorCohortFocusedPassResult(
    ConductorCohortFocusedPassKind Kind,
    IReadOnlyList<string> FailingIdentities,
    IReadOnlyList<string> FailedChecks,
    int ExecutedCount,
    bool AllChecksPassed,
    IReadOnlyList<string> TestResultPaths,
    string? TreeRevision = null,
    long ElapsedMilliseconds = 0,
    string? Detail = null);

internal static class ConductorAcceptanceCohortFocusedAttribution
{
    internal static ConductorCohortFocusedSelection? TrySelect(
        IReadOnlyCollection<string> cohortFailingTests, IReadOnlyList<AcceptanceCheckResult> failedChecks)
    {
        if (cohortFailingTests.Count == 0 ||
            cohortFailingTests.Count > AcceptanceFailureAttributionPlanner.MaxCandidateRerunIdentities)
            return null;

        var identities = cohortFailingTests.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var requests = new List<string>();
        foreach (var identity in identities)
        {
            // Data cases and unqualified TRX display names cannot be selected individually.
            if (string.IsNullOrWhiteSpace(identity) ||
                AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity) != identity.Trim() ||
                !Regex.IsMatch(identity, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$"))
                return null;
            var projects = failedChecks
                .Where(check => check.FailingTestIdentities?.Contains(identity, StringComparer.Ordinal) == true)
                .Select(check => check.TestProjectPath)
                .Where(project => !string.IsNullOrWhiteSpace(project))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (projects.Length > 1) return null;
            var label = projects.Length == 1 ? $"{GoalAcceptanceVerifier.ProjectLabel(projects[0]!)}: " : "";
            requests.Add($"{label}FullyQualifiedName~{identity}");
        }
        return new(string.Join("; ", requests), identities);
    }

    internal static ConductorCohortFocusedPassResult Interpret(
        FocusedEvidenceRunResult result, ConductorCohortFocusedSelection selection)
    {
        if (!result.Accepted || result.Rejection is not null)
            return Unusable(ConductorCohortFocusedPassKind.SelectionRejected, result.Summary);
        if (result.OutcomeReason == FindingEvidenceOutcomeReason.ApparatusFailure || result.Checks.Count == 0 ||
            result.Checks.Any(check => check.ExitCode is null || check.ExecutedTestCount is null or <= 0 ||
                AcceptanceFailureClassifications.IsEnvironmentalApparatus(check.FailureClassification) ||
                check.CompletionDecision is { TimedOut: true } ||
                check.CompletionDecision?.FailedPredicate is AcceptanceShardCompletionPredicates.IncompleteExecution or
                    AcceptanceShardCompletionPredicates.MissingTrx or AcceptanceShardCompletionPredicates.MalformedTrx) ||
            result.Checks.Sum(check => check.ExecutedTestCount ?? 0) < selection.Identities.Count)
            return Unusable(ConductorCohortFocusedPassKind.InfrastructureFailure, result.Summary);

        var paths = result.Checks.SelectMany(check => check.TestResultPaths ?? []).Distinct(StringComparer.Ordinal).ToArray();
        var failures = ConductorDriver.CohortFailingTestIdentities(new AcceptanceVerificationResult(
            result.Passed, false, result.Passed ? 0 : 1, result.Summary,
            Checks: result.Checks, TestResultPaths: paths));
        return new(ConductorCohortFocusedPassKind.Executed, failures,
            result.Checks.Where(check => !check.Passed).Select(check => check.Name).ToArray(),
            result.Checks.Sum(check => check.ExecutedTestCount!.Value),
            result.Passed && result.Checks.All(check => check.Passed && check.ExitCode == 0), paths);
    }

    internal static ConductorCohortFocusedPassResult Unusable(ConductorCohortFocusedPassKind kind, string detail) =>
        new(kind, [], [], 0, false, [], Detail: detail);

    internal static int? Decide(IReadOnlyCollection<string> cohortFailingTests,
        ConductorCohortFocusedPassResult first, ConductorCohortFocusedPassResult second)
    {
        if (cohortFailingTests.Count == 0 || first.Kind != ConductorCohortFocusedPassKind.Executed ||
            second.Kind != ConductorCohortFocusedPassKind.Executed) return null;
        var identities = cohortFailingTests.ToHashSet(StringComparer.Ordinal);
        // The focused verifier resolves every selector and checks TRX selection coverage.
        // Batched checks expose counts rather than per-identity passes; require that lower bound too.
        bool Green(ConductorCohortFocusedPassResult result) => result.AllChecksPassed &&
            result.FailingIdentities.Count == 0 && result.ExecutedCount >= identities.Count;
        bool Reproduces(ConductorCohortFocusedPassResult result) => !result.AllChecksPassed &&
            identities.IsSubsetOf(result.FailingIdentities) && result.ExecutedCount >= identities.Count;
        if (Reproduces(first) && Green(second)) return 0;
        if (Reproduces(second) && Green(first)) return 1;
        return null;
    }
}
