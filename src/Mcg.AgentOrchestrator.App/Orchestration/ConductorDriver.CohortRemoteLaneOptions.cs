using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal static AcceptanceRunExecutionOptions CreateCohortGateExecutionOptions(
        ConductEventLogWriter writer,
        AcceptanceCohortIdentity identity,
        IReadOnlyList<AcceptanceCohortMemberBinding> bindings,
        string? projectHomeDirectory = null) => new(
            ProgressSink: progress => AppendCohortGateProgressEvents(writer, identity, bindings, progress),
            RemoteLaneEventSink: detail => AppendRemoteLaneEvent(writer, bindings.Count == 1 ? bindings[0].GoalId.Value[..8] : null, detail),
            OwnerProtectedCohortMembers: bindings.Select(binding =>
                new AcceptanceOwnerProtectedCohortMember(binding.GoalId, binding.CandidateRevision)).ToArray(),
            GateRunIdentity: CohortGateRunIdentity(bindings.Select(binding => binding.GoalId.Value)),
            CohortRemoteLanes: true,
            ProjectHomeDirectory: projectHomeDirectory);
}
