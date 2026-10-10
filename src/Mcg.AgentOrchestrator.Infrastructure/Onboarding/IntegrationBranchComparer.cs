using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class IntegrationBranchComparer
{
    public static IntegrationBranchComparison Compare(LearnedIntegrationBranch learned, string? registryBranch)
    {
        ArgumentNullException.ThrowIfNull(learned);
        if (learned.Branch is not { } branch)
            return new(IntegrationBranchResultKind.Unresolved, null, registryBranch,
                $"Owner question {learned.OwnerQuestionFactKey}: {learned.Reason}");

        var matches = string.Equals(branch.Value, registryBranch, StringComparison.Ordinal);
        return new(matches ? IntegrationBranchResultKind.Match : IntegrationBranchResultKind.BranchDiffers,
            branch.Value, registryBranch, matches
                ? $"The learned and registry branches match at {branch.Confidence} confidence."
                : $"The learned branch '{branch.Value}' differs from registry branch '{registryBranch}' at {branch.Confidence} confidence.");
    }
}
