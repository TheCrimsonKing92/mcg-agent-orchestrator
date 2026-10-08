// Resolves sibling project outputs belonging to the build that produced this test assembly.
internal static class TestBuildOutputLocator
{
    internal const string BuildOutputRootVariable = "MCG_TEST_BUILD_OUTPUT_ROOT";

    internal sealed record Location(string? Directory, IReadOnlyList<string> TriedPaths);

    internal static Location Locate(
        string projectName, string configuration, string? testAssemblyDirectory = null,
        string? requiredFileName = null)
    {
        var assemblyDirectory = new DirectoryInfo(testAssemblyDirectory ?? AppContext.BaseDirectory);
        var triedPaths = new List<string>();
        if (assemblyDirectory.Parent?.Parent is { } binRoot)
        {
            var sibling = Path.Combine(binRoot.FullName, projectName, configuration);
            triedPaths.Add(sibling);
            if (System.IO.Directory.Exists(sibling)
                && (requiredFileName is null || File.Exists(Path.Combine(sibling, requiredFileName))))
                return new(sibling, triedPaths);
        }

        var buildRoot = Environment.GetEnvironmentVariable(BuildOutputRootVariable);
        // Synthetic layout fixtures must not resolve against the real runner's build root.
        if (!string.IsNullOrWhiteSpace(buildRoot)
            && File.Exists(Path.Combine(assemblyDirectory.FullName,
                Path.GetFileName(typeof(TestBuildOutputLocator).Assembly.Location))))
        {
            var candidate = Path.GetFullPath(Path.Combine(buildRoot, projectName, configuration));
            triedPaths.Add(candidate);
            if (System.IO.Directory.Exists(candidate)
                && (requiredFileName is null || File.Exists(Path.Combine(candidate, requiredFileName))))
                return new(candidate, triedPaths);
        }

        return new(null, triedPaths);
    }
}
