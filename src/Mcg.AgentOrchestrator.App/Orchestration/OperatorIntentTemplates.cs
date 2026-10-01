using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record OperatorIntentTemplate(
    string Name,
    string Description,
    IReadOnlyList<string> DecompositionRules,
    IReadOnlyList<string> RequiredEvidence,
    IReadOnlyList<string> VerificationPolicy,
    IReadOnlyList<string> DeterministicWorkflows);

internal sealed record OperatorIntentPlan(
    OperatorIntentTemplate Template,
    string Request,
    string ReadyObjective);

internal static class OperatorIntentTemplates
{
    private static readonly OperatorIntentTemplate[] Templates =
    [
        new(
            "feature",
            "Implement a product or workflow feature.",
            [
                "Identify source, API/CLI/dashboard, and test surfaces before editing.",
                "Split independent file scopes into separate tasks; serialize shared files.",
                "Prefer deterministic planners, brokers, or DTO mappers over prompt-only behavior."
            ],
            [
                "Changed files and behavior summary.",
                "Worker result contract or operator implementation evidence.",
                "Acceptance evidence bundle before merge."
            ],
            [
                "Run focused tests for changed surfaces.",
                "Broaden to dashboard/API/worker slices when shared contracts change.",
                "Reviewer verifies source diff, generated artifacts, and backlog/log updates."
            ],
            [
                "goal-plan or backlog-intake when the request maps to backlog work.",
                "workspace create before file work.",
                "acceptance and retention-plan after verification."
            ]),
        new(
            "bugfix",
            "Fix a reproducible failure or regression.",
            [
                "Start with the failing command, log line, or user-visible symptom.",
                "Classify root cause before changing code.",
                "Keep the patch scoped to the failing behavior."
            ],
            [
                "Failing evidence before fix.",
                "Passing regression after fix.",
                "Risk note for adjacent behavior."
            ],
            [
                "Add or update a regression test when feasible.",
                "Run the original failing command after the fix.",
                "Use failure-triage when dispatch, verification, or build failures are involved."
            ],
            [
                "failure-triage for known failure classes.",
                "Invoke-IsolatedDotnet.ps1 or brokered dotnet verification for build/test work.",
                "goal-recovery if interrupted state exists."
            ]),
        new(
            "refactor",
            "Change structure while preserving behavior.",
            [
                "Name the behavior that must remain unchanged.",
                "Avoid unrelated rewrites and generated churn.",
                "Prefer existing abstractions and flat Core/Infrastructure namespaces."
            ],
            [
                "Before/after API or behavior equivalence note.",
                "Changed public surface list.",
                "Focused tests proving preserved behavior."
            ],
            [
                "Run tests around every touched contract.",
                "Broaden when shared models, DTOs, or command parsing changes.",
                "Reviewer checks for accidental semantic changes."
            ],
            [
                "source-survey for current ownership.",
                "deterministic test-impact selection.",
                "acceptance evidence before merge."
            ]),
        new(
            "test-hardening",
            "Improve reliability, isolation, or coverage of tests.",
            [
                "Identify the flaky, missing, or over-broad test boundary.",
                "Avoid hiding product failures with looser assertions.",
                "Prefer deterministic fixtures over sleeps or broad process cleanup."
            ],
            [
                "Original failure or coverage gap.",
                "New deterministic assertion.",
                "Any build-lock or environment assumption."
            ],
            [
                "Run the focused test repeatedly when fixing flake.",
                "Use isolated build artifacts for dotnet lock issues.",
                "Document residual risk if a broad suite is not run."
            ],
            [
                "dotnet build-server shutdown only after file-lock evidence.",
                "Invoke-IsolatedDotnet.ps1 for goal-scoped dotnet runs.",
                "failure-triage for verification failures."
            ]),
        new(
            "skill-authoring",
            "Create or update repo-scoped worker skills.",
            [
                "Identify trigger terms and exact worker behavior before authoring.",
                "Keep SKILL.md concise and task-actionable.",
                "Confirm subscription worker writability before delegating edits."
            ],
            [
                "Skill path and frontmatter.",
                "Trigger examples.",
                "Worker result evidence showing skill usage when dogfooded."
            ],
            [
                "Validate SKILL.md frontmatter and routing terms.",
                "Run worker context artifact tests when skill selection changes.",
                "Dogfood a worker only after local source evidence is ready."
            ],
            [
                "selected-skills.md artifact inspection.",
                "worker-profile-check for permission mode.",
                "retention-plan after skill dogfood goals."
            ]),
        new(
            "release-prep",
            "Prepare a safe operator-facing release or merge batch.",
            [
                "Collect completed goals and unresolved gates first.",
                "Serialize acceptance and cleanup.",
                "Avoid mixing product changes with log/backlog-only cleanup."
            ],
            [
                "Acceptance queue output.",
                "Focused and broadened test evidence.",
                "Backlog delta and SQLite dogfood-log evidence."
            ],
            [
                "Run acceptance-queue dry run before apply.",
                "Run retention-plan for accepted or abandoned goals.",
                "Use git history and SQLite durability for state recovery."
            ],
            [
                "acceptance-queue.",
                "retention-plan.",
                "goal-recovery for interrupted goals."
            ])
    ];

    public static IReadOnlyList<OperatorIntentTemplate> All => Templates;

    public static OperatorIntentPlan Build(string templateName, string request)
    {
        var template = Templates.FirstOrDefault(item => item.Name.Equals(templateName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown intent template '{templateName}'. Available: {string.Join(", ", Templates.Select(item => item.Name))}.");
        if (string.IsNullOrWhiteSpace(request))
        {
            throw new ArgumentException("Intent request cannot be empty.", nameof(request));
        }

        return new OperatorIntentPlan(
            template,
            request.Trim(),
            BuildObjective(template, request.Trim()));
    }

    private static string BuildObjective(OperatorIntentTemplate template, string request)
    {
        return string.Join(Environment.NewLine, [
            $"Intent template: {template.Name}",
            $"Request: {request}",
            string.Empty,
            "Decomposition rules:",
            .. template.DecompositionRules.Select(item => $"- {item}"),
            string.Empty,
            "Required evidence:",
            .. template.RequiredEvidence.Select(item => $"- {item}"),
            string.Empty,
            "Verification policy:",
            .. template.VerificationPolicy.Select(item => $"- {item}"),
            string.Empty,
            "Deterministic workflows:",
            .. template.DeterministicWorkflows.Select(item => $"- {item}")
        ]);
    }
}
