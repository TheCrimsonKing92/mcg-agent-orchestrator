using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AttributedFailureRouteDecision(
    TaskId? DeveloperTaskId, string? Feedback, string? Signature, string? DeclineReason)
{
    internal bool IsRoute => DeveloperTaskId is not null;

    internal static AttributedFailureRouteDecision Decline(string reason) => new(null, null, null, reason);
}
