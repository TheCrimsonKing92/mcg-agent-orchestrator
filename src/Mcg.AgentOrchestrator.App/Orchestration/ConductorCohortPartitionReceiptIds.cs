using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorCohortPartitionReceiptIds
{
    internal static string Create(AcceptanceCohortMemberBinding member, string mainRevision,
        string? treeRevision, string manifestIdentity, AcceptanceAttributionLaneScope? scope)
    {
        var tag = scope is null ? "cohort-partition-v1" : "cohort-lane-scoped-partition-v1";
        var payload = string.Join('\n', tag, member.GoalId.Value, member.CandidateRevision,
            mainRevision, treeRevision ?? "unavailable", manifestIdentity);
        if (scope is not null) payload += $"\n{scope.Sha256}";
        return $"{tag}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))}";
    }
}
