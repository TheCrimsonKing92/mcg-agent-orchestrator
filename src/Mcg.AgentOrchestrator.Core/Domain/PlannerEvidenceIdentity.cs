using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Core;

// Only the host binds dispatch provenance. Workers provide the criterion and key.
public sealed record PlannerEvidenceIdentity(
    string GoalId, string RequestingTaskId, int BriefVersion, int CriterionIndex, string EvidenceKey)
{
    public string StableId => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        System.Text.Json.JsonSerializer.Serialize(new object[]
            { GoalId, RequestingTaskId, BriefVersion, CriterionIndex, EvidenceKey })))).ToLowerInvariant();
}

public sealed record PlannerEvidencePayload(
    int CriterionIndex, string EvidenceKey, string Availability, string? Owner,
    string Needed, string Reason, string? Store, PlannerEvidenceStoreReference? StoreReference,
    IReadOnlyList<string>? Selectors)
{
    public bool SemanticallyEquals(PlannerEvidencePayload? other) => other is not null &&
        CriterionIndex == other.CriterionIndex && EvidenceKey == other.EvidenceKey &&
        Availability == other.Availability && Owner == other.Owner && Needed == other.Needed &&
        Reason == other.Reason && Store == other.Store && StoreReference == other.StoreReference &&
        (Selectors ?? []).SequenceEqual(other.Selectors ?? [], StringComparer.Ordinal);
}
