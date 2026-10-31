using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Owned by one console view; project identities survive refreshes and scope changes in that session.
internal sealed class OwnerConsoleBoardCollapse
{
    internal const string KeyHint = "c collapse";
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    internal static bool Enabled(OwnerConsoleViewModel? model) =>
        model?.BoardScope is { ProjectCount: > 1, Sections: not null };

    internal void Toggle(string project)
    {
        if (!_collapsed.Add(project)) _collapsed.Remove(project);
    }

    internal OwnerConsoleViewModel Apply(OwnerConsoleViewModel arranged)
    {
        if (!Enabled(arranged)) return arranged;
        var displayed = OwnerConsoleBoardSection.DisplayedRowCount(arranged.Board, true);
        var projects = arranged.Board.Take(displayed).Where(row => row.IsDiagnostic && _collapsed.Contains(row.Project))
            .Select(row => row.Project).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return arranged with { Board = arranged.Board
            .Where(row => !projects.Contains(row.Project) || row.IsDiagnostic)
            .Select(row => projects.Contains(row.Project) ? row with { Collapsed = true } : row)
            .ToImmutableArray() };
    }
}
