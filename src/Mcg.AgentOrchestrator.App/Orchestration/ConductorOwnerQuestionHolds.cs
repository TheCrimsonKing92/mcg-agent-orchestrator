namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorOwnerQuestionHolds
{
    internal static bool ExcludesFromWalk(string? state) =>
        state is "steward-owner-question" or "author-owner-question";
}
