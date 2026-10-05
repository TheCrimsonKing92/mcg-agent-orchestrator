using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the translation from an impact plan to broker-ready pre-review evidence context.
internal static class PreReviewEvidenceContextBuilder
{
    internal static PreReviewEvidenceContext Build(
        string? candidateSha, RepositoryChangeSummary changeSummary, RepositoryTestImpactPlan plan) =>
        BuildMappedPreReviewEvidenceContext(candidateSha, changeSummary, plan) with
        {
            TestImpactDegradation = plan.ReverseDependencyDegradation,
            TestImpactHeadroom = plan.ReverseDependencyHeadroom
        };

    private static PreReviewEvidenceContext BuildMappedPreReviewEvidenceContext(
        string? candidateSha, RepositoryChangeSummary changeSummary, RepositoryTestImpactPlan plan)
    {
        if (!plan.RequiresBuild &&
            plan.Checks.Count > 0 &&
            plan.Checks.All(check => check.Command.Count == 0))
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
                    plan.Summary,
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }

            if (!PreReviewFocusedRequestSplitter.TryResolveBrokerAlias(project, out var alias))
            {
                return new PreReviewEvidenceContext(
                    candidateSha,
                    selected,
                    null,
                    $"Mapped project is not supported by the focused evidence broker: {project}",
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }
            if (filterIndex >= 0 && filterIndex + 1 >= check.Command.Count)
            {
                return new PreReviewEvidenceContext(
                    candidateSha,
                    selected,
                    null,
                    $"Mapped test command has an empty --filter argument: {check.CommandLine}",
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }

            if (!PreReviewFocusedRequestSplitter.TrySplitRequestItems(
                    alias!, check.Command[filterIndex + 1], out var requestItems))
            {
                return new PreReviewEvidenceContext(
                    candidateSha, selected, null,
                    "Mapped test filter cannot be split into broker-safe positive clauses.",
                    NoApplicableTests: false, MappingNeedsInput: true);
            }
            requests.AddRange(requestItems);
        }

        return new PreReviewEvidenceContext(
            candidateSha,
            requests,
            string.Join("; ", requests),
            plan.Summary,
            NoApplicableTests: false,
            MappingNeedsInput: requests.Count == 0);
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
