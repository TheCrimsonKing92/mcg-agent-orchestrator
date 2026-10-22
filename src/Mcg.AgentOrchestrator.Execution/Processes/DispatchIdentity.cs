using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DispatchIdentity
{
    internal static string BuildId(GoalId goalId, TaskId taskId, TaskDispatchRecord dispatch)
    {
        var raw = string.Join('\u001f',
            goalId.Value,
            taskId.Value,
            dispatch.DispatchedAt.ToUniversalTime().ToString("O"),
            dispatch.WorkerName,
            dispatch.WorkingDirectory,
            dispatch.ProviderSessionId,
            dispatch.Command);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
    }
}
