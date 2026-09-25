namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed partial class WorkerArtifactWriter
{
    private static IReadOnlyList<string> BuildScoutArtifactPriorities() =>
    [
        "- 1. artifact-registry.json: confirm available artifacts, hashes, and freshness before opening content.",
        "- 2. source-survey.md: perform the current source survey yourself; no earlier Researcher artifact exists to consume.",
        "- 3. prior-goal-evidence.md and prior-task-evidence.md: inspect relevant earlier evidence and cite it where material.",
        "- 4. selected-skills.md: read the selected planning and research skills before producing the Scout plan.",
        "- 5. context-budget.md: use retrieval handles before asking for other large evidence in prompts.",
        "- 6. objective.md and digest.md: preserve the goal, scope, risks, and acceptance path."
    ];
}
