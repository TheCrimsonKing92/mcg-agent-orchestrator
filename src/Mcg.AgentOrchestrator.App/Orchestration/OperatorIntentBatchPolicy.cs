using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public static class OperatorIntentBatchPolicy
{
    public static bool CanShareTick(string verb) => verb is
        OperatorIntentVerbs.CriterionEvidenceMap or
        OperatorIntentVerbs.CriterionEvidenceRecord or
        OperatorIntentVerbs.CriterionEvidenceRepair;

    // answer and approve-policy-change write decision stores; answer also resolves human input.
    // retry reads retry-cause clarification and task lifecycle state changed by sibling intents.
    // adjudicate checks preconditions against the persisted goal version and writes decisions.
    // cancel-dispatch stops a process outside the goal snapshot.
    // verify-manual and progress transition task lifecycle state that other intents read.
    // Unknown and workspace verbs remain isolated until their own contracts prove batching safe.
}
