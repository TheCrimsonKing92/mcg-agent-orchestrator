using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class PerUserGoalRootLeakProbe
{
    private readonly string _perUserGoalsRoot;
    private readonly string _goalPrefix;
    private readonly string[] _beforeEntries;
    private readonly string[] _beforeGoalRoot;

    internal PerUserGoalRootLeakProbe(GoalId goalId)
    {
        _perUserGoalsRoot = Path.Combine(DotnetBuildEnvironmentManager.ResolveIsolatedRootBase(
            null,
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath(),
            OperatingSystem.IsWindows()),
            "goals");
        _goalPrefix = goalId.Value[..8].ToLowerInvariant();
        _beforeEntries = SnapshotEntries(_perUserGoalsRoot);
        _beforeGoalRoot = Snapshot(Path.Combine(_perUserGoalsRoot, _goalPrefix));
    }

    internal void AssertScopedTo(string actualGoalRoot, string temporaryDirectory)
    {
        Assert.True(Directory.Exists(actualGoalRoot), $"Goal root was not created: {actualGoalRoot}");
        var relative = Path.GetRelativePath(temporaryDirectory, actualGoalRoot);
        Assert.False(relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"Goal root escaped test temporary directory: {actualGoalRoot}");
        Assert.Empty(SnapshotEntries(_perUserGoalsRoot).Except(_beforeEntries, StringComparer.Ordinal));
        Assert.Equal(_beforeGoalRoot, Snapshot(Path.Combine(_perUserGoalsRoot, _goalPrefix)));
    }

    internal static void DrainRegistrationReports(DotnetBuildStorageRoot storageRoot)
    {
        // An unregistered test root must not leave reports for another test's sweep.
        while (DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(25, storageRoot).Count == 25)
        {
        }
    }

    private static string[] SnapshotEntries(string root) => Directory.Exists(root)
        ? Directory.GetFileSystemEntries(root).Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray()
        : [];

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
