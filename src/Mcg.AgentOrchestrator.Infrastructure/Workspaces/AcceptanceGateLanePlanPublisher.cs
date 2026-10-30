using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceGateLanePlanPublisher
{
    internal static AcceptanceGateLanePlan Classify(string attemptId,
        IReadOnlyList<AcceptanceTestLane> candidates, IReadOnlyList<AcceptanceTestLane> selected,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks, IReadOnlyList<AcceptanceManifestCheck> requiredChecks,
        PolicyShardPlan policy, DotnetShardDisposition disposition,
        IReadOnlyList<GoalAcceptanceVerifier.SemanticExecutionDeduplicationReceipt> deduplications,
        IReadOnlyList<AcceptanceManifestCheck> planned, IReadOnlyList<AcceptanceManifestCheck> attributed,
        IReadOnlyList<AcceptanceManifestCheck> dispatched)
    {
        var scheduled = new List<string>();
        var skipped = new List<AcceptanceGateLaneSkip>();
        var broad = manifestChecks.Where(IsBroadInfrastructureTestCheck).ToArray();
        var focused = requiredChecks.Where(IsFocusedProjectDotnetCheck).ToArray();
        foreach (var lane in candidates)
        {
            bool Contains(IReadOnlyList<AcceptanceManifestCheck> checks) => checks.Any(check => Matches(check, lane));
            // Actual dispatch wins: attribution can restore a lane excluded by an earlier stage.
            if (Contains(dispatched)) { scheduled.Add(lane.Name); continue; }
            string? reason = !selected.Any(item => item.Name == lane.Name) ? AcceptanceGateLaneSkip.NoBuildSystemChange :
                disposition != DotnetShardDisposition.RunDotnetShards ? DotnetShardDispositionReason(disposition) :
                policy.IsAbsentProject(InfrastructureTestsProject) ? AcceptanceGateLaneSkip.AbsentProject :
                Contains(attributed) ? AcceptanceGateLaneSkip.OutsideChangeScope :
                Contains(planned) ? AcceptanceGateLaneSkip.OutsideAttributionScope :
                deduplications.Any(item => item.DroppedCheckName.EndsWith(": " + lane.Name, StringComparison.Ordinal)) ? AcceptanceGateLaneSkip.SemanticDuplicate :
                broad.Length > 0 && broad.All(check => IsSkippedPolicyShardCheck(check, policy)) ? AcceptanceGateLaneSkip.OutsideDependencyClosure :
                broad.Any(check => IsReplacedByFocusedProjectCheck(check, focused)) ? AcceptanceGateLaneSkip.ReplacedByFocusedCheck : null;
            // No matching check and no exclusion evidence means this is an unrelated manifest row.
            if (reason is not null) skipped.Add(new(lane.Name, reason));
        }
        return new(AcceptanceGateLanePlan.CurrentSchemaVersion, attemptId, scheduled, skipped);
    }

    private static bool Matches(AcceptanceManifestCheck check, AcceptanceTestLane lane)
    {
        if (!check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) ||
            !ProjectMatches(check.Project, InfrastructureTestsProject)) return false;
        for (var i = 0; i + 1 < check.Arguments.Count; i++)
            if (check.Arguments[i].Equals("--filter", StringComparison.OrdinalIgnoreCase) &&
                check.Arguments[i + 1].Equals(lane.Filter, StringComparison.Ordinal)) return true;
        return false;
    }

    internal static void Publish(AcceptanceGateLanePlan plan, string? attemptPrefix, GoalId? goalId,
        Action<AcceptanceGateProgress> emit, TimeProvider clock)
    {
        var path = string.IsNullOrWhiteSpace(attemptPrefix) ? "" : attemptPrefix + AcceptanceGateLanePlan.ArtifactSuffix;
        if (path.Length > 0)
        {
            try { File.WriteAllText(path, plan.ToJson()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var now = clock.GetUtcNow();
        emit(new(goalId?.Value, "lane-plan", plan.FormatEventTarget(), null, null, null,
            now, now, now, TimeSpan.Zero, 0, path));
    }
}
