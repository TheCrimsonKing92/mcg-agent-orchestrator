using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private bool TryDecideLifecycleEntry(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, GoalLifecycleState state,
        out ConductorAdvanceResult result, out LifecycleEntryDecision decision)
    {
        var facts = new LifecycleEntryFacts(state) { PolicyName = policy.Name, StateIsFailed = state == GoalLifecycleState.Failed };
        if (state is GoalLifecycleState.Created or GoalLifecycleState.WorkspaceReady or GoalLifecycleState.Dispatched)
            facts = facts with { SliceBatchParentHold = SliceBatchParentExecutionGuard?.TryDescribeHold(goal) ?? string.Empty };

        // Gather only inputs reached by the original cascade; later builders and policy lookups stay uncalled.
        if (facts.SliceBatchParentHold.Length == 0 && facts.StateIsFailed != true)
        {
            if (state == GoalLifecycleState.AwaitingClarification)
                facts = facts with { AwaitingClarificationReason = _tryBuildAwaitingClarificationEscalationReason(goal) ?? string.Empty };
            else if (state is GoalLifecycleState.Blocked or GoalLifecycleState.AwaitingHumanInput)
                facts = facts with { TerminalEscalationReason = BuildTerminalEscalationReason(goal, state) };
            // Merged's transition map is the pre-landing risk gate, not the post-landing record gate.
            else if (state != GoalLifecycleState.Merged)
                facts = facts with { TransitionDecision = policy.GetTransitionDecision(state).ToString() };
        }

        decision = LifecycleEntryPolicy.Evaluate(facts);
        if (decision.Action == LifecycleEntryAction.Hold)
        {
            result = MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(state, decision.Reason) { Decision = decision.ToRecord() });
            return true;
        }
        if (decision.Action == LifecycleEntryAction.Escalate)
        {
            result = Escalate(goal, goalPrefix, policy, state, decision.Reason);
            if (result.Outcome is ConductorAdvanceOutcome.Escalated escalated)
                result = result with { Outcome = escalated with { Decision = decision.ToRecord() } };
            return true;
        }

        result = null!;
        return false;
    }

    private ConductorAdvanceResult ExecuteCreateWorkspace(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        var execution = CreatedWorkspaceExecutor.Execute(() => _createWorkspace(goal));
        var facts = execution.LeaseUnavailableMessage is not null
            ? new CreatedStageFacts { LeaseUnavailableMessage = execution.LeaseUnavailableMessage }
            : new CreatedStageFacts { CreatedPath = execution.CreatedPath! };

        var decision = CreatedStagePolicy.Evaluate(facts);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            decision.Action == CreatedStageAction.Hold
                ? new ConductorAdvanceOutcome.Held(GoalLifecycleState.Created, decision.Reason) { Decision = decision.ToRecord() }
                : new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Created, decision.Reason));
    }
}
