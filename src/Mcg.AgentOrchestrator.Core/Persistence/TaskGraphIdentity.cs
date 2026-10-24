using System.Security.Cryptography;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

// Ordered task identity and contract, independent of task progress and goal status.
public static class TaskGraphIdentity
{
    public static string Compute(GoalSnapshot goal) => Hash(goal.Tasks.Select(task =>
        new Entry(task.Id, task.RequiredRole, task.Description)));

    public static string Compute(Goal goal) => Hash(goal.Tasks.Select(task =>
        new Entry(task.Id.Value, task.RequiredRole, task.Description)));

    private static string Hash(IEnumerable<Entry> entries) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entries.ToArray())));

    private sealed record Entry(string Id, AgentRole Role, string Description);
}
