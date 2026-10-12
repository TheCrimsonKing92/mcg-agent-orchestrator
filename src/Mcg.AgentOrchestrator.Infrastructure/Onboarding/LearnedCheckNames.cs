namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Assigns check names from slash-normalized project paths without I/O.</summary>
public static class LearnedCheckNames
{
    public static (IReadOnlyDictionary<string, string> Names, IReadOnlyList<string> Collisions) Assign(
        IReadOnlyList<string> projectPaths)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        var assignments = projectPaths
            .Select(path => new { Path = path, Name = Path.GetFileNameWithoutExtension(path) })
            .GroupBy(item => AcceptanceArtifactFileNames.Slug(item.Name), StringComparer.Ordinal)
            .SelectMany(group => group.Select(item => new
            {
                item.Path,
                Name = group.Count() > 1 ? Path.ChangeExtension(item.Path, null).Replace('/', '.') : item.Name
            }))
            .OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();

        // Count occurrences before collapsing duplicate paths into dictionary keys.
        var collisions = assignments
            .GroupBy(item => AcceptanceArtifactFileNames.Slug(item.Name), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(item => item.Path))
            .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in assignments)
            names[item.Path] = item.Name;
        return (names, collisions);
    }
}
