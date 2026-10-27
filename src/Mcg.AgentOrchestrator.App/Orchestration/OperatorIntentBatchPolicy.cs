using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public static class OperatorIntentBatchPolicy
{
    public static bool CanShareTick(string verb) => verb is
        OperatorIntentVerbs.CriterionEvidenceMap or
        OperatorIntentVerbs.CriterionEvidenceRecord or
        OperatorIntentVerbs.CriterionEvidenceRepair;

    internal static bool IsClarificationAnswer(OperatorIntentRecord intent)
    {
        if (intent.Verb != OperatorIntentVerbs.Answer)
            return false;

        try
        {
            return JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)
                ?.TargetKind == OperatorAnswerTargetKind.Clarification;
        }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    internal static bool CanJoinPass(IReadOnlyList<string> appliedVerbs, OperatorIntentRecord intent) =>
        appliedVerbs.Count == 0 ||
        (CanShareTick(intent.Verb) && appliedVerbs.All(CanShareTick)) ||
        (IsClarificationAnswer(intent) && appliedVerbs.All(verb => verb == OperatorIntentVerbs.Answer));

    // Clarification answers may share a pass because each writes rows keyed by its own item or intent.
    // retry reads retry-cause clarification and task lifecycle state changed by sibling intents.
    // adjudicate checks preconditions against the persisted goal version and writes decisions.
    // cancel-dispatch stops a process outside the goal snapshot.
    // verify-manual and progress transition task lifecycle state that other intents read.
    // Unknown and workspace verbs remain isolated until their own contracts prove batching safe.
}
