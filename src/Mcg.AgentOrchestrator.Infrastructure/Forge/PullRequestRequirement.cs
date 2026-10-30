namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record PullRequestRequirement(PullRequestRequirementKind Kind, string Reason)
{
    public static PullRequestRequirement Required(string reason) => new(PullRequestRequirementKind.Required, reason);
    public static PullRequestRequirement NotRequired(string reason) => new(PullRequestRequirementKind.NotRequired, reason);
    public static PullRequestRequirement Unknown(string reason) => new(PullRequestRequirementKind.Unknown, reason);
}
