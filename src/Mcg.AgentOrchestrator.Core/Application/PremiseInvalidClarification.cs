namespace Mcg.AgentOrchestrator.Core;

internal sealed record PremiseInvalidClarification(string Question, string BlockerFingerprint)
{
    internal static bool CanRoute(AgentRole role, bool premiseRefuted) =>
        role is AgentRole.Planner or AgentRole.Researcher ||
        role is AgentRole.Developer or AgentRole.Tester && premiseRefuted;

    internal static PremiseInvalidClarification Create(TaskSpec task, string evidence)
    {
        var question =
            $"{task.RequiredRole} reported premise-invalid: {evidence}. " +
            (task.RequiredRole is AgentRole.Planner or AgentRole.Researcher
                ? "Clarify, supersede, or abandon the goal before Developer dispatch."
                : $"Clarify, supersede, or abandon the goal before the next {task.RequiredRole} dispatch.");
        return new(question, HumanInputRequest.BuildWorkerResultBlockerFingerprint(
            task.Id, task.RequiredRole, question, evidence));
    }
}
