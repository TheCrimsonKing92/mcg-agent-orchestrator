using System.Collections.Immutable;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// A project owns one non-goal header followed by its already ranked goal rows.
internal sealed record OwnerConsoleBoardSection(string Project, OwnerConsoleBoardRow Header,
    ImmutableArray<OwnerConsoleBoardRow> Goals)
{
    internal static OwnerConsoleBoardSection Create(string project, ImmutableArray<OwnerConsoleBoardRow> goals,
        bool conductorLive, DateTimeOffset? lastLanded, DateTimeOffset now)
    {
        var active = goals.Count(row => !row.Dimmed);
        var verified = goals.Count(row => row.State == nameof(GoalStatus.Verified));
        var conductor = !conductorLive ? "stopped" : active > 0 ? "running" : "idle";
        var age = lastLanded is { } at ? OwnerConsoleBoardRenderer.Age(now - at) : "never";
        var title = $"{project}  conductor {conductor}  {active} active  {verified} verified  last landed {age}" +
            (goals.IsEmpty ? "  no active goals" : "");
        return new(project, new("", "", title, "", "", null, "") { Project = project, IsDiagnostic = true }, goals);
    }

    internal static ImmutableArray<OwnerConsoleBoardRow> Flatten(IEnumerable<OwnerConsoleBoardSection> sections) =>
        sections.SelectMany(section => section.Goals.Prepend(section.Header)).ToImmutableArray();

    // WithDecisions updates goal ranks after source composition. Restore section boundaries at the view seam.
    internal static OwnerConsoleViewModel Arrange(OwnerConsoleViewModel model)
    {
        if (model.BoardScope is not { ProjectCount: > 1, Sections: { } projects }) return model;
        var groups = model.Board.ToLookup(row => row.Project, StringComparer.OrdinalIgnoreCase);
        var order = projects.Concat(model.Board.Select(row => row.Project)).Distinct(StringComparer.OrdinalIgnoreCase);
        return model with { Board = order.SelectMany(project => groups[project]
            .OrderBy(row => row.IsDiagnostic ? 0 : 1)).ToImmutableArray() };
    }

    internal static bool IsHeader(OwnerConsoleBoardRow row, OwnerConsoleBoardScope? scope) =>
        scope is { ProjectCount: > 1 } && row.IsDiagnostic;

    internal static int NearestGoal(ImmutableArray<OwnerConsoleBoardRow> board,
        OwnerConsoleBoardScope? scope, int index, int direction)
    {
        if (board.IsEmpty) return -1;
        index = Math.Clamp(index, 0, board.Length - 1);
        if (!IsHeader(board[index], scope)) return index;
        if (direction == 0)
        {
            for (var distance = 1; distance < board.Length; distance++)
            {
                if (index + distance < board.Length && !IsHeader(board[index + distance], scope)) return index + distance;
                if (index - distance >= 0 && !IsHeader(board[index - distance], scope)) return index - distance;
            }
            return -1;
        }
        var step = Math.Sign(direction);
        for (var candidate = index + step; candidate >= 0 && candidate < board.Length; candidate += step)
            if (!IsHeader(board[candidate], scope)) return candidate;
        for (var candidate = index - step; candidate >= 0 && candidate < board.Length; candidate -= step)
            if (!IsHeader(board[candidate], scope)) return candidate;
        return -1;
    }
}
