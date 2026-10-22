namespace Mcg.AgentOrchestrator.Core;

// Reconstruct the same request identity on every replay of a missing human wait.
internal static class RepairedHumanInputRequestIdentity
{
    internal static HumanInputRequestId Create(
        GoalId goalId,
        TaskId taskId,
        ProgressEvent requested)
    {
        var seed = $"{goalId.Value}\n{taskId.Value}\n{requested.OccurredAt:O}\n{requested.Message}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return new HumanInputRequestId(Convert.ToHexString(hash).ToLowerInvariant()[..32]);
    }
}
