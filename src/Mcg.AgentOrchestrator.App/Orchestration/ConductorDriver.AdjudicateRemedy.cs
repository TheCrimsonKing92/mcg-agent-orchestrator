using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal static string BuildAdjudicateRemedy(GoalStatus goalStatus) =>
        $"Operator remedy: adjudicate --goal <goal> <task#> " +
        $"{(goalStatus == GoalStatus.AcceptanceFailed ? "reopen-regate" : "close")} " +
        "--text-file <path> --evidence <store>:<ref> " +
        "[--reversibility <kind>] [--precedent <decision-id|rule>].";
}
