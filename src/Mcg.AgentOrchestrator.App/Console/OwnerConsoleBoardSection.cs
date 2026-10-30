using System.Collections.Immutable;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Section metadata stays in the model; only goal-bearing sections occupy scrolling board rows.
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
        var title = $"{project} · conductor {conductor} · {active} active · {verified} verified · last landed {age}";
        return new(project, new("", "", title, conductor, "", null, "") { Project = project, IsDiagnostic = true }, goals);
    }

    internal static ImmutableArray<OwnerConsoleBoardRow> Flatten(IEnumerable<OwnerConsoleBoardSection> sections) =>
        sections.OrderBy(section => section.Goals.IsEmpty)
            .SelectMany(section => section.Goals.Prepend(section.Header)).ToImmutableArray();

    // WithDecisions updates goal ranks after source composition. Restore section boundaries at the view seam.
    internal static OwnerConsoleViewModel Arrange(OwnerConsoleViewModel model)
    {
        if (model.BoardScope is not { ProjectCount: > 1, Sections: { } projects }) return model;
        var groups = model.Board.ToLookup(row => row.Project, StringComparer.OrdinalIgnoreCase);
        var order = projects.Concat(model.Board.Select(row => row.Project)).Distinct(StringComparer.OrdinalIgnoreCase);
        return model with { Board = order.OrderBy(project => !groups[project].Any(row => !row.IsDiagnostic))
            .SelectMany(project => groups[project]
            .OrderBy(row => row.IsDiagnostic ? 0 : 1)).ToImmutableArray() };
    }

    // Arrange keeps footer-only projects in a trailing partition, preserving model/table indexes for goals.
    internal static int DisplayedRowCount(IReadOnlyList<OwnerConsoleBoardRow> board, bool sectioned)
    {
        if (!sectioned) return board.Count;
        for (var index = board.Count - 1; index >= 0; index--)
            if (!board[index].IsDiagnostic) return index + 1;
        return 0;
    }

    internal static string HeaderLine(OwnerConsoleBoardRow header) => $"── {header.Title} ──";

    internal static IReadOnlyList<(string Text, bool Dimmed)> Footer(OwnerConsoleViewModel model)
    {
        if (model.BoardScope is not { ProjectCount: > 1 }) return [];
        var goals = model.Board.Where(row => !row.IsDiagnostic).Select(row => row.Project)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var empty = model.Board.Where(row => row.IsDiagnostic && !goals.Contains(row.Project)).ToArray();
        var segments = new List<(string Text, bool Dimmed)>();
        foreach (var state in new[] { "running", "idle", "stopped", "unreadable" })
        {
            var entries = empty.Where(row => (row.Dimmed ? "unreadable" : row.State) == state).ToArray();
            if (entries.Length == 0) continue;
            segments.Add(($"{(segments.Count == 0 ? "" : " · ")}{state}: ", false));
            for (var index = 0; index < entries.Length; index++)
            {
                if (index > 0) segments.Add((", ", false));
                segments.Add((state == "unreadable" ? entries[index].Title : entries[index].Project,
                    entries[index].Dimmed));
            }
        }
        return segments;
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
