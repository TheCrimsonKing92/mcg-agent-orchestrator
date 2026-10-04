using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    internal static bool IsStopParkAlias(IReadOnlyList<string> parts) => IsStopDisposition(parts, "park");
    internal static bool IsStopAbandonAlias(IReadOnlyList<string> parts) => IsStopDisposition(parts, "abandon");

    private static bool IsStopDisposition(IReadOnlyList<string> parts, string disposition) =>
        parts.Count > 0 && parts[0].Equals("stop", StringComparison.OrdinalIgnoreCase) &&
        GetFlagValue(parts, "--as")?.Equals(disposition, StringComparison.OrdinalIgnoreCase) == true;

    private static IReadOnlyList<string> PrepareStopDispositionParts(IReadOnlyList<string> parts, string disposition)
    {
        var usage = CliCommandHelp.StopUsage["Usage: ".Length..];
        CliArgumentParser.RequirePartCount(parts, 3, usage);
        var reason = ResolveTextArgument(parts, 2, usage, "--text-file");
        return BuildStopDelegateParts($"{disposition}-goal", parts[1], reason, parts, $"--confirm-goal-{disposition}");
    }

    internal static GoalParkCommand PrepareGoalParkCommandFromStopAlias(IReadOnlyList<string> parts) =>
        PrepareGoalParkCommand(PrepareStopDispositionParts(parts, "park"));

    internal static GoalAbandonCommand PrepareGoalAbandonCommandFromStopAlias(IReadOnlyList<string> parts) =>
        PrepareGoalAbandonCommand(PrepareStopDispositionParts(parts, "abandon"));

    internal static void RenderGoalParkStopAliasDryRun(GoalParkCommand command, Goal goal)
    {
        Console.WriteLine($"Goal park dry run {goal.Id.Value[..8]}:");
        Console.WriteLine($"  reason: {command.Reason}");
        Console.WriteLine($"  running dispatches to cancel: {CollectLiveDispatches(goal).Length}");
        Console.WriteLine("  attention waits: resolve with park reason");
        Console.WriteLine($"  command: park-goal {goal.Id.Value[..8]} <reason> --confirm-goal-park");
    }
}
