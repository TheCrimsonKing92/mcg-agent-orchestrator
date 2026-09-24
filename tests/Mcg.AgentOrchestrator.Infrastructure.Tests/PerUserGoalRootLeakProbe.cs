using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class PerUserGoalRootLeakProbe(GoalId goalId)
{
    private readonly string _perUserGoalRoot = Path.Combine(
        DotnetBuildEnvironmentManager.ResolveIsolatedRootBase(
            null,
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath(),
            OperatingSystem.IsWindows()),
        "goals", goalId.Value[..8].ToLowerInvariant());
    private readonly string[] _before = Snapshot(Path.Combine(
        DotnetBuildEnvironmentManager.ResolveIsolatedRootBase(
            null,
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath(),
            OperatingSystem.IsWindows()),
        "goals", goalId.Value[..8].ToLowerInvariant()));

    internal void AssertScopedTo(string actualGoalRoot, string temporaryDirectory)
    {
        Assert.True(Directory.Exists(actualGoalRoot), $"Goal root was not created: {actualGoalRoot}");
        var relative = Path.GetRelativePath(temporaryDirectory, actualGoalRoot);
        Assert.False(relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"Goal root escaped test temporary directory: {actualGoalRoot}");
        Assert.Equal(_before, Snapshot(_perUserGoalRoot));
    }

    private static string[] Snapshot(string root)
    {
        if (!Directory.Exists(root))
            return [];
        return ["<directory>", .. Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path) + ":" +
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))];
    }
}
