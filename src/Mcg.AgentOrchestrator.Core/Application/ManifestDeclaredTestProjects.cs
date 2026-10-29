namespace Mcg.AgentOrchestrator.Core;

/// <summary>The test project paths explicitly declared by one manifest.</summary>
public sealed class ManifestDeclaredTestProjects
{
    private readonly HashSet<string> projects;

    private ManifestDeclaredTestProjects(IEnumerable<string?> projectPaths) =>
        projects = projectPaths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Normalize(path!)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static ManifestDeclaredTestProjects From(IEnumerable<string?> projectPaths) => new(projectPaths);

    public bool Declares(string? path) =>
        !string.IsNullOrWhiteSpace(path) && projects.Contains(Normalize(path));

    public bool DeclaresAnyBuiltInTestProject =>
        RepositoryTestImpactPlanner.BuiltInTestProjectPaths.Any(Declares);

    public RepositoryTestImpactPlan Filter(RepositoryTestImpactPlan plan) =>
        plan with { Checks = plan.Checks.Where(check => Declares(ProjectArgument(check.Command))).ToArray() };

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string? ProjectArgument(IReadOnlyList<string> command)
    {
        for (var index = 2; index + 1 < command.Count; index++)
        {
            if (command[index].Equals("--project", StringComparison.OrdinalIgnoreCase))
                return command[index + 1];
        }

        // Support the legacy positional dotnet test project form as well.
        return command.Count > 2 && !command[2].StartsWith("-", StringComparison.Ordinal)
            ? command[2]
            : null;
    }
}
