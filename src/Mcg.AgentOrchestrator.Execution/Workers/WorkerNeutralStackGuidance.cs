namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerNeutralStackGuidance
{
    internal static IReadOnlyList<string> BuildTestLines { get; } = Array.AsReadOnly<string>(
    [
        "- Run the project's own build and test commands from the worktree root before WORKER_RESULT; deterministic-verification.md names the commands this checkout suggests.",
        "- Report each command and its result in WORKER_RESULT `tests`, for example `tests: pass - <command> ok` or `tests: fail - <command>: <first failure>`.",
        "- This project has no orchestrator build helper; use only the project's own tooling.",
        "- The acceptance gate re-runs the project's acceptance manifest checks and decides the verdict."
    ]);
}
