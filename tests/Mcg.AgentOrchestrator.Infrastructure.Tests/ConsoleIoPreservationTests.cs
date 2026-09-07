using System.Diagnostics;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ConsoleIoPreservationTests
{
    [Xunit.Theory(DisplayName = "Child console launch paths preserve redirected caller I/O")]
    [Xunit.InlineData("suppressed", "early", 1, 1)]
    [Xunit.InlineData("suppressed", "lazy", 2, 2)]
    [Xunit.InlineData("console", "early", 1, 1)]
    [Xunit.InlineData("console", "lazy", 2, 2)]
    [Xunit.InlineData("start", "early", 1, 1)]
    [Xunit.InlineData("start", "lazy", 2, 2)]
    public async Task ChildConsoleLaunchPathsPreserveRedirectedCallerIo(string scope, string writerInitialization, int repeat, int nest)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunProbe(scope, writerInitialization, repeat, nest, oldMutation: false);
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("FATAL:", result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.Contains("STDIN:input-marker", result.Stdout, StringComparison.Ordinal);
        Assert.Equal(1, Count(result.Stdout, "MARK:before"));
        Assert.Equal(1, Count(result.Stdout, "MARK:after"));
        Assert.Equal(1, Count(result.Stderr, "EMARK:before"));
        Assert.Equal(1, Count(result.Stderr, "EMARK:after"));
        for (var index = 0; index < repeat; index++)
        {
            Assert.Equal(1, Count(result.Stdout, $"MARK:during:{index}"));
            Assert.Equal(1, Count(result.Stderr, $"EMARK:during:{index}"));
        }

        Assert.All(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("HANDLE:", StringComparison.Ordinal)),
            line => Assert.Contains("out=3,err=3,in=3,window=0", line, StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Legacy console mutation RED control attaches the caller to a console window")]
    public async Task LegacyConsoleMutationRedControlAttachesCallerToConsoleWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunProbe("suppressed", "early", 1, 1, oldMutation: true);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("HANDLE:during:0:out=3,err=3,in=3,window=1", result.Stdout, StringComparison.Ordinal);
    }

    private static async Task<ProbeResult> RunProbe(string scope, string writerInitialization, int repeat, int nest, bool oldMutation)
    {
        var probe = ResolveProbeAssembly();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment.Remove("DOTNET_STARTUP_HOOKS");
        startInfo.ArgumentList.Add(probe);
        startInfo.ArgumentList.Add("--scope");
        startInfo.ArgumentList.Add(scope);
        startInfo.ArgumentList.Add("--writer-init");
        startInfo.ArgumentList.Add(writerInitialization);
        startInfo.ArgumentList.Add("--repeat");
        startInfo.ArgumentList.Add(repeat.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--nest");
        startInfo.ArgumentList.Add(nest.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (oldMutation) startInfo.ArgumentList.Add("--old-mutation");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ConsoleIoProbe.");
        var stdoutRead = process.StandardOutput.ReadToEndAsync();
        var stderrRead = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync("input-marker");
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return new ProbeResult(process.ExitCode, await stdoutRead, await stderrRead);
    }

    private static int Count(string text, string marker) => text.Split(marker, StringSplitOptions.None).Length - 1;

    private static string ResolveProbeAssembly()
    {
        const string assemblyName = "Mcg.AgentOrchestrator.ConsoleIoProbe.dll";
        var testAssemblyDirectory = new DirectoryInfo(Path.GetDirectoryName(typeof(ConsoleIoPreservationTests).Assembly.Location)!);
        var parent = testAssemblyDirectory.Parent;
        var candidates = new List<string> { Path.Combine(testAssemblyDirectory.FullName, assemblyName) };
        if (parent?.Parent?.Parent is { } testProjectDirectory)
        {
            candidates.Add(Path.Combine(
                testProjectDirectory.FullName,
                "Fixtures",
                "ConsoleIoProbe",
                "bin",
                parent.Name,
                testAssemblyDirectory.Name,
                assemblyName));
        }

        if (parent?.Parent is { } artifactsDirectory)
        {
            candidates.Add(Path.Combine(
                artifactsDirectory.FullName,
                "Mcg.AgentOrchestrator.ConsoleIoProbe",
                testAssemblyDirectory.Name,
                assemblyName));
        }

        var probe = candidates.FirstOrDefault(File.Exists);
        Assert.False(string.IsNullOrWhiteSpace(probe), $"Console I/O probe was not built. Tried: {string.Join("; ", candidates)}");
        return probe!;
    }

    private sealed record ProbeResult(int ExitCode, string Stdout, string Stderr);
}
