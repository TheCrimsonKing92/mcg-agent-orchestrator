using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;
using Plan = Mcg.AgentOrchestrator.Infrastructure.AcceptanceLaneRerunSelection.Plan;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Narrowing changes the execution check, while the original check continues to own the lane receipt.
internal static class AcceptanceLaneRerunNarrowing
{
    internal const string RunnerNotMtp = "runner-not-mtp";
    internal const string MissingLaneFilter = "missing-lane-filter";

    internal static Plan Select(AcceptanceManifestCheck check, IReadOnlyList<string> identities)
    {
        if (!AcceptanceCheckCommandBuilder.UsesMicrosoftTestingPlatform(check))
            return Plan.WholeLane(RunnerNotMtp);
        // Focused tokens replace a --filter value; without one the builder ignores them.
        var arguments = new List<string>();
        if (string.IsNullOrWhiteSpace(AcceptanceCheckCommandBuilder.ExtractFilterArguments(check.Arguments, arguments)))
            return Plan.WholeLane(MissingLaneFilter);
        return AcceptanceLaneRerunSelection.Create(identities);
    }

    internal static AcceptanceManifestCheck BuildRerunCheck(AcceptanceManifestCheck check, Plan plan) =>
        !plan.IsNarrowed ? check : new()
        {
            Name = check.Name, Type = check.Type, Command = check.Command, Project = check.Project,
            Arguments = NarrowedArguments(check.Arguments, plan), Pattern = check.Pattern, FilePath = check.FilePath,
            TimeoutMinutes = check.TimeoutMinutes, Advisory = check.Advisory, Runner = check.Runner,
            EstimatedSerialSeconds = check.EstimatedSerialSeconds, ExclusiveResourceKeys = check.ExclusiveResourceKeys,
            IsFocusedEvidenceSelection = check.IsFocusedEvidenceSelection,
            FocusedEvidenceTokens = plan.Tokens, FocusedEvidenceSelections = [plan.Tokens],
            FocusedEvidenceMatchedClasses = check.FocusedEvidenceMatchedClasses,
            IsResolvedInfrastructureLane = check.IsResolvedInfrastructureLane,
            RequiresBuildSystemChange = check.RequiresBuildSystemChange
        };

    private static IReadOnlyList<string> NarrowedArguments(IReadOnlyList<string> arguments, Plan plan)
    {
        var narrowed = new List<string>();
        if (string.IsNullOrWhiteSpace(AcceptanceCheckCommandBuilder.ExtractFilterArguments(arguments, narrowed)))
            throw new InvalidOperationException("A narrowed lane rerun requires an original filter.");
        // The extractor leaves the original filter at the end. Keep all other
        // arguments, and bind the replacement expression to the same method tokens.
        narrowed[^1] = string.Join('|', plan.Tokens.Select(token => token.CanonicalToken));
        return narrowed;
    }

    internal static bool Passed(Plan plan, AcceptanceCheckResult rerun) =>
        rerun.Passed && (!plan.IsNarrowed ||
            // Completion adjudication certifies readable TRX, no fatal outcomes, and a complete run.
            (rerun.CompletionDecision is { Passed: true, FailedPredicate: null } &&
             (rerun.CompletionDecision.ExecutedTestCount ?? rerun.ExecutedTestCount) >= plan.Tokens.Count &&
             rerun.TestResultPaths is { Count: > 0 } &&
             rerun.FailingTestIdentities is not { Count: > 0 }));

    internal static AcceptanceCheckResult ComposeResult(
        Plan plan, AcceptanceCheckResult first, AcceptanceCheckResult rerun, bool passed)
    {
        if (!plan.IsNarrowed)
            return rerun;

        // Preserve the original failure when an apparently green subset lacks completion evidence.
        var result = !passed && rerun.Passed ? first : rerun;
        return result with
        {
            TestResultPaths = (first.TestResultPaths ?? []).Concat(rerun.TestResultPaths ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ExecutedTestCount = Max(first.ExecutedTestCount ?? first.CompletionDecision?.ExecutedTestCount,
                rerun.ExecutedTestCount ?? rerun.CompletionDecision?.ExecutedTestCount),
            DiscoveredTestCount = Max(first.DiscoveredTestCount ?? first.CompletionDecision?.DiscoveredTestCount,
                rerun.DiscoveredTestCount ?? rerun.CompletionDecision?.DiscoveredTestCount)
        };
    }

    internal static string ModeText(Plan plan) => plan.IsNarrowed
        ? "mode=failed-only" : $"mode=whole-lane reason={plan.FallbackReason}";

    private static int? Max(int? first, int? second) =>
        first is null ? second : second is null ? first : Math.Max(first.Value, second.Value);
}
