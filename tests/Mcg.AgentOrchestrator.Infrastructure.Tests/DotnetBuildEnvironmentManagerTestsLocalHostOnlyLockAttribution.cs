using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;
using static DotnetBuildEnvironmentManagerTests;

[Xunit.Collection(TestCollections.DotnetBuildEnvironmentManagerStaticHooks)]
public sealed class DotnetBuildEnvironmentManagerTestsLocalHostOnlyLockAttribution : DotnetBuildEnvironmentManagerRootedTestBase
{
    public static bool RestartManagerAvailable =>
        DotnetBuildEnvironmentManagerTestsLockAttributionLandingFixtures.RestartManagerAvailable;

    [Xunit.Trait("Category", "LocalHostOnly")]
    [Xunit.Fact(
        DisplayName = "LockAttribution_restart_manager_names_file_holder",
        Skip = "Requires Windows Restart Manager.",
        SkipUnless = nameof(RestartManagerAvailable))]
    public void LockAttributionRestartManagerNamesFileHolder()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var currentProcessPath = currentProcess.MainModule?.FileName;
        Assert.True(File.Exists(currentProcessPath), $"Current test host path does not exist: {currentProcessPath}");
        var root = Path.Combine(Path.GetTempPath(), "mcg-rm-attribution-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var lockedPath = Path.Combine(root, "held.bin");
        File.WriteAllText(lockedPath, "held");

        try
        {
            using var heldFile = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var attribution = LockAttribution.Attribute(
                lockedPath,
                null,
                "artifact-prep",
                "prepare-artifacts");

            Assert.Equal("restart-manager", attribution.Source);

            var holder = Assert.Single(attribution.Holders.Where(holder => holder.ProcessId == currentProcess.Id));
            var expectedStartTime = new DateTimeOffset(currentProcess.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var expectedProcessName = FileVersionInfo.GetVersionInfo(currentProcessPath!).FileDescription;
            if (string.IsNullOrWhiteSpace(expectedProcessName))
            {
                expectedProcessName = currentProcess.ProcessName;
            }

            Assert.Equal(expectedProcessName, holder.ProcessName);
            Assert.True(holder.ProcessStartTime.HasValue);
            Assert.True(
                (holder.ProcessStartTime.Value - expectedStartTime).Duration() < TimeSpan.FromSeconds(2),
                $"Expected RM start time near {expectedStartTime:O}, got {holder.ProcessStartTime:O}.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }
}
