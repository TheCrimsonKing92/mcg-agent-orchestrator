using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static partial class WorkerSubscriptionModelResolver
{
    internal const string TesterCascadeCheapLane = "codex-luna:cascade-cheap";
    internal const string TesterCascadeEscalatedLane = "codex-cli:cascade-escalated";

    private static SubscriptionModelSelection RouteDeveloperCascade(
        AgentDefinition agent, Goal goal, TaskSpec task, SubscriptionModelSelection primary,
        WorkerProviderCatalog providers, WorkerProfileCatalog? profiles,
        bool enabled, string? alias)
    {
        SubscriptionModelSelection Full(string decision, string rule) => primary with
        {
            Reason = new CascadeRouteMarker(decision, rule).AppendTo(primary.Reason),
            DispatchLane = decision == CascadeRouteMarker.Escalated ? TesterCascadeEscalatedLane : primary.DispatchLane
        };
        SubscriptionModelSelection Cheap(string rule, IReadOnlyList<string> ids)
        {
            var modelAlias = alias ?? AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias;
            if (string.IsNullOrWhiteSpace(modelAlias))
                throw new ArgumentException("cascadeCheapModelAlias must not be empty.", nameof(alias));
            return new(primary.Complexity,
                new ModelProfile("OpenAI", modelAlias, agent.Model.Capabilities, SubscriptionMode.ApiKey,
                    AgentCatalog.DefaultSubscriptionReasoningEffort("OpenAI", modelAlias), agent.Model.MaxOutputTokens),
                UsesComplexModel: false, UsesSubscriptionLaunchProfile: false,
                Reason: new CascadeRouteMarker(CascadeRouteMarker.Cheap, rule, ids).AppendTo(primary.Reason),
                LaunchProfileName: WorkerProfileDispatcher.OpenAiLunaSubscriptionProfileName,
                DispatchLane: TesterCascadeCheapLane);
        }
        bool ProfileAvailable() => profiles is not null && TryValidateLunaProfile(profiles, providers, out _);

        if (!enabled) return Full(CascadeRouteMarker.Primary, "switched-off");
        // Refresh rebuilds the prepared round; it must not treat its own ids as persisting findings.
        if (task.Status == WorkTaskStatus.Running && task.LastProcess is null &&
            CascadeRouteMarker.TryParse(task.LastDispatch?.ModelSelectionReason, out var prepared))
            return prepared.Decision == CascadeRouteMarker.Cheap
                ? ProfileAvailable() ? Cheap(prepared.RuleId, prepared.Ids ?? []) : Full(CascadeRouteMarker.Primary, "cheap-profile-unavailable")
                : Full(prepared.Decision, prepared.RuleId);

        if (task.DispatchHistory.Count == 0) return Full(CascadeRouteMarker.Primary, "first-round");
        if (task.DispatchHistory.Any(d => CascadeRouteMarker.TryParse(d.ModelSelectionReason, out var route) &&
                route.Decision == CascadeRouteMarker.Escalated))
            return Full(CascadeRouteMarker.Primary, "prior-escalation");
        var classification = MechanicalReworkClassifier.Classify(goal, task);
        if (!classification.IsMechanical) return Full(CascadeRouteMarker.Primary, "non-mechanical");
        var previous = task.DispatchHistory.Last();
        if (CascadeRouteMarker.TryParse(previous.ModelSelectionReason, out var cheap) && cheap.Decision == CascadeRouteMarker.Cheap)
        {
            if ((cheap.Ids ?? []).Intersect(classification.Ids, StringComparer.Ordinal).Any())
                return Full(CascadeRouteMarker.Escalated, "mechanical-finding-persisted");
            // Kernel event order binds the latest verification to this latest dispatch, even if clocks differ.
            var anchor = -1;
            for (var i = goal.Timeline.Count - 1; i >= 0; i--)
                if (goal.Timeline[i].TaskId == task.Id && goal.Timeline[i].Kind == ProgressKind.TaskDispatchRecorded)
                {
                    anchor = i;
                    break;
                }
            var verification = anchor >= 0
                ? goal.Timeline.Skip(anchor + 1).Any(e => e.TaskId == task.Id && e.Kind == ProgressKind.TaskVerificationRecorded)
                    ? task.VerificationHistory.LastOrDefault() : null
                : task.VerificationHistory.LastOrDefault(v => v.CompletedAt > previous.DispatchedAt);
            if (verification?.CompletionVerdictRule == "provider-model-rejection")
                return Full(CascadeRouteMarker.Escalated, "cheap-model-rejected");
        }
        if (!ProfileAvailable()) return Full(CascadeRouteMarker.Primary, "cheap-profile-unavailable");
        return Cheap(classification.RuleId!, classification.Ids);
    }

    private static SubscriptionModelSelection RouteTesterCascade(
        AgentDefinition agent, Goal goal, TaskSpec task, SubscriptionModelSelection primary,
        WorkerProviderCatalog providers, WorkerProfileCatalog? profiles,
        bool enabled, string? alias)
    {
        SubscriptionModelSelection Full(string decision, string rule) => primary with
        {
            Reason = new CascadeRouteMarker(decision, rule).AppendTo(primary.Reason),
            DispatchLane = decision == CascadeRouteMarker.Escalated ? TesterCascadeEscalatedLane : primary.DispatchLane
        };
        SubscriptionModelSelection Cheap(string rule)
        {
            var modelAlias = alias ?? AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias;
            if (string.IsNullOrWhiteSpace(modelAlias))
                throw new ArgumentException("cascadeCheapModelAlias must not be empty.", nameof(alias));
            return new(primary.Complexity,
                new ModelProfile("OpenAI", modelAlias, agent.Model.Capabilities, SubscriptionMode.ApiKey,
                    AgentCatalog.DefaultSubscriptionReasoningEffort("OpenAI", modelAlias), agent.Model.MaxOutputTokens),
                UsesComplexModel: false, UsesSubscriptionLaunchProfile: false,
                Reason: new CascadeRouteMarker(CascadeRouteMarker.Cheap, rule).AppendTo(primary.Reason),
                LaunchProfileName: WorkerProfileDispatcher.OpenAiLunaSubscriptionProfileName,
                DispatchLane: TesterCascadeCheapLane);
        }
        bool ProfileAvailable() => profiles is not null && TryValidateLunaProfile(profiles, providers, out _);

        if (!enabled) return Full(CascadeRouteMarker.Primary, "switched-off");

        // A pending dispatch refresh is the same round, not evidence of an earlier escalation.
        if (task.Status == WorkTaskStatus.Running && task.LastProcess is null &&
            CascadeRouteMarker.TryParse(task.LastDispatch?.ModelSelectionReason, out var prepared))
        {
            return prepared.Decision == CascadeRouteMarker.Cheap
                ? ProfileAvailable() ? Cheap(prepared.RuleId) : Full(CascadeRouteMarker.Primary, "cheap-profile-unavailable")
                : Full(prepared.Decision, prepared.RuleId);
        }

        if (task.DispatchHistory.Any(d => CascadeRouteMarker.TryParse(d.ModelSelectionReason, out var route) &&
            route.Decision == CascadeRouteMarker.Escalated))
            return Full(CascadeRouteMarker.Primary, "prior-escalation");

        var cheapRound = task.DispatchHistory.LastOrDefault(d =>
            CascadeRouteMarker.TryParse(d.ModelSelectionReason, out var route) && route.Decision == CascadeRouteMarker.Cheap);
        TaskVerificationRecord? verification = null;
        if (cheapRound is not null)
        {
            // Kernel event order is authoritative when caller dispatch timestamps use another clock.
            // Match history/events backwards; retained history or timeline may have been trimmed.
            var newerDispatches = task.DispatchHistory.Reverse().TakeWhile(d => !ReferenceEquals(d, cheapRound)).Count();
            var dispatchEvents = goal.Timeline.Select((e, index) => (Event: e, Index: index))
                .Where(e => e.Event.TaskId == task.Id && e.Event.Kind == ProgressKind.TaskDispatchRecorded).ToArray();
            var anchor = dispatchEvents.Length > newerDispatches ? dispatchEvents[^(newerDispatches + 1)].Index : -1;
            var laterEvents = goal.Timeline.Skip(anchor + 1).Where(e => e.TaskId == task.Id &&
                (anchor >= 0 || e.OccurredAt > cheapRound.DispatchedAt)).ToArray();
            verification = anchor >= 0
                ? laterEvents.Any(e => e.Kind == ProgressKind.TaskVerificationRecorded) ? task.VerificationHistory.LastOrDefault() : null
                : task.VerificationHistory.LastOrDefault(v => v.CompletedAt > cheapRound.DispatchedAt);
            if (laterEvents.Any(e =>
                    e.Kind == ProgressKind.TaskFailed && e.Message.StartsWith("Tester WORKER_RESULT structured findings invalid: ", StringComparison.Ordinal) ||
                    e.Kind == ProgressKind.TaskNote && e.Message.StartsWith("Rejected Tester structured finding result:", StringComparison.Ordinal)) ||
                TryFindWorkerResultGuardrailFailure(AgentRole.Tester, verification, out _))
                return Full(CascadeRouteMarker.Escalated, "tester-contract-rejected");
            if (verification?.CompletionVerdictRule == "provider-model-rejection")
                return Full(CascadeRouteMarker.Escalated, "cheap-model-rejected");
        }

        if (RetryContextFingerprintFactory.GetOpenBlockingFindings(goal).Any(f => f.Category == FindingCategory.TestEvidence))
            return Full(cheapRound is null ? CascadeRouteMarker.Primary : CascadeRouteMarker.Escalated, "tester-evidence-finding");
        if (!ProfileAvailable()) return Full(CascadeRouteMarker.Primary, "cheap-profile-unavailable");
        return Cheap("tester-cheap-first");
    }
}
