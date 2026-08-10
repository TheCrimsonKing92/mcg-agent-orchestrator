using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
// Deterministic recording: acceptance stores a dogfood entry rendered from the goal's
// receipts, so a landed goal is journaled without the operator hand-writing prose. --no-record opts out.
private static void AutoRecordDogfoodEntry(CliExecutionContext context)
{
    var goal = context.CurrentGoal!;
    RecordDogfoodEntry(context.Workspace, goal);
    Console.WriteLine($"Recorded dogfood-log entry for goal {goal.Id.Value[..8]} to {context.Workspace.DogfoodLogStorePath}.");
}

private static DogfoodLogRecord RecordDogfoodEntry(OrchestratorWorkspace workspace, Goal goal)
{
    return GoalLandingPostActions.RecordDogfoodEntry(
        goal,
        workspace.ExecutionDirectory,
        workspace.DogfoodLogStorePath,
        Console.WriteLine);
}

private static void HandleRecordGoal(CliExecutionContext context)
{
    var goal = context.CurrentGoal!;
    var record = RecordDogfoodEntry(context.Workspace, goal);
    Console.Write(record.RenderedMarkdown);
    Console.WriteLine();
    Console.WriteLine($"Recorded to {context.Workspace.DogfoodLogStorePath}");
}

private static void HandleDogfoodLog(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var subcommand = parts.Count > 1 && !parts[1].StartsWith("--", StringComparison.Ordinal)
        ? parts[1]
        : "list";

    switch (subcommand.ToLowerInvariant())
    {
        case "list":
        {
            var limit = GetFlagValue(parts, "--limit") is { } value
                ? ParsePositiveInteger(value, "--limit")
                : 20;
            var records = new DogfoodLogStore(context.Workspace.DogfoodLogStorePath)
                .ListRecentAsync(limit)
                .GetAwaiter()
                .GetResult();
            foreach (var record in records)
            {
                Console.WriteLine(record.RenderedMarkdown);
                Console.WriteLine();
            }
            if (records.Count == 0)
            {
                Console.WriteLine($"No dogfood log entries in {context.Workspace.DogfoodLogStorePath}.");
            }
            return;
        }

        case "add":
        case "record":
        {
            var goalPrefix = parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal)
                ? parts[2]
                : null;
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, goalPrefix);
            HandleRecordGoal(context);
            return;
        }

        default:
            throw new ArgumentException(CliCommandHelp.DogfoodLogUsage);
    }
}
}
