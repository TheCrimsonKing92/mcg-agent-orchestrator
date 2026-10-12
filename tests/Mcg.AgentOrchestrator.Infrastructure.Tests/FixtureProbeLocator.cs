using System.Reflection;

internal static class FixtureProbeLocator
{
    internal static string? FindDirectory(Type anchor, string projectName, params string[] requiredFiles)
    {
        var location = Locate(anchor, projectName, requiredFiles);
        return location.Directory is { } directory &&
            requiredFiles.All(name => File.Exists(Path.Combine(directory, name)))
            ? directory : null;
    }

    internal static IReadOnlyList<string> TriedPaths(Type anchor, string projectName, params string[] requiredFiles)
        => Locate(anchor, projectName, requiredFiles).TriedPaths;

    private static TestBuildOutputLocator.Location Locate(Type anchor, string projectName, string[] requiredFiles)
        => TestBuildOutputLocator.Locate(projectName,
            anchor.Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration,
            Path.GetDirectoryName(anchor.Assembly.Location)!, requiredFiles[0]);
}
