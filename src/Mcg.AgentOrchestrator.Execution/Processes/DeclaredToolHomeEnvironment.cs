namespace Mcg.AgentOrchestrator.Infrastructure;

public static class DeclaredToolHomeEnvironment
{
    public static void Apply(IDictionary<string, string?> environment, IReadOnlyList<DeclaredToolHome>? toolHomes)
    {
        if (DescribeMissing(toolHomes) is { } diagnostic)
            throw new InvalidOperationException(diagnostic);
        foreach (var home in toolHomes ?? [])
            environment[home.Variable] = home.Directory;
    }

    public static string? DescribeMissing(IReadOnlyList<DeclaredToolHome>? toolHomes)
    {
        foreach (var home in toolHomes ?? [])
            if (!System.IO.Directory.Exists(home.Directory))
                return $"Declared tool home {home.Variable} directory '{home.Directory}' does not exist.";
        return null;
    }

    public static bool Declares(string name, IReadOnlyList<DeclaredToolHome>? toolHomes) =>
        toolHomes?.Any(home => home.Variable.Equals(name, StringComparison.OrdinalIgnoreCase)) == true;
}
