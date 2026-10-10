using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;
using static DotnetBuildEnvironmentManagerTests;

[Xunit.Collection(TestCollections.DotnetBuildEnvironmentManagerStaticHooks)]
public sealed class DotnetBuildEnvironmentManagerTestsLocalHostOnlyHandleProbe : DotnetBuildEnvironmentManagerRootedTestBase
{
    [Xunit.Trait("Category", "LocalHostOnly")]
    [Xunit.Fact(
        DisplayName = "LockAttribution_handle_probe_returns_results_for_real_held_file",
        Skip = "probe spawn-context hang under managed hosts - tracked by the probe-fix goal; unskip there")]
    public void LockAttributionHandleProbeReturnsResultsForRealHeldFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = ResolveHandleExecutableForTests();
        if (handle is null)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"mcg-handle64-held-file-{Guid.NewGuid():N}");
        var lockedPath = Path.Combine(root, "held.dll");
        var readyPath = Path.Combine(root, "ready.txt");
        var releasePath = Path.Combine(root, "release.txt");
        Process? holder = null;
        LockAttribution.HandleExecutableForTests = handle;
        LockAttribution.HandleProbeTimeoutForTests = TimeSpan.FromSeconds(10);
        LockAttribution.DisableRestartManagerForTests = true;
        try
        {
            holder = StartFileHolder(lockedPath, readyPath, releasePath);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(readyPath), TimeSpan.FromSeconds(10)), "file holder did not become ready");

            var elapsed = Stopwatch.StartNew();
            var attribution = LockAttribution.Attribute(
                lockedPath,
                "mcg-dotnet-isolated",
                "artifact-prep",
                "prepare-artifacts");
            elapsed.Stop();

            var diagnostic = FormatAttributionDiagnostic(handle, lockedPath, holder.Id, elapsed.Elapsed, attribution);
            Assert.True(
                string.Equals("handle64", attribution.Source, StringComparison.Ordinal),
                $"Expected source handle64 but got {attribution.Source}. {diagnostic}");
            Assert.True(attribution.Holders.Any(attributedHolder => attributedHolder.ProcessId == holder.Id), diagnostic);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"handle64 probe exceeded bound. {diagnostic}");
            Assert.Equal("artifact-prep", attribution.Phase);
            Assert.Equal("prepare-artifacts", attribution.Operation);
        }
        finally
        {
            try { File.WriteAllText(releasePath, "release"); } catch { }
            if (holder is not null)
            {
                StopProcess(holder);
                holder.Dispose();
            }

            LockAttribution.HandleExecutableForTests = null;
            LockAttribution.HandleProbeTimeoutForTests = null;
            LockAttribution.DisableRestartManagerForTests = false;
            TryDeleteDirectory(root);
        }
    }
}
