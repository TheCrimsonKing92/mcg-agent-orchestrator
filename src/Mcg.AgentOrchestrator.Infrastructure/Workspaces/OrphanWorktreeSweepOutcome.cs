using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record OrphanWorktreeSweepOutcome(
    string Path, GoalId? GoalId, string Reason, int ProcessId, string? ExecutablePath, string? CallerRole)
{
    internal static void Write(OrphanWorktreeSweepOutcome outcome) =>
        Console.Error.WriteLine("orphan-sweep-outcome " + JsonSerializer.Serialize(outcome));
}
