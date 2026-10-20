namespace Mcg.AgentOrchestrator.Core;

/// <summary>Executed candidate failures awaiting baseline attribution, retained with the failed acceptance.</summary>
public sealed record AcceptanceAttributionPending(
    string CandidateSha, string MainSha, string AttemptId,
    IReadOnlyList<AcceptanceAttributionPendingTest> Tests, string OtherEvidenceKind)
{
    public const string EvidenceKind = "acceptance-attribution-pending";

    [System.Text.Json.Serialization.JsonIgnore]
    public string Identity => $"{EvidenceKind}:{CandidateSha}:{MainSha}:{AttemptId}:" +
        string.Join(";", Tests.Select(test => $"{test.CheckName}:{test.TestIdentity}").Order(StringComparer.Ordinal));
}

public sealed record AcceptanceAttributionPendingTest(
    string CheckName, string TestIdentity, string BaselineEvidence,
    IReadOnlyList<AcceptanceAttributionPendingReceipt> FirstReceipts,
    IReadOnlyList<AcceptanceAttributionPendingReceipt> RerunReceipts);

public sealed record AcceptanceAttributionPendingReceipt(string Path, string Sha256, string RunId);
