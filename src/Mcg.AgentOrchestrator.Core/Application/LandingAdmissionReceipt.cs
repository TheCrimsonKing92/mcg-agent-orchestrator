namespace Mcg.AgentOrchestrator.Core;

public sealed record LandingAdmissionReceipt(
    string AdmissionRule,
    string CandidateSha,
    string DenylistSource,
    IReadOnlyList<string> DenylistMatches)
{
    public const string GreenGateAuto = "green-gate-auto";
    public const string DenylistMatchRecorded = "denylist-match-recorded";
    public const string RecoveredLanding = "recovered-landing";
    public const string BuiltInDefaultSource = "built-in-default";
    public const string NotEvaluatedSource = "not-evaluated";

    public static LandingAdmissionReceipt Recovered(string candidateSha) =>
        new(RecoveredLanding, candidateSha, NotEvaluatedSource, []);

    public static LandingAdmissionReceipt Evaluated(string candidateSha, string source, IReadOnlyList<string> matches) =>
        new(matches.Count == 0 ? GreenGateAuto : DenylistMatchRecorded, candidateSha, source, matches);
}
