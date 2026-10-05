using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal delegate Task<bool> ClarificationAnswerResolver(string correlationKey, string answer, int briefVersion);
internal delegate Task<bool> ClarificationAnswerRecovery(string correlationKey, string answer, int briefVersion);

internal sealed class AnswerApplicationRetryException(string message, Exception innerException)
    : Exception(message, innerException);

internal sealed partial class OperatorIntentCoordinator
{
    private void ApplyAnswer(AgentOrchestratorKernel kernel, Goal goal, OperatorIntentRecord intent)
    {
        var payload = Deserialize<AnswerOperatorIntentPayload>(intent);
        if (string.IsNullOrWhiteSpace(payload.Text))
            throw new InvalidOperationException("Answer text is empty.");
        if (string.IsNullOrWhiteSpace(payload.TargetId))
            throw new InvalidOperationException("Answer target id is empty.");
        if (!string.Equals(payload.GoalId, intent.GoalId, StringComparison.Ordinal) ||
            !string.Equals(payload.GoalId, goal.Id.Value, StringComparison.Ordinal))
            throw new InvalidOperationException("Answer target belongs to a different goal.");
        if (payload.ActorKind != intent.ActorKind)
            throw new InvalidOperationException("Answer actor kind does not match intent actor kind.");
        var decisions = _decisions ?? throw new InvalidOperationException("Answer decision store is not configured.");
        var priorDecision = decisions.GetDecisionStateAsync($"answer-{intent.Id}").GetAwaiter().GetResult();
        if (priorDecision?.Receipt is { } priorReceipt)
        {
            var replayRequest = payload.TargetKind == OperatorAnswerTargetKind.HumanInput
                ? kernel.HumanInputRequests.FirstOrDefault(candidate =>
                    candidate.Id.Value == payload.TargetId && candidate.GoalId == goal.Id)
                : null;
            if (payload.TargetKind == OperatorAnswerTargetKind.HumanInput && replayRequest is null)
                throw new KeyNotFoundException($"Human-input request '{payload.TargetId}' was not found for goal '{goal.Id.Value}'.");
            if (replayRequest?.TaskId is { } replayTaskId && !goal.Tasks.Any(task => task.Id == replayTaskId))
                throw new InvalidOperationException($"Human-input request '{payload.TargetId}' no longer has a waiting task.");
            try
            {
                if (replayRequest is { IsCompleted: false })
                    kernel.SubmitHumanInput(replayRequest.Id, payload.Text);
                var effect = decisions.TryApplyDecisionEffectAsync(priorDecision.Request.Id,
                    priorReceipt.Id, priorReceipt.Response.ActionRef, null,
                    $"answered {payload.TargetKind}:{payload.TargetId}", _utcNow()).GetAwaiter().GetResult();
                if (!effect.Applied)
                    throw new InvalidOperationException($"Answer decision effect was rejected: {effect.Receipt.Result}");
                RecordAnswerTimeline(kernel, goal, intent, payload, priorReceipt);
            }
            catch (Exception ex)
            {
                throw new AnswerApplicationRetryException("Answer replay could not finish; the intent remains claimed.", ex);
            }
            return;
        }

        var firstLine = payload.Text.ReplaceLineEndings("\n").Trim().Split('\n')[0].Trim();
        if (string.Equals(firstLine, FrozenFactRuling.Header, StringComparison.Ordinal) &&
            FrozenFactRuling.TryParse(payload.Text) is null)
            throw new InvalidOperationException("frozen-fact-ruling-malformed: answer must contain every structured ruling field.");

        switch (payload.TargetKind)
        {
            case OperatorAnswerTargetKind.Clarification:
                var item = decisions.ListAsync(goal.Id.Value).GetAwaiter().GetResult()
                    .FirstOrDefault(candidate => candidate.Id == payload.TargetId);
                if (item is null || string.IsNullOrWhiteSpace(item.CorrelationKey) ||
                    !item.CorrelationKey.StartsWith("spec-clarification:", StringComparison.Ordinal))
                    throw new KeyNotFoundException($"Clarification '{payload.TargetId}' was not found for goal '{goal.Id.Value}'.");
                if (CollaborationItemLifecycle.IsTerminal(item.Status))
                {
                    if (item.ResolvedAt is not { } resolvedAt || resolvedAt < intent.ClaimedAt ||
                        item.AuthoritativeAnswer is not { } authoritativeAnswer ||
                        authoritativeAnswer.BriefVersion is not { } answerBriefVersion ||
                        !string.Equals(authoritativeAnswer.Text, payload.Text, StringComparison.Ordinal))
                        throw new InvalidOperationException($"Clarification '{payload.TargetId}' is already answered — use supersede.");
                    try
                    {
                        if (!(_clarificationAnswerRecovery ?? throw new InvalidOperationException(
                            "Clarification answer recovery is not configured."))
                            (item.CorrelationKey, payload.Text, answerBriefVersion).GetAwaiter().GetResult())
                            throw new InvalidOperationException("Resolved clarification answer could not be recovered.");
                    }
                    catch (Exception ex)
                    {
                        throw new AnswerApplicationRetryException("Clarification recovery could not finish; the intent remains claimed.", ex);
                    }
                    break;
                }
                bool accepted;
                try
                {
                    accepted = (_clarificationAnswers ?? throw new InvalidOperationException(
                        "Clarification answer resolver is not configured."))
                        (item.CorrelationKey, payload.Text, goal.AuthoritativeBrief.Version).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    throw new AnswerApplicationRetryException("Clarification resolution could not finish; the intent remains claimed.", ex);
                }
                if (!accepted)
                    throw new InvalidOperationException($"Clarification '{payload.TargetId}' answer was not accepted.");
                break;
            case OperatorAnswerTargetKind.HumanInput:
                var request = kernel.HumanInputRequests.FirstOrDefault(candidate => candidate.Id.Value == payload.TargetId);
                if (request is null || request.GoalId != goal.Id)
                    throw new KeyNotFoundException($"Human-input request '{payload.TargetId}' was not found for goal '{goal.Id.Value}'.");
                if (request.IsCompleted)
                {
                    if (request.AnsweredAt is not { } answeredAt || answeredAt < intent.ClaimedAt ||
                        !string.Equals(request.Answer, payload.Text.Trim(), StringComparison.Ordinal))
                        throw new InvalidOperationException($"Human-input request '{payload.TargetId}' is already answered.");
                    break;
                }
                if (request.TaskId is { } requestTaskId && !goal.Tasks.Any(task => task.Id == requestTaskId))
                    throw new InvalidOperationException($"Human-input request '{payload.TargetId}' no longer has a waiting task.");
                try { kernel.SubmitHumanInput(request.Id, payload.Text); }
                catch (Exception ex)
                {
                    throw new AnswerApplicationRetryException("Human-input resolution could not finish; the intent remains claimed.", ex);
                }
                break;
            default:
                throw new InvalidOperationException($"Unsupported answer target kind '{payload.TargetKind}'.");
        }

        try
        {
            var receipt = RecordAnswerDecision(decisions, goal, intent, payload);
            RecordAnswerTimeline(kernel, goal, intent, payload, receipt);
        }
        catch (Exception ex)
        {
            throw new AnswerApplicationRetryException("Answer decision could not finish; the intent remains claimed.", ex);
        }
    }

    private static void RecordAnswerTimeline(
        AgentOrchestratorKernel kernel, Goal goal, OperatorIntentRecord intent,
        AnswerOperatorIntentPayload payload, DecisionReceipt receipt)
    {
        kernel.RecordOperatorIntentApplied(goal.Id, intent.Id, intent.Verb, null, intent.Actor, intent.Channel,
            intent.AuthenticationAssurance,
            $"{BuildApplicationMarker(intent)} verb=answer target={payload.TargetKind}:{payload.TargetId}",
            intent.ActorKind, decisionId: receipt.Id, outcome: "applied");
    }

    private DecisionReceipt RecordAnswerDecision(
        ICollaborationItemStore decisions, Goal goal, OperatorIntentRecord intent, AnswerOperatorIntentPayload payload)
    {
        var now = _utcNow();
        var requestId = $"answer-{intent.Id}";
        var action = new DecisionActionRef($"answer.{intent.Id}");
        var evidence = (payload.EvidenceReferences ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Select(value => new EvidenceManifestEntry(value,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()))
            .ToArray();
        decisions.RaiseDecisionRequestAsync(new DecisionRequest(
            requestId, DecisionRequestKind.General, goal.Id.Value,
            $"Answer {payload.TargetKind} {payload.TargetId}", payload.Text.Trim(), "operator-answer-v1",
            EvidenceManifest.Create(evidence), now.AddHours(24), DecisionDefaultDisposition.NoAction,
            new DecisionBlockingImpact("The answer remains open until the conductor applies it.", []),
            [DecisionReuseScope.ThisOccurrence],
            [new DecisionAllowedAction(action, "answer", DecisionActionKind.Clarify,
                AuthorizationTier.Answer, now.AddHours(24), null)], now)).GetAwaiter().GetResult();
        var receipt = decisions.RecordDecisionAsync(requestId,
            OperatorActorIdentity.Format(intent.Actor, intent.ActorKind), intent.Channel,
            OperatorIntentAdjudication.ResolveAuthorizationTier(intent.AuthenticationAssurance), null,
            new DecisionResponse(action, payload.Text.Trim(), DecisionReuseScope.ThisOccurrence, false),
            now, DecisionReversibility.ReversibleWithCost,
            string.IsNullOrWhiteSpace(payload.Precedent) ? null : payload.Precedent.Trim()).GetAwaiter().GetResult();
        var effect = decisions.TryApplyDecisionEffectAsync(requestId, receipt.Id, action, null,
            $"answered {payload.TargetKind}:{payload.TargetId}", now).GetAwaiter().GetResult();
        if (!effect.Applied)
            throw new InvalidOperationException($"Answer decision effect was rejected: {effect.Receipt.Result}");
        return receipt;
    }

    // A claimed retry can be waiting for this answer. Claim answers by verb so it cannot hide them.
    private bool TryApplyQueuedAnswer(AgentOrchestratorKernel kernel, Goal goal, List<string> lines, out bool mutated)
    {
        mutated = false;
        var answer = _store.ClaimNextByVerbAsync(goal.Id.Value, OperatorIntentVerbs.Answer, ClaimOwner)
            .GetAwaiter().GetResult();
        if (answer is null) return false;
        if (goal.Timeline.Any(item => item.OperatorIntentApplied?.IntentId == answer.Id))
        {
            _store.CompleteAsync(answer.Id, ClaimOwner, OperatorIntentStatus.Applied,
                $"Applied; recovered durable answer intent {answer.Id}.", _utcNow()).GetAwaiter().GetResult();
            lines.Add($"OPERATOR_INTENT id={answer.Id} verb=answer goal={goal.Id.Value[..8]} result=applied-recovered");
            return true;
        }
        try
        {
            ApplyAnswer(kernel, goal, answer);
            AddPendingCompletion(goal.Id.Value, answer.Id, $"Applied answer to goal {goal.Id.Value[..8]}.");
            lines.Add($"OPERATOR_INTENT id={answer.Id} verb=answer goal={goal.Id.Value[..8]} result=applied-pending-commit");
            mutated = true;
        }
        catch (AnswerApplicationRetryException ex)
        {
            mutated = true;
            lines.Add($"OPERATOR_INTENT id={answer.Id} verb=answer goal={goal.Id.Value[..8]} result=retryable reason={Sanitize(ex.Message)}");
        }
        catch (Exception ex)
        {
            _store.CompleteAsync(answer.Id, ClaimOwner, OperatorIntentStatus.Rejected,
                $"Rejected answer: {Sanitize(ex.Message)}", _utcNow()).GetAwaiter().GetResult();
            lines.Add($"OPERATOR_INTENT id={answer.Id} verb=answer goal={goal.Id.Value[..8]} result=rejected reason={Sanitize(ex.Message)}");
        }
        return true;
    }
}
