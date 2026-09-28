using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTestsRepairHintWrapper
{
    [Xunit.Fact]
    public void MissingReceiptPrintsRunnableDiagnosticWrapperCommand()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var assembly = sandbox.CreateManagedAssemblyPlaceholder();
        File.Delete(Path.Combine(Path.GetDirectoryName(assembly)!, ".mcg-build-receipt.txt"));
        var missingReceipt = sandbox.RunPartition("GoalWorktree", dotnetPath: sandbox.RunnerPath, runnerOverride: false);

        Assert.Equal(24, missingReceipt.ExitCode);
        Assert.Contains("Repair: dotnet build", missingReceipt.Stdout, StringComparison.Ordinal);
        var lines = missingReceipt.Stdout.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var wrapperLine = Assert.Single(lines.Where(line => line.StartsWith("Sanctioned wrapper: ", StringComparison.Ordinal)));
        var command = wrapperLine["Sanctioned wrapper: ".Length..];
        var property = "-p:McgIsolatedArtifactsPath=" + sandbox.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Contains($"\"{property}\"", missingReceipt.Stdout, StringComparison.Ordinal);
        Assert.Contains($"\"{property}\"", command, StringComparison.Ordinal);

        File.Copy(
            Path.Combine(MtpTestRunnerScriptTests.RepositoryRoot(), "scripts", "Invoke-WorkerBuildDiagnostic.ps1"),
            Path.Combine(sandbox.Root, "scripts", "Invoke-WorkerBuildDiagnostic.ps1"));
        var commandScript = Path.Combine(sandbox.Root, "run-repair.ps1");
        File.WriteAllText(commandScript, command + Environment.NewLine);
        var stubDirectory = Path.Combine(sandbox.Root, "dotnet-stub");
        Directory.CreateDirectory(stubDirectory);
        CopyProbeToStub(stubDirectory);
        var argvPath = Path.Combine(sandbox.Root, "dotnet-argv.txt");
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            WorkingDirectory = sandbox.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", commandScript })
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PATH"] = stubDirectory + Path.PathSeparator + startInfo.Environment["PATH"];
        startInfo.Environment["MCG_ISOLATED_DOTNET_MTP_PROBE_PATH"] = argvPath;
        startInfo.Environment["MCG_DOTNET_ISOLATED_ROOT"] = Path.Combine(sandbox.Root, "diagnostic-artifacts");

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.DoesNotContain("parameter name 'p' is ambiguous", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        Assert.True(process.ExitCode == 0, stdout + stderr);
        Assert.Contains("DIAGNOSTIC build: exit=0", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("TIMEOUT", stdout + stderr, StringComparison.Ordinal);
        var argv = File.ReadAllLines(argvPath);
        Assert.Equal(5, argv.Length);
        Assert.Equal("build", argv[0]);
        Assert.Equal(property, argv[2]);
    }

    private static void CopyProbeToStub(string stubDirectory)
    {
        const string name = "Mcg.AgentOrchestrator.IsolatedDotnetProbe";
        var assemblyDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var parent = assemblyDirectory.Parent;
        var candidates = new List<string> { Path.Combine(assemblyDirectory.FullName, name + ".dll") };
        if (parent?.Parent?.Parent is { } testProjectDirectory)
            candidates.Add(Path.Combine(testProjectDirectory.FullName, "Fixtures", "IsolatedDotnetProbe", "bin", parent.Name, assemblyDirectory.Name, name + ".dll"));
        if (parent?.Parent is { } projectOutputRoot)
            candidates.Add(Path.Combine(projectOutputRoot.FullName, name, assemblyDirectory.Name, name + ".dll"));
        var probeDll = candidates.FirstOrDefault(candidate =>
            File.Exists(candidate) &&
            File.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, name + ".exe")) &&
            File.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, name + ".deps.json")) &&
            File.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, name + ".runtimeconfig.json")));
        Assert.False(string.IsNullOrWhiteSpace(probeDll), $"Missing isolated dotnet probe. Tried: {string.Join("; ", candidates)}");
        var probeDirectory = Path.GetDirectoryName(probeDll!)!;
        foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            File.Copy(Path.Combine(probeDirectory, name + extension), Path.Combine(stubDirectory, name + extension));
        File.Copy(Path.Combine(probeDirectory, name + ".exe"), Path.Combine(stubDirectory, "dotnet.exe"));
    }
}
