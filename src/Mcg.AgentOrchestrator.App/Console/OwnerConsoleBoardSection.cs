using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Goal-bearing and onboarding sections occupy scrolling board rows.
internal sealed record OwnerConsoleBoardSection(string Project, OwnerConsoleBoardRow Header,
    ImmutableArray<OwnerConsoleBoardRow> Goals)
{
    internal static OwnerConsoleBoardSection Create(string project, ImmutableArray<OwnerConsoleBoardRow> goals,
        bool conductorLive, DateTimeOffset? lastLanded, DateTimeOffset now, ProjectOnboardingState? onboarding = null)
    {
        if (onboarding is { IsOnboarding: true })
            return new(project, new("", "", $"{project} · onboarding · {onboarding.Stack} · {onboarding.Progress}",
                "onboarding", "", null, "", Dimmed: onboarding.Unavailable is not null)
                { Project = project, IsDiagnostic = true }, goals);
        var active = goals.Count(row => !row.Dimmed);
        var inGate = goals.Count(row => row.SortKey.Rank == OwnerConsoleAttention.GateRunning);
        var waitingForGate = goals.Count(row => row.SortKey.Rank == OwnerConsoleAttention.WaitingForGate);
        var conductor = !conductorLive ? "stopped" : active > 0 ? "running" : "idle";
        var age = lastLanded is { } at ? OwnerConsoleBoardRenderer.Age(now - at) : "never";
        var parts = new List<string> { project, $"conductor {conductor}", $"{active} active" };
        if (inGate > 0) parts.Add($"{inGate} in gate");
        if (waitingForGate > 0) parts.Add($"{waitingForGate} waiting for gate");
        parts.Add($"last landed {age}");
        var title = string.Join(" · ", parts);
        return new(project, new("", "", title, conductor, "", null, "") { Project = project, IsDiagnostic = true }, goals);
    }

    internal static ImmutableArray<OwnerConsoleBoardRow> Flatten(IEnumerable<OwnerConsoleBoardSection> sections) =>
        sections.OrderBy(section => section.Goals.IsEmpty && section.Header.State != "onboarding")
            .SelectMany(section => section.Goals.Prepend(section.Header)).ToImmutableArray();

    // WithDecisions updates goal ranks after source composition. Restore section boundaries at the view seam.
    internal static OwnerConsoleViewModel Arrange(OwnerConsoleViewModel model)
    {
        if (model.BoardScope is not { Sections: { } projects }) return model;
        var groups = model.Board.ToLookup(row => row.Project, StringComparer.OrdinalIgnoreCase);
        var order = projects.Concat(model.Board.Select(row => row.Project)).Distinct(StringComparer.OrdinalIgnoreCase);
        var attention = new OwnerConsoleBoardSectionOrder(model.Decisions);
        var sectioned = model.BoardScope.ProjectCount > 1;
        return model with { Board = order.OrderBy(project => !groups[project].Any(row => !row.IsDiagnostic || row.State == "onboarding"))
            .ThenBy(project => sectioned ? attention.Tier(groups[project]) : 0)
            .SelectMany(project => groups[project]
            .OrderBy(row => row.IsDiagnostic ? 0 : 1)
            .Select(row => sectioned && row.IsDiagnostic
                ? row with { OwnerDecisions = attention.DecisionCount(groups[project]) } : row)).ToImmutableArray() };
    }

    // Arrange keeps footer-only projects in a trailing partition, preserving model/table indexes for goals.
    internal static int DisplayedRowCount(IReadOnlyList<OwnerConsoleBoardRow> board, bool sectioned)
    {
        if (!sectioned) return board.Count;
        for (var index = board.Count - 1; index >= 0; index--)
            if (!board[index].IsDiagnostic || board[index].State == "onboarding" || board[index].Collapsed) return index + 1;
        return 0;
    }

    internal static string HeaderLine(OwnerConsoleBoardRow header) => header.OwnerDecisions > 0
        ? $"── {header.Title} · {header.OwnerDecisions} {(header.OwnerDecisions == 1 ? "decision" : "decisions")} ──"
        : $"── {header.Title} ──";

    internal static IReadOnlyList<(string Text, bool Dimmed)> Footer(OwnerConsoleViewModel model)
    {
        if (model.BoardScope is not { ProjectCount: > 1 }) return [];
        var goals = model.Board.Where(row => !row.IsDiagnostic).Select(row => row.Project)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var empty = model.Board.Where(row => row.IsDiagnostic && !row.Collapsed && row.State != "onboarding" && !goals.Contains(row.Project)).ToArray();
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
        scope is not null && (scope.ProjectCount > 1 || row.State == "onboarding") && row.IsDiagnostic && !row.Collapsed;

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
