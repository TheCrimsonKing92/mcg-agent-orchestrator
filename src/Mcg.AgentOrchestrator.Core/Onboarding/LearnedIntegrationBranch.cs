namespace Mcg.AgentOrchestrator.Core;

/// <summary>A branch supported by repository evidence, or a question for its owner.</summary>
public sealed record LearnedIntegrationBranch
{
    public const string OwnerQuestionKey = "integration-branch";

    public ProjectFact<string>? Branch { get; }
    public string? OwnerQuestionFactKey { get; }
    public string Reason { get; }

    private LearnedIntegrationBranch(ProjectFact<string>? branch, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Branch = branch;
        OwnerQuestionFactKey = branch is null ? OwnerQuestionKey : null;
        Reason = reason;
    }

    public static LearnedIntegrationBranch Learned(ProjectFact<string> branch, string reason)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch.Value);
        return new(branch, reason);
    }

    public static LearnedIntegrationBranch Unlearned(string reason) => new(null, reason);
}
