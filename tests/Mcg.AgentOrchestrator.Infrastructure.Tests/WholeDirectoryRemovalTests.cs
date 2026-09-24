using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class WholeDirectoryRemovalTests
{
    [Xunit.Fact]
    public void Partial_output_without_an_app_dll_is_removed_as_a_whole()
    {
        var root = Path.Combine(Path.GetTempPath(), $"partial-build-removal-{Guid.NewGuid():N}");
        var owned = Path.Combine(root, "owned");
        Directory.CreateDirectory(owned);
        File.WriteAllText(Path.Combine(owned, "partial-dependency.dll"), "partial");
        try
        {
            WholeDirectoryRemoval.Remove(owned, "App.dll");
            Assert.False(Directory.Exists(owned));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Held_owner_file_prevents_any_removal_until_released()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"whole-removal-{Guid.NewGuid():N}");
        var owned = Path.Combine(root, "owned");
        Directory.CreateDirectory(Path.Combine(owned, "nested"));
        var app = Path.Combine(owned, "App.dll");
        var dependency = Path.Combine(owned, "nested", "dependency.dll");
        File.WriteAllText(app, "app");
        File.WriteAllText(dependency, "dependency");
        try
        {
            using (File.Open(app, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.ThrowsAny<IOException>(() => WholeDirectoryRemoval.Remove(owned, "App.dll"));
                Assert.Equal("app", File.ReadAllText(app));
                Assert.Equal("dependency", File.ReadAllText(dependency));
            }

            WholeDirectoryRemoval.Remove(owned, "App.dll");
            Assert.False(Directory.Exists(owned));
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
