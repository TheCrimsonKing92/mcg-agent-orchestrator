using System.Collections.Immutable;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Session-only project membership. Names are canonical; All is resolved on every refresh.
internal sealed class OwnerConsoleProjectScope
{
    internal static OwnerConsoleProjectScope All { get; } = new(true, []);
    internal bool IsAll { get; }
    internal ImmutableArray<string> Names { get; }
    internal string Label => IsAll ? "all projects" : string.Join(", ", Names);

    private OwnerConsoleProjectScope(bool all, IEnumerable<string> names)
    {
        IsAll = all;
        Names = names.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name.Equals("home", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    internal static OwnerConsoleProjectScope ForLaunch(OrchestratorWorkspace workspace) =>
        workspace.IsProjectScoped ? new(false, [workspace.ProjectName]) : All;

    internal static OwnerConsoleProjectScope? Parse(string argument, IReadOnlyList<string> knownNames,
        out string? error)
    {
        error = null;
        if (argument.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return All;
        var names = new List<string>();
        foreach (var part in argument.Split(','))
        {
            var name = part.Trim();
            if (name.Length == 0) name = "home";
            var canonical = name.Length <= 64 ? knownNames.FirstOrDefault(known =>
                known.Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
            if (canonical is null)
            {
                error = $"scope: unknown project '{name}'; known projects: {string.Join(", ", knownNames)}";
                return null;
            }
            names.Add(canonical);
        }
        return new(false, names);
    }

    internal bool SameAs(OwnerConsoleProjectScope other) => IsAll == other.IsAll &&
        Names.SequenceEqual(other.Names, StringComparer.OrdinalIgnoreCase);
}
