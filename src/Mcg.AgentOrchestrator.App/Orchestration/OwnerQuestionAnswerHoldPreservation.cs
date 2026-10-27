using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class OwnerQuestionAnswerHoldPreservation
{
    internal static bool ShouldPreserve(GoalHoldState? hold, OperatorIntentRecord intent)
    {
        if (!ConductorOwnerQuestionHolds.ExcludesFromWalk(hold?.State) ||
            intent.Verb != OperatorIntentVerbs.Answer)
            return false;

        var answer = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)
            ?? throw new InvalidOperationException($"Answer intent '{intent.Id}' has an empty payload.");
        // Match the entire target token in the hold's own blocker, not an id prefix or question text.
        // Steward trigger holds have no answer target, so an answer cannot resolve those holds.
        return !hold!.Blocker.StartsWith(
            $"{hold.State} item={answer.TargetKind}:{answer.TargetId} ", StringComparison.Ordinal);
    }
}
