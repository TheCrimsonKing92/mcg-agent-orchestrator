using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Planning recommendations and evidence requests; this renderer grants no execution authority.
internal static class PlanningBrokerGuidance
{
    internal static IReadOnlyList<string> BuildAndPolicyLines(
        Toolchain toolchain, RepositoryTestImpactPlan impact, VerificationPolicy policy, bool usesDurableResearch)
    {
        var contextArtifact = usesDurableResearch ? "research-notes.md" : "source-survey.md";
        var selection = toolchain switch
        {
            Toolchain.Dotnet => ".NET project builds and focused test classes selected from changed scope",
            Toolchain.Go => "Go package build, vet, and focused package tests",
            Toolchain.Node => "Node build, lint, and test scripts declared by the package and CI configuration",
            Toolchain.Python => "Python project build, lint, type checks, and focused tests declared by project and CI configuration",
            _ => "project-standard build, policy checks, and tests discovered from repository conventions and CI configuration"
        };
        var lines = new List<string>
        {
            "- build-test-selection",
            $"  Artifact: current-task.md plus {contextArtifact}",
            "  Use for: recommending focused tests and required checks from task scope and supplied evidence, without executing them.",
            $"  Selection context: Recommend {selection} to Acceptance where applicable.",
            $"  Current recommendation: {impact.Summary}",
            $"  Selection scope: build needed={impact.RequiresBuild}; broad verification needed={impact.RequiresBroadVerification}.",
            "  Execution handoff: Acceptance owns deterministic build/test execution and receipts. Identify proposed selections and requested evidence; unresolved selections or unavailable owners remain explicit planning issues.",
            "  Failure handling: report supplied failing checks with their command and exit evidence; unexecuted recommendations remain pending evidence requests.",
            "- static-policy-checks",
            $"  Artifact: current-task.md plus {contextArtifact}",
            "  Use for: planning required verification policy, generated-path risk, worker-result contract checks, and manifest presence from task scope and supplied evidence.",
            $"  Current policy: {policy.Summary}",
            $"  Required review: human risk review={policy.RequiresHumanReview}; deterministic tests={policy.RequiresTests}.",
            "  Policy handoff: Acceptance owns deterministic policy checks. Required human review stays with its existing owner; Operator owns external observations only when the criterion declares them."
        };
        foreach (var check in policy.Checks)
            lines.Add($"  Policy requirement: {check.Name}; kind={check.Kind}; required={check.Required}; reason={check.Reason}");
        lines.Add("  Failure handling: supplied required policy failures block acceptance until resolved; missing policy evidence remains an explicit request, never an assumed pass.");
        return lines;
    }

    internal static IReadOnlyList<string> DiffSummaryLines(bool usesDurableResearch) =>
    [
        "- diff-summary",
        $"  Artifact: current-task.md plus {(usesDurableResearch ? "research-notes.md" : "source-survey.md")}",
        "  Use for: planning changed-file scope and generated-output noise review using supplied research or source survey, supplied change evidence, and permitted read-only workspace inspection.",
        "  Failure handling: report unexpected dirty files or missing change evidence as unresolved scope issues; do not infer a clean workspace from absent evidence."
    ];

    internal static IReadOnlyList<string> AcceptanceEvidenceLines() =>
    [
        "- acceptance-evidence",
        "  Artifact: current-task.md",
        "  Use for: planning acceptance manifest requirements, reporting supplied verification history, model-fit evidence, and WORKER_RESULT completeness.",
        "  Evidence handoff: Acceptance supplies deterministic build/test and gate receipts. Request missing receipts and identify pending checks; recommendations are not completed verification or an acceptance verdict.",
        "  Failure handling: missing acceptance evidence remains an explicit evidence request to Acceptance; external observations go to Operator only when assigned by the criterion."
    ];

    internal static IReadOnlyList<string> BacklogEvidenceLines() =>
    [
        "- backlog-log-evidence",
        "  Artifact: current-task.md",
        "  Store context: `.orchestrator/dogfood-log.db` is the durable dogfood evidence source (descriptive metadata, not a required delivered planning artifact).",
        "  Use for: reporting supplied backlog and model-fit evidence and proposing follow-ups at goal boundaries.",
        "  Execution handoff: request durable backlog/log maintenance from the Operator; use only supplied records when planning.",
        "  Failure handling: identify missing records as evidence requests instead of inventing goal history or executing maintenance."
    ];
}
