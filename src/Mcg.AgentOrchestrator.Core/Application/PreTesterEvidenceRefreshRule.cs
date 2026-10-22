namespace Mcg.AgentOrchestrator.Core;

public enum PreTesterEvidenceRefreshDecision { Reuse, Refresh, EscalateRepeat }

public sealed record PreTesterEvidenceRefreshMarker(
    string CandidateSha, string SupersededReceiptId, string FreshReceiptId);

/// <summary>One apparatus refresh is allowed per Tester and candidate, across retries and restarts.</summary>
public static class PreTesterEvidenceRefreshRule
{
    private const string Prefix = "finding-evidence pre-tester-refresh ";

    public static PreTesterEvidenceRefreshDecision DecideBuildPhaseRefresh(PreTesterEvidenceEntry? prior,
        bool priorApparatusUnusable, bool priorBuildPhaseOnly, int refreshesForCandidate) =>
        prior?.Outcome == "unusable" && priorApparatusUnusable && priorBuildPhaseOnly && refreshesForCandidate == 0
            ? PreTesterEvidenceRefreshDecision.Refresh : PreTesterEvidenceRefreshDecision.Reuse;

    public static PreTesterEvidenceRefreshDecision Decide(PreTesterEvidenceEntry? prior,
        bool priorApparatusUnusable, RetryCause pendingTesterRetryCause, int refreshesForCandidate) =>
        prior?.Outcome == "unusable" && priorApparatusUnusable &&
        pendingTesterRetryCause == RetryCause.EnvironmentApparatusFailure && refreshesForCandidate == 0
            ? PreTesterEvidenceRefreshDecision.Refresh : PreTesterEvidenceRefreshDecision.Reuse;

    public static PreTesterEvidenceRefreshDecision DecideAfterRun(
        PreTesterEvidenceRefreshMarker? marker, string receiptId, string outcome) =>
        marker?.FreshReceiptId == receiptId && outcome == "unusable"
            ? PreTesterEvidenceRefreshDecision.EscalateRepeat : PreTesterEvidenceRefreshDecision.Reuse;

    public static string FormatMarker(PreTesterEvidenceRefreshMarker marker) =>
        Prefix + $"candidate_sha={marker.CandidateSha}; superseded_receipt_id={marker.SupersededReceiptId}; " +
        $"fresh_receipt_id={marker.FreshReceiptId}";

    public static bool TryParse(string message, out PreTesterEvidenceRefreshMarker? marker)
    {
        marker = null;
        if (!message.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var fields = message[Prefix.Length..].Split(';', StringSplitOptions.TrimEntries);
        string[] keys = ["candidate_sha=", "superseded_receipt_id=", "fresh_receipt_id="];
        if (fields.Length != keys.Length || fields.Where((field, i) =>
                !field.StartsWith(keys[i], StringComparison.Ordinal) || field.Length == keys[i].Length).Any())
            return false;
        marker = new(fields[0][keys[0].Length..], fields[1][keys[1].Length..], fields[2][keys[2].Length..]);
        return true;
    }

    private static IEnumerable<PreTesterEvidenceRefreshMarker> ForCandidate(Goal goal, TaskId testerId,
        string candidateSha) => goal.Timeline
        .Where(evt => evt.TaskId == testerId && evt.Kind == ProgressKind.FindingEvidenceRequestRecorded)
        .Select(evt => TryParse(evt.Message, out var marker) ? marker : null)
        .OfType<PreTesterEvidenceRefreshMarker>()
        .Where(marker => string.Equals(marker.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase));

    public static int CountForCandidate(Goal goal, TaskId testerId, string candidateSha) =>
        ForCandidate(goal, testerId, candidateSha).Count();

    public static PreTesterEvidenceRefreshMarker? LatestForCandidate(Goal goal, TaskId testerId,
        string candidateSha) => ForCandidate(goal, testerId, candidateSha).LastOrDefault();

    public static string DescribeRepeat(string candidateSha, string supersededReceiptId, string freshReceiptId) =>
        $"PRE_TESTER_APPARATUS_REFRESH_UNUSABLE: candidate_sha={candidateSha}; " +
        $"superseded_receipt_id={supersededReceiptId}; fresh_receipt_id={freshReceiptId}; " +
        "the single fresh pre-Tester evidence run is unusable; operator intervention is required.";
}
