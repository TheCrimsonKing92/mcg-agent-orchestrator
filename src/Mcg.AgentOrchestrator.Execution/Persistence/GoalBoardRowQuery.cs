using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record GoalBoardRowQuery(int? RecentTerminalLimit, int TitlePrefixChars, string? FullObjectiveMarker)
{
    internal static bool IsTerminal(string status) =>
        status is nameof(GoalStatus.Completed) or nameof(GoalStatus.Cancelled) or nameof(GoalStatus.Superseded);

    internal static string TerminalStatusSql =>
        $"'{GoalStatus.Completed}', '{GoalStatus.Cancelled}', '{GoalStatus.Superseded}'";

    internal void EnsureValid()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(TitlePrefixChars);
        if (RecentTerminalLimit is { } limit) ArgumentOutOfRangeException.ThrowIfNegative(limit);
    }

    internal string Title(string objective) => TitlePrefixChars == 0 ? string.Empty :
        FullObjectiveMarker is not null && objective.Contains(FullObjectiveMarker, StringComparison.Ordinal)
            ? objective : objective[..Math.Min(TitlePrefixChars, objective.Length)];
}
