using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the translation from an impact plan to broker-ready pre-review evidence context.
internal static class PreReviewEvidenceContextBuilder
{
    internal static PreReviewEvidenceContext Build(
        string? candidateSha, RepositoryChangeSummary changeSummary, RepositoryTestImpactPlan plan,
        Action<string>? log = null) =>
        BuildMappedPreReviewEvidenceContext(candidateSha, changeSummary, plan, log ?? Console.WriteLine) with
        {
            TestImpactDegradation = plan.ReverseDependencyDegradation,
            TestImpactHeadroom = plan.ReverseDependencyHeadroom
        };

    private static PreReviewEvidenceContext BuildMappedPreReviewEvidenceContext(
        string? candidateSha, RepositoryChangeSummary changeSummary, RepositoryTestImpactPlan plan,
        Action<string> log)
    {
        // Manifest scoping can replace the plan's no-build check; candidate cleanup still takes priority.
        if (changeSummary.HasGeneratedArtifacts ||
            (!plan.RequiresBuild &&
             plan.Checks.Count > 0 &&
             plan.Checks.All(check => check.Command.Count == 0)))
        {
            var generatedArtifactsBlock = changeSummary.HasGeneratedArtifacts;
            return new PreReviewEvidenceContext(
                candidateSha,
                [],
                null,
                plan.Summary,
                NoApplicableTests: !generatedArtifactsBlock,
                MappingNeedsInput: generatedArtifactsBlock,
                SourceCleanupPaths: changeSummary.Files
                    .Where(file => file.IsGeneratedArtifact)
                    .Select(file => file.Path)
                    .ToArray());
        }

        var focusedChecks = plan.Checks
            .Where(check => FindArgument(check.Command, "--filter") >= 0)
            .ToArray();
        if (focusedChecks.Length == 0)
        {
            return new PreReviewEvidenceContext(
                candidateSha,
                [],
                null,
                $"{plan.Summary} No filtered test target mapped; project-wide checks are deferred to the acceptance gate.",
                NoApplicableTests: true,
                MappingNeedsInput: false);
        }

        var selected = focusedChecks.Select(check => check.CommandLine).ToArray();
        var requests = new List<string>();
        var notApplicableChecks = new List<string>();
        string WithNotApplicableChecks(string reason) => notApplicableChecks.Count == 0
            ? reason
            : $"{reason} {string.Join(" ", notApplicableChecks)}";

        foreach (var check in focusedChecks)
        {
            var filterIndex = FindArgument(check.Command, "--filter");
            var project = check.Command.FirstOrDefault(argument =>
                argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(project))
            {
                return new PreReviewEvidenceContext(
                    candidateSha,
                    selected,
                    null,
                    WithNotApplicableChecks(plan.Summary),
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }

            if (!PreReviewFocusedRequestSplitter.TryResolveBrokerAlias(project, out var alias))
            {
                return new PreReviewEvidenceContext(
                    candidateSha,
                    selected,
                    null,
                    WithNotApplicableChecks($"Mapped project is not supported by the focused evidence broker: {project}"),
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }
            if (filterIndex >= 0 && filterIndex + 1 >= check.Command.Count)
            {
                return new PreReviewEvidenceContext(
                    candidateSha,
                    selected,
                    null,
                    WithNotApplicableChecks($"Mapped test command has an empty --filter argument: {check.CommandLine}"),
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }

            if (PreReviewExclusionOnlyFilter.IsExclusionOnly(check.Command[filterIndex + 1]))
            {
                var decision = $"check=\"{check.Name}\" reason=\"{PreReviewExclusionOnlyFilter.NotApplicableReason}\"";
                notApplicableChecks.Add($"not-applicable {decision}");
                log($"PRE_REVIEW_CHECK_NOT_APPLICABLE candidate={candidateSha} {decision}");
                continue;
            }

            if (!PreReviewFocusedRequestSplitter.TrySplitRequestItems(
                    alias!, check.Command[filterIndex + 1], out var requestItems))
            {
                return new PreReviewEvidenceContext(
                    candidateSha, selected, null,
                    WithNotApplicableChecks("Mapped test filter cannot be split into broker-safe positive clauses."),
                    NoApplicableTests: false, MappingNeedsInput: true);
            }
            requests.AddRange(requestItems);
        }

        var noApplicableTests = requests.Count == 0 && notApplicableChecks.Count > 0;
        return new PreReviewEvidenceContext(
            candidateSha,
            requests,
            noApplicableTests ? null : string.Join("; ", requests),
            WithNotApplicableChecks(plan.Summary),
            NoApplicableTests: noApplicableTests,
            MappingNeedsInput: requests.Count == 0 && !noApplicableTests);
    }

    private static int FindArgument(IReadOnlyList<string> arguments, string value)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
