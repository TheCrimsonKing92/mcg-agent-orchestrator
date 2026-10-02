using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    private static bool HandleAttentionAnswer(
        IReadOnlyList<string> parts, CliExecutionContext context, CollaborationItemStore store)
    {
        var answerParts = WithoutAnswerMetadataFlags(parts);
        if (answerParts.Count < 4)
            throw new ArgumentException(CliCommandHelp.AttentionUsage);

        var idOrGoal = answerParts[2];
        var clarifications = OpenClarifications(store);
        var globalClarification = ResolveClarificationByExactShortId(
            clarifications, clarifications, idOrGoal,
            $"Id '{idOrGoal}' is ambiguous ({{0}} matches); use a goal-scoped id from `attention show <goal-id>` or a full correlation key.");
        var globalHumanRequest = globalClarification is null && context.Kernel.HumanInputRequests.Any(request =>
            request.Id.Value.StartsWith(idOrGoal, StringComparison.OrdinalIgnoreCase));
        if (globalClarification is null && !globalHumanRequest &&
            !context.Kernel.Goals.Any(candidate => candidate.Id.Value.StartsWith(idOrGoal, StringComparison.OrdinalIgnoreCase)))
        {
            var allClarifications = AllClarifications(store);
            globalClarification = ResolveClarificationByExactShortId(
                allClarifications, allClarifications, idOrGoal,
                $"Id '{idOrGoal}' is ambiguous ({{0}} matches); use a goal-scoped id from `attention show <goal-id>` or a full correlation key.");
        }
        var scoped = globalClarification is null && !globalHumanRequest;
        var goal = scoped ? ResolveAttentionGoal(context.Kernel, idOrGoal) : null;
        if (scoped && answerParts.Count < 5)
            throw new ArgumentException(CliCommandHelp.AttentionUsage);

        var id = scoped ? answerParts[3] : idOrGoal;
        var answer = ResolveTextArgument(answerParts, scoped ? 4 : 3,
            CliCommandHelp.AttentionUsage, "--text-file");
        OperatorAnswerTargetKind targetKind;
        string targetId;

        if (globalHumanRequest)
        {
            var request = OrchestratorEntityResolver.ResolveHumanInputRequest(context.Kernel, id);
            goal = context.Kernel.GetGoal(request.GoalId);
            targetKind = OperatorAnswerTargetKind.HumanInput;
            targetId = request.Id.Value;
        }
        else
        {
            CollaborationItem? clarification = globalClarification;
            if (scoped)
            {
                var scopedClarifications = AllClarificationsForGoal(store, goal!);
                clarification = TryResolveClarificationByShortId(
                    scopedClarifications, scopedClarifications, id,
                    $"Id '{id}' is ambiguous ({{0}} matches); copy a full id from `attention show {goal!.Id.Value[..8]}` or use a full correlation key.");
                if (clarification is null)
                {
                    var matchesHumanRequest = context.Kernel.HumanInputRequests.Any(request =>
                        request.GoalId == goal!.Id && request.Id.Value.StartsWith(id, StringComparison.OrdinalIgnoreCase));
                    if (!matchesHumanRequest)
                        throw new ArgumentException(
                            $"Clarification id '{id}' was not found for goal '{goal!.Id.Value}'. Run `attention show {goal.Id.Value[..8]}` to list valid identifiers, including human-wait request ids.");
                    var request = OrchestratorEntityResolver.ResolveHumanInputRequest(context.Kernel, goal!.Id, id);
                    targetKind = OperatorAnswerTargetKind.HumanInput;
                    targetId = request.Id.Value;
                    goto Submit;
                }
            }

            goal ??= context.Kernel.Goals.FirstOrDefault(candidate =>
                string.Equals(candidate.Id.Value, clarification!.GoalId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Goal '{clarification!.GoalId}' for clarification '{id}' was not found.");
            targetKind = OperatorAnswerTargetKind.Clarification;
            targetId = clarification!.Id;
        }

    Submit:
        var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(
            parts, CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
        var intentId = Guid.NewGuid().ToString("N");
        var payload = new AnswerOperatorIntentPayload(targetKind, targetId, goal!.Id.Value, answer,
            attribution.ActorKind, GetAnswerEvidence(parts), GetFlagValue(parts, "--precedent"));
        var intent = new OperatorIntentRecord(
            intentId, GetFlagValue(parts, "--idempotency-key") ?? intentId,
            OperatorIntentVerbs.Answer, goal.Id.Value, TaskId: null,
            JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [],
            attribution.Actor, attribution.Channel, attribution.AuthenticationAssurance,
            DateTimeOffset.UtcNow, ActorKind: attribution.ActorKind);
        var persisted = SqliteOperatorIntentStore.ForDirectories(
            context.Workspace.OrchestratorDirectory, context.Workspace.LogDirectory)
            .EnqueueAsync(intent).GetAwaiter().GetResult();
        Console.WriteLine($"Operator intent queued: id={persisted.Id} verb={persisted.Verb} " +
            $"goal={goal.Id.Value} status={persisted.Status}; poll with operator-intent-status {persisted.Id} (or add --wait).");
        if (!ConductorLoopLease.IsActive(context.Workspace.OrchestratorDirectory))
            Console.WriteLine(ConductorLoopLease.InactiveWarning);
        return false;
    }

    private static IReadOnlyList<string> WithoutAnswerMetadataFlags(IReadOnlyList<string> parts)
    {
        var filtered = new List<string>();
        for (var index = 0; index < parts.Count; index++)
        {
            if (parts[index] is "--operator-actor" or "--actor-kind" or "--evidence" or
                "--precedent" or "--idempotency-key")
            {
                if (++index >= parts.Count)
                    throw new ArgumentException($"{parts[index - 1]} requires a value.");
                continue;
            }
            filtered.Add(parts[index]);
        }
        return filtered;
    }

    private static IReadOnlyList<string> GetAnswerEvidence(IReadOnlyList<string> parts)
    {
        var evidence = new List<string>();
        for (var index = 0; index < parts.Count - 1; index++)
            if (parts[index].Equals("--evidence", StringComparison.OrdinalIgnoreCase))
                evidence.Add(parts[++index]);
        return evidence;
    }
}
