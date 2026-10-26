using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;

// Probe children and files are isolated per case. The required ambient negative control
// restores both variables immediately after the single start-info construction.
public sealed class GcEnvironmentBuildChannelTests
{
    [Fact]
    public void StartInfo_ExplicitMap_ChangesOnlyGcEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}");
        var baseline = WorkerBuildRequestHost.BuildStartInfo(root, "probe/Probe.proj", "Debug", [], root);
        var enabled = WorkerBuildRequestHost.BuildStartInfo(root, "probe/Probe.proj", "Debug", [], root,
            new Dictionary<string, string> { ["DOTNET_GCConserveMemory"] = "3", ["DOTNET_GCHeapCount"] = "10" });
        Assert.Equal("3", enabled.Environment["DOTNET_GCConserveMemory"]);
        Assert.Equal("10", enabled.Environment["DOTNET_GCHeapCount"]);
        Assert.Equal(baseline.Environment.OrderBy(p => p.Key), enabled.Environment
            .Where(p => p.Key is not ("DOTNET_GCConserveMemory" or "DOTNET_GCHeapCount")).OrderBy(p => p.Key));
        Assert.Equal(baseline.ArgumentList, enabled.ArgumentList);
        Assert.Equal(baseline.FileName, enabled.FileName);
        Assert.Equal(baseline.WorkingDirectory, enabled.WorkingDirectory);
    }

    [Fact]
    public void StartInfo_AbsentMap_DiscardsInheritedGcEntries()
    {
        var conserve = Environment.GetEnvironmentVariable("DOTNET_GCConserveMemory");
        var heaps = Environment.GetEnvironmentVariable("DOTNET_GCHeapCount");
        ProcessStartInfo info;
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_GCConserveMemory", "9");
            Environment.SetEnvironmentVariable("DOTNET_GCHeapCount", "2");
            var root = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}");
            info = WorkerBuildRequestHost.BuildStartInfo(root, "probe/Probe.proj", "Debug", [], root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_GCConserveMemory", conserve);
            Environment.SetEnvironmentVariable("DOTNET_GCHeapCount", heaps);
        }
        Assert.False(info.Environment.ContainsKey("DOTNET_GCConserveMemory"));
        Assert.False(info.Environment.ContainsKey("DOTNET_GCHeapCount"));
    }

    [Theory]
    [InlineData(true, true, "conserve=3;heaps=10")]
    [InlineData(false, true, "conserve=;heaps=10")]
    [InlineData(false, false, "conserve=;heaps=")]
    public async Task ToolTask_ExplicitSettings_InheritIndependently(bool conserve, bool heaps, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}");
        var probe = Path.Combine(root, "probe");
        Directory.CreateDirectory(probe);
        try
        {
            File.WriteAllText(Path.Combine(probe, "Probe.proj"), """
                <Project DefaultTargets="Build">
                  <Target Name="Restore" />
                  <Target Name="Build">
                    <Exec Command="&gt; gc-probe.txt echo conserve=%DOTNET_GCConserveMemory%;heaps=%DOTNET_GCHeapCount%" WorkingDirectory="$(MSBuildProjectDirectory)" />
                  </Target>
                </Project>
                """);
            Dictionary<string, string>? variables = null;
            if (conserve || heaps)
            {
                variables = new();
                if (conserve) variables["DOTNET_GCConserveMemory"] = "3";
                if (heaps) variables["DOTNET_GCHeapCount"] = "10";
            }
            var info = WorkerBuildRequestHost.BuildStartInfo(root, "probe/Probe.proj", "Debug", [], root, variables);
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Probe child did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                using var failsafe = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                try { await process.WaitForExitAsync(failsafe.Token); }
                catch (OperationCanceledException)
                { throw new TimeoutException("ToolTask probe child did not exit within the hang failsafe."); }
                Assert.True(process.ExitCode == 0, $"Probe exit {process.ExitCode}: {await output}\n{await error}");
                Assert.Equal(expected, File.ReadAllText(Path.Combine(probe, "gc-probe.txt")).Trim());
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
