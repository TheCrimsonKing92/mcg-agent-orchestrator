using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleStatusLabel
{
    internal static string For(GoalStatus status) => status switch
    {
        GoalStatus.Draft => "draft",
        GoalStatus.Active => "in progress",
        GoalStatus.WaitingForHuman => "waiting for an answer",
        GoalStatus.Parked => "parked",
        GoalStatus.Verifying => "in gate",
        GoalStatus.Verified => "reviewed, awaiting gate",
        GoalStatus.AcceptanceFailed => "gate failed",
        GoalStatus.Completed => "landed",
        GoalStatus.Failed => "failed",
        GoalStatus.Cancelled => "cancelled",
        GoalStatus.Superseded => "superseded",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown goal status.")
    };

    internal static string For(string state) => For(Enum.Parse<GoalStatus>(state));
}
