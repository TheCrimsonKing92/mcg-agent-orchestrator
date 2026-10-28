using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Renders reporting guidance for native Developers; delivery and execution authority stay with callers.
internal static class NativeDeveloperBrokerGuidance
{
    internal static IReadOnlyList<string> BuildAndPolicyLines(
        RepositoryTestImpactPlan testImpactPlan, VerificationPolicy verificationPolicy,
        string useFor, string commandShape, string localChecks, string recommendationSource)
    {
        var lines = new List<string>
        {
            "- build-test-selection",
            "  Artifact: current-task.md plus source-survey.md",
            $"  Use for: {useFor}",
            $"  Suggested command shape: `{commandShape}`",
            $"  Local checks: {localChecks}",
            $"  Current recommendation: {testImpactPlan.Summary}",
            $"  Recommendation source: generated test-impact plan; {recommendationSource}",
            "  Evidence: Record each permitted local command, result and failure in WORKER_RESULT; leave unrun checks explicitly pending.",
            "  Failure handling: failing required checks are actionable verification failures; include the command and exit evidence.",
            "- static-policy-checks",
            "  Artifact: source-survey.md plus diff-summary.md",
            "  Use for: comparing changed files with the surveyed scope, checking generated-path risk and worker-result completeness, and reporting applicable policy requirements and evidence gaps.",
            $"  Current policy: {verificationPolicy.Summary}",
            "  Policy source: generated verification policy. Gate checks and required human review remain with their existing owners; report their status or missing evidence without expanding local execution permissions."
        };
        foreach (var check in verificationPolicy.Checks)
            lines.Add($"  Policy check: {check.Name}; kind={check.Kind}; required={check.Required}; reason={check.Reason}");
        lines.Add("  Failure handling: required policy failures block acceptance until fixed or explicitly explained.");
        return lines;
    }

    internal static IReadOnlyList<string> AcceptanceEvidenceLines(string commandResults, string gateChecks) =>
    [
        "- acceptance-evidence",
        "  Artifact: current-task.md plus diff-summary.md",
        $"  Use for: reporting {commandResults} against the assigned task and changed files, model-fit evidence, WORKER_RESULT completeness, and pending acceptance manifest or verification evidence.",
        "  Evidence: Record commands, exit results, failures and missing evidence; never report unrun checks as passed.",
        $"  Verification ownership: Local command-result reporting does not authorize a formal acceptance verdict. The acceptance gate re-runs {gateChecks} and decides the verdict; required review remains with its existing owner.",
        "  Failure handling: missing acceptance evidence should become an explicit blocker or a verify command, not a silent pass."
    ];
}
