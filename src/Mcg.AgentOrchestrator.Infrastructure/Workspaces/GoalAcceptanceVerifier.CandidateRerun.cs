using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private sealed record CandidateRerunPreparedCheck(
        int Index,
        AcceptanceCheckResult Check,
        AcceptanceFailureAttributionPlanner.BoundedIdentitySelection IdentitySelection,
        string[] Selectors,
        AcceptanceFailureAttributionPlanner.CandidateSelectionPlan SelectionPlan);

    private async Task ApplyCandidateRerunToChecksAsync(
        IReadOnlyList<CandidateRerunPreparedCheck> prepared,
        AcceptanceCheckResult[] results,
        string baselineSha,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        IReadOnlyList<string>? changedFiles,
        AcceptanceFailureAttributionPlanner.FocusedInvocationBudget invocationBudget,
        CancellationToken cancellationToken)
    {
        var allAttributions = prepared
            .SelectMany(item => results[item.Index].FailingTestAttributions ?? [])
            .ToArray();
        var projectByIdentity = prepared
            .SelectMany(item => item.IdentitySelection.Selected.Select(identity =>
                (Identity: identity, Project: item.Check.TestProjectPath)))
            .GroupBy(item => item.Identity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Project, StringComparer.Ordinal);
        var rerunner = new CandidateFailureRerunner(async (identities, token) =>
        {
            var selected = prepared
                .SelectMany(item => item.IdentitySelection.Selected
                    .Where(identities.Contains)
                    .Select(identity =>
                    {
                        var selector = AcceptanceFailureAttributionPlanner.NormalizeIdentity(identity);
                        var name = item.SelectionPlan.CheckNamesBySelector[selector];
                        return (Identity: identity, Check: item.SelectionPlan.Checks.Single(check => check.Name == name));
                    }))
                .GroupBy(item => item.Identity, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            var checks = selected.Select(item => item.Check)
                .DistinctBy(check => check.Name, StringComparer.Ordinal).ToArray();
            var coverage = new FocusedEvidenceCoverage(
                selected.Select(item => new FocusedEvidenceTargetCoverage(
                    item.Identity, [item.Check.Name])).ToArray(),
                ExecutionReason: "candidate-failure-attribution-rerun");
            var arm = await RunFocusedEvidenceArmAsync(
                FindingEvidenceArm.Candidate,
                ResolveGitScalar(worktreePath, "rev-parse", "HEAD"),
                worktreePath,
                goalId,
                checks,
                coverage,
                stableSlotIndex,
                stableSlotLease,
                executionEnvironment: null,
                cancellationToken: token,
                continueAfterFailure: true,
                executionOwner: _executionContext).ConfigureAwait(false);
            var byName = arm.Checks.ToDictionary(check => check.Name, StringComparer.Ordinal);
            return selected.ToDictionary(item => item.Identity, item =>
            {
                if (!byName.TryGetValue(item.Check.Name, out var check))
                {
                    return new CandidateFailureRerunResult(null, null, "candidate rerun check was missing");
                }

                var receipt = check.TestResultPaths?.FirstOrDefault() ?? check.ArtifactsPath;
                return string.IsNullOrWhiteSpace(receipt)
                    ? new CandidateFailureRerunResult(null, null, "candidate rerun receipt was missing")
                    : new CandidateFailureRerunResult(check.Passed, receipt);
            }, StringComparer.Ordinal);
        });
        var rerunAttributions = await AcceptanceFailureAttributionPlanner.ApplyCandidateRerunAsync(
            allAttributions,
            baselineSha,
            identity => AcceptanceTestSourceResolver.ResolveSourcePaths(
                worktreePath,
                projectByIdentity.GetValueOrDefault(identity),
                identity),
            changedFiles,
            invocationBudget,
            rerunner,
            cancellationToken).ConfigureAwait(false);
        var attributionOffset = 0;
        foreach (var item in prepared)
        {
            var attributionCount = results[item.Index].FailingTestAttributions!.Count;
            results[item.Index] = results[item.Index] with
            {
                FailingTestAttributions = rerunAttributions
                    .Skip(attributionOffset)
                    .Take(attributionCount)
                    .ToArray()
            };
            attributionOffset += attributionCount;
        }
    }
}
