using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its files and child process; environment overrides are
// scoped to the child, with no process-wide environment or hooks changed.
public sealed class AcceptanceBareCommandResolutionTests
{
    [Fact]
    public async Task ResolvedBareBatchNameRunsWithBatchFileDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var probe = new BatchProbe();

        var result = await probe.RunAsync("probe", resolveBareCommand: true);

        Assert.False(result.TimedOut, "probe batch did not exit before the command hang limit");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr ?? string.Empty);
        Assert.Equal(probe.BatchPath, result.ResolvedCommandPath, StringComparer.OrdinalIgnoreCase);
        AssertPrintedDirectory(probe.CommandDirectory, result);
        Assert.NotEqual(NormalizeDirectory(probe.WorkingDirectory),
            NormalizeDirectory(result.StandardOutput!), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BypassedResolutionRunsBatchWithWorkingDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var probe = new BatchProbe();

        var result = await probe.RunAsync("probe", resolveBareCommand: false);

        Assert.False(result.TimedOut, "probe batch did not exit before the command hang limit");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr ?? string.Empty);
        Assert.Null(result.ResolvedCommandPath);
        AssertPrintedDirectory(probe.WorkingDirectory, result);
        Assert.NotEqual(NormalizeDirectory(probe.CommandDirectory),
            NormalizeDirectory(result.StandardOutput!), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnresolvableBareNameKeepsCmdNotRecognizedFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var probe = new BatchProbe();
        var command = $"mcg_missing_{Guid.NewGuid():N}";

        var baseline = await probe.RunAsync(command, resolveBareCommand: false);
        var result = await probe.RunAsync(command, resolveBareCommand: true);

        Assert.False(baseline.TimedOut, "unresolved baseline command did not exit before the command hang limit");
        Assert.False(result.TimedOut, "unresolved command did not exit before the command hang limit");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(baseline.ExitCode, result.ExitCode);
        Assert.Contains("is not recognized", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(baseline.Stderr, result.Stderr);
        Assert.Equal(baseline.StandardOutput, result.StandardOutput);
        Assert.Null(baseline.ResolvedCommandPath);
        Assert.Null(result.ResolvedCommandPath);
    }

    [Theory]
    [InlineData("probe.cmd")]
    [InlineData("relative\\probe")]
    [InlineData("relative/probe")]
    public void NameWithSeparatorOrExtensionPassesThroughUnchanged(string command)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var probe = new BatchProbe();
        // Without the qualified-name guard, PATH/PATHEXT search would find this
        // candidate (including probe.cmd.CMD for the already extended command).
        var candidate = Path.Combine(probe.CommandDirectory, command + ".CMD");
        Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
        File.Copy(probe.BatchPath, candidate);
        Assert.True(File.Exists(candidate));
        string[] arguments = [command, "probe"];

        var startInfo = GoalAcceptanceVerifierCaptureCustody.BuildAcceptanceProcessStartInfo(
            arguments, probe.WorkingDirectory, null, null, false,
            environment =>
            {
                foreach (var pair in probe.EnvironmentOverrides)
                    environment[pair.Key] = pair.Value;
            },
            true, out var resolved);

        Assert.Null(resolved);
        Assert.Equal($"/c \"\"{command}\" \"probe\"\"", startInfo.Arguments);
        Assert.Equal(new[] { command, "probe" }, arguments);
    }

    [Fact]
    public async Task FailedResolvedBatchStillRecordsItsPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var probe = new BatchProbe(exitCode: 7);

        var result = await probe.RunAsync("probe", resolveBareCommand: true);

        Assert.False(result.TimedOut, "failing probe batch did not exit before the command hang limit");
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(probe.BatchPath, result.ResolvedCommandPath, StringComparer.OrdinalIgnoreCase);
        AssertPrintedDirectory(probe.CommandDirectory, result);
    }

    [Fact]
    public void PathDirectoryOrderPrecedesExtensionOrder()
    {
        using var files = new ResolverFiles();
        var first = files.Directory("first");
        var second = files.Directory("second");
        var expected = files.File(first, "probe.cmd");
        files.File(second, "probe.exe");

        Assert.Equal(expected, Resolve("probe", $"{first};{second}", ".exe;.cmd", files.Root));
    }

    [Fact]
    public void ExtensionOrderAndCaseInsensitiveEnvironmentKeysAreHonored()
    {
        using var files = new ResolverFiles();
        files.File(files.Root, "probe.cmd");
        var expected = files.File(files.Root, "probe.exe");
        var environment = new Dictionary<string, string?>
        {
            ["pAtH"] = files.Root,
            ["pAtHeXt"] = ";.exe;;.cmd;"
        };

        Assert.Equal(expected, AcceptanceBareCommandResolver.Resolve("probe", environment, files.Root));
    }

    [Fact]
    public void QuotedAndRelativePathEntriesUseChildWorkingDirectory()
    {
        using var files = new ResolverFiles();
        var directory = files.Directory("command files");
        var expected = files.File(directory, "probe.cmd");

        Assert.Equal(expected, Resolve("probe", $"\"{directory}\"", ".cmd", files.Root));
        Assert.Equal(expected, Resolve("probe", "\"command files\"", ".cmd", files.Root));
    }

    [Fact]
    public void ExplicitEmptyEntrySearchesChildWorkingDirectory()
    {
        using var files = new ResolverFiles();
        var other = files.Directory("other");
        var expected = files.File(files.Root, "probe.cmd");

        Assert.Equal(expected, Resolve("probe", $"{other};", ".cmd", files.Root));
        Assert.Null(Resolve("probe", other, ".cmd", files.Root));
    }

    [Theory]
    [InlineData(null, ".cmd")]
    [InlineData("", ".cmd")]
    [InlineData("present", null)]
    [InlineData("present", "")]
    [InlineData("present", ";;")]
    public void MissingSearchVariablesDoNotInventDefaults(string? path, string? pathExt)
    {
        using var files = new ResolverFiles();
        files.File(files.Root, "probe.cmd");
        var environment = new Dictionary<string, string?>();
        if (path is not null) environment["PATH"] = path == "present" ? files.Root : path;
        if (pathExt is not null) environment["PATHEXT"] = pathExt;

        Assert.Null(AcceptanceBareCommandResolver.Resolve("probe", environment, files.Root));
    }

    [Fact]
    public void DirectoriesAndExtensionlessFilesAreNotMatches()
    {
        using var files = new ResolverFiles();
        files.Directory("probe.cmd");
        files.File(files.Root, "probe");
        var second = files.Directory("second");
        var expected = files.File(second, "probe.cmd");

        Assert.Null(Resolve("probe", files.Root, ".cmd", files.Root));
        Assert.Equal(expected, Resolve("probe", $"{files.Root};{second}", ".cmd", files.Root));
    }

    [Fact]
    public void InvalidPathEntryDoesNotHideLaterMatch()
    {
        using var files = new ResolverFiles();
        var expected = files.File(files.Root, "probe.cmd");

        Assert.Equal(expected, Resolve("probe", $"invalid\0path;{files.Root}", ".cmd", files.Root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("probe.cmd")]
    [InlineData("relative/probe")]
    [InlineData("relative\\probe")]
    public void QualifiedNamesAreNotResolved(string command)
    {
        using var files = new ResolverFiles();
        files.File(files.Root, "probe.cmd");

        Assert.Null(Resolve(command, files.Root, ".cmd", files.Root));
    }

    [Fact]
    public void BuilderUsesConfiguredEnvironmentAndRewritesOnlyFirstArgumentOnWindows()
    {
        using var files = new ResolverFiles();
        var expected = files.File(files.Root, "probe.cmd");
        string[] arguments = ["probe", "probe"];
        var startInfo = GoalAcceptanceVerifierCaptureCustody.BuildAcceptanceProcessStartInfo(
            arguments, files.Root, null, null, false,
            environment =>
            {
                environment["PATH"] = files.Root;
                environment["PATHEXT"] = ".cmd";
            },
            true, out var resolved);

        Assert.Equal(new[] { "probe", "probe" }, arguments);
        Assert.Equal(files.Root, startInfo.Environment["PATH"]);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(expected, resolved);
            Assert.Equal($"/c \"\"{expected}\" \"probe\"\"", startInfo.Arguments);
            var unchanged = GoalAcceptanceVerifier.BuildAcceptanceProcessStartInfo(arguments, files.Root);
            Assert.Equal("/c \"\"probe\" \"probe\"\"", unchanged.Arguments);
        }
        else
        {
            Assert.Null(resolved);
            Assert.Equal("'probe' 'probe'", startInfo.ArgumentList.Last());
        }
    }

    private static string? Resolve(string command, string path, string pathExt, string workingDirectory) =>
        AcceptanceBareCommandResolver.Resolve(command, new Dictionary<string, string?>
        {
            ["PATH"] = path,
            ["PATHEXT"] = pathExt
        }, workingDirectory);

    private static string NormalizeDirectory(string directory) =>
        Path.TrimEndingDirectorySeparator(directory.Trim());

    private static void AssertPrintedDirectory(string expected, GoalAcceptanceVerifier.CommandResult result)
    {
        Assert.NotNull(result.StandardOutput);
        Assert.Equal(NormalizeDirectory(expected), NormalizeDirectory(result.StandardOutput),
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed class BatchProbe : IDisposable
    {
        private readonly ResolverFiles _files = new();

        internal string CommandDirectory { get; }
        internal string WorkingDirectory { get; }
        internal string BatchPath { get; }
        internal IReadOnlyDictionary<string, string?> EnvironmentOverrides { get; }

        internal BatchProbe(int exitCode = 0)
        {
            CommandDirectory = _files.Directory("command files");
            WorkingDirectory = _files.Directory("work files");
            BatchPath = _files.File(CommandDirectory, "probe.cmd");
            File.WriteAllText(BatchPath, $"@echo off\r\necho %~dp0\r\nexit /b {exitCode}\r\n");
            EnvironmentOverrides = new Dictionary<string, string?>
            {
                ["PATH"] = CommandDirectory,
                ["PATHEXT"] = ".CMD"
            };
            Assert.False(File.Exists(Path.Combine(WorkingDirectory, "probe.cmd")));
            Assert.NotEqual(CommandDirectory, WorkingDirectory);
        }

        internal Task<GoalAcceptanceVerifier.CommandResult> RunAsync(string command, bool resolveBareCommand) =>
            GoalAcceptanceVerifier.RunManifestCommandForTestsAsync(
                new GoalAcceptanceVerifier.AcceptanceManifestCheck
                {
                    Name = "bare command probe",
                    Type = "command",
                    Command = command
                },
                WorkingDirectory, TimeSpan.FromMinutes(1), EnvironmentOverrides, resolveBareCommand);

        public void Dispose() => _files.Dispose();
    }

    private sealed class ResolverFiles : IDisposable
    {
        internal string Root { get; } = System.IO.Directory.CreateTempSubdirectory("mcg-command-resolution-").FullName;

        internal string Directory(string name) => System.IO.Directory.CreateDirectory(Path.Combine(Root, name)).FullName;

        internal string File(string directory, string name)
        {
            var path = Path.Combine(directory, name);
            System.IO.File.WriteAllText(path, string.Empty);
            return path;
        }

        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }
}
