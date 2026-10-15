using System.Security.Cryptography;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerBuildScopeStableDigest
{
    internal static string Compute(WorkerBuildProjectScope.Metadata metadata) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            metadata.Version,
            metadata.WorktreeRoot,
            metadata.GitCommonDirectory,
            metadata.Branch,
            metadata.GoalId,
            metadata.GoalBaseline,
            metadata.IntegrationBranch
        })));
}
