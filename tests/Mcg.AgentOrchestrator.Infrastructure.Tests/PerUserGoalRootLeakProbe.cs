using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

internal sealed class PerUserGoalRootLeakProbe
{
    private readonly string _perUserGoalsRoot;
    private readonly string _goalId;
    private readonly string _goalPrefix;
    private readonly string[] _beforeEntries;
    private readonly string[] _beforeGoalRoot;

    internal PerUserGoalRootLeakProbe(GoalId goalId, string? perUserGoalsRoot = null)
    {
        _perUserGoalsRoot = perUserGoalsRoot ?? Path.Combine(DotnetBuildEnvironmentManager.ResolveIsolatedRootBase(
            null,
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath(),
            OperatingSystem.IsWindows()),
            "goals");
        _goalId = goalId.Value;
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
        Assert.Empty(SnapshotEntries(_perUserGoalsRoot)
            .Except(_beforeEntries, StringComparer.Ordinal)
            .Where(IsCreatedByThisTest));
        var goalRoot = Path.Combine(_perUserGoalsRoot, _goalPrefix);
        if (IsCreatedByThisTest(_goalPrefix))
            Assert.Equal(_beforeGoalRoot, Snapshot(goalRoot));
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

    private bool IsCreatedByThisTest(string entry)
    {
        var leasePath = Path.Combine(_perUserGoalsRoot, entry, "lease", "lease.json");
        try
        {
            using var lease = JsonDocument.Parse(File.ReadAllText(leasePath));
            var metadata = lease.RootElement;
            if (metadata.GetProperty("goalId").GetString() == _goalId)
                return true;

            return metadata.GetProperty("ownerProcessId").GetInt32() == Environment.ProcessId &&
                string.Equals(metadata.GetProperty("machineName").GetString(),
                    Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
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
