using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorRefusedGroupedIdentityGuard
{
    internal static bool IsRefused(IEnumerable<ConductorGroupedGateAttempt> records, string kind,
        IReadOnlyList<GateReadyCandidateProjection> members, string mainRevision,
        string treeRevision, string identityValue) => records.Any(record =>
            record.Detail?.StartsWith("refused:identity-changed", StringComparison.Ordinal) == true &&
            string.Equals(record.Kind, kind, StringComparison.Ordinal) &&
            string.Equals(record.MainRevision, mainRevision, StringComparison.Ordinal) &&
            string.Equals(record.CombinedTreeRevision, treeRevision, StringComparison.Ordinal) &&
            string.Equals(record.IdentityValue, identityValue, StringComparison.Ordinal) &&
            record.Members.Count == members.Count &&
            record.Members.Select(member => (member.GoalId, member.CandidateRevision)).ToHashSet()
                .SetEquals(members.Select(member => (member.GoalId.Value, member.CandidateRevision))));
}
