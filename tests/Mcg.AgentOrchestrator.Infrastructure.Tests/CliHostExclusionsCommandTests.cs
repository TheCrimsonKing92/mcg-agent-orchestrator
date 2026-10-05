using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: command I/O is in memory; runtime adapter processes are never started.
public sealed class CliHostExclusionsCommandTests
{
    private static readonly string[] Roots = HostScanExclusionRoots.Compute(new(
        Path.GetFullPath("/host-command-tests/temp"),
        Path.GetFullPath("/host-command-tests/user/AppData/Local"),
        Path.GetFullPath("/host-command-tests/user"),
        Path.GetFullPath("/host-command-tests/repo"))).ToArray();

    [Fact]
    public void Report_ElevatedSubset_PrintsEachStatusWithoutWrites()
    {
        var preferences = new FakePreferences(Roots.Take(2));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, Run(false, preferences, output, error));
        Assert.All(Roots.Take(2), root => Assert.Contains($"present {root}", output.ToString()));
        Assert.All(Roots.Skip(2), root => Assert.Contains($"missing {root}", output.ToString()));
        Assert.Empty(preferences.Writes);
        Assert.Equal(1, preferences.Reads);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Report_NotElevated_PrintsEveryRootAndOneEscapedPowerShellCommand()
    {
        var preferences = new FakePreferences([]) { IsElevated = false };
        var roots = Roots.Append(Path.GetFullPath("/host-command-tests/owner's folder")).ToArray();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliHostExclusionsCommand.Run(["host-exclusions"], roots,
            preferences, preferences, output, error));
        Assert.Contains("cannot be read without elevation", output.ToString());
        var command = Assert.Single(output.ToString().Split(Environment.NewLine),
            line => line.StartsWith("Add-MpPreference -ExclusionPath ", StringComparison.Ordinal));
        Assert.All(roots, root =>
        {
            Assert.Contains($"required {root}", output.ToString());
            Assert.Contains("'" + root.Replace("'", "''") + "'", command);
        });
        Assert.Empty(preferences.Writes);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Apply_TwoMissingRoots_AddsEachOnceAndRereadsFinalState()
    {
        var preferences = new FakePreferences(Roots.Skip(2));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, Run(true, preferences, output, error));
        Assert.Equal(Roots.Take(2), preferences.Writes);
        Assert.All(Roots.Take(2), root => Assert.Contains($"added {root}", output.ToString()));
        Assert.Equal(2, preferences.Reads);
        Assert.Contains("Final state:", output.ToString());
        Assert.All(Roots, root => Assert.Contains($"present {root}", output.ToString()));
        Assert.DoesNotContain("missing ", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Apply_AllAlreadyPresent_MakesNoWriterCalls()
    {
        var preferences = new FakePreferences(Roots);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, Run(true, preferences, output, error));
        Assert.Empty(preferences.Writes);
        Assert.Equal(2, preferences.Reads);
        Assert.All(Roots, root => Assert.Contains($"present {root}", output.ToString()));
    }

    [Fact]
    public void Apply_NotElevated_RefusesBeforeReadingOrWriting()
    {
        var preferences = new FakePreferences([]) { IsElevated = false };
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.NotEqual(0, Run(true, preferences, output, error));
        Assert.Contains("elevated", error.ToString());
        Assert.Empty(preferences.Writes);
        Assert.Equal(0, preferences.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_FailedOrIgnoredWrite_ReturnsFailureWithFinalMissingRoot(bool throws)
    {
        var preferences = new FakePreferences(Roots.Skip(2)) { IgnoreWrites = !throws,
            ThrowForPath = throws ? Roots[0] : null };
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.NotEqual(0, Run(true, preferences, output, error));
        Assert.Equal(Roots.Take(2), preferences.Writes);
        Assert.Equal(2, preferences.Reads);
        Assert.Contains($"missing {Roots[0]}", output.ToString());
        Assert.Contains("remain missing", error.ToString());
        if (throws)
        {
            Assert.Contains($"failed {Roots[0]}", error.ToString());
            Assert.Contains($"present {Roots[1]}", output.ToString());
        }
    }

    [Fact]
    public void Report_ReadFailure_ReturnsFailureWithoutWrites()
    {
        var preferences = new FakePreferences([]) { ThrowOnRead = true };
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, Run(false, preferences, output, error));
        Assert.Contains("reader failed", error.ToString());
        Assert.Empty(preferences.Writes);
    }

    [Fact]
    public void Apply_FinalReadFails_ReturnsFailureAfterAttemptedWrites()
    {
        var preferences = new FakePreferences(Roots.Skip(2)) { ThrowOnFinalRead = true };
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, Run(true, preferences, output, error));
        Assert.Equal(Roots.Take(2), preferences.Writes);
        Assert.Equal(2, preferences.Reads);
        Assert.Contains("reader failed", error.ToString());
    }

    [Fact]
    public void Report_OnlyParentExcluded_StillReportsChildMissing()
    {
        var preferences = new FakePreferences([Path.GetDirectoryName(Roots[0])!]);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, Run(false, preferences, output, error));
        Assert.All(Roots, root => Assert.Contains($"missing {root}", output.ToString()));
        Assert.Empty(preferences.Writes);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("extra")]
    [InlineData("--apply=false")]
    public void Run_InvalidArgument_RejectsBeforeReadingOrWriting(string argument)
    {
        var preferences = new FakePreferences([]);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, CliHostExclusionsCommand.Run(["host-exclusions", argument], Roots,
            preferences, preferences, output, error));
        Assert.Contains(CliCommandHelp.HostExclusionsUsage, error.ToString());
        Assert.Equal(0, preferences.Reads);
        Assert.Empty(preferences.Writes);
    }

    [Fact]
    public void Report_CaseAndTrailingSeparator_NormalizesExactMatches()
    {
        var preferences = new FakePreferences(Roots.Select(root => root.ToUpperInvariant() + Path.DirectorySeparatorChar));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, Run(false, preferences, output, error));
        Assert.All(Roots, root => Assert.Contains($"present {root}", output.ToString()));
        Assert.Empty(preferences.Writes);
    }

    [Fact]
    public void Adapter_QuotedMetacharacterPath_PassesOneLiteralFileArgument()
    {
        const string path = "C:\\owner's files\\$(throw 'oops'); & [data]";
        var start = DefenderPreferenceCmdletAdapter.CreateStartInfo("Add", path, "fixed-script.ps1");
        Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "fixed-script.ps1",
            "-Operation", "Add", "-ExclusionPath", path }, start.ArgumentList);
        Assert.Equal(string.Empty, start.Arguments);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
    }

    [Fact]
    public void RuntimeAdapter_SourceCallers_AreLimitedToExplicitCliVerb()
    {
        var root = FindRepositoryRoot();
        var allowed = new[] {
            "src/Mcg.AgentOrchestrator.Execution/Processes/DefenderExclusionPreferences.cs",
            "src/Mcg.AgentOrchestrator.App/Cli/CliHostExclusionsCommand.cs" };
        var callers = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(path => File.ReadAllText(path).Contains(nameof(DefenderPreferenceCmdletAdapter)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).ToArray();
        Assert.Equal(allowed.Order(), callers.Order());
        var script = File.ReadAllText(Path.Combine(root,
            "src/Mcg.AgentOrchestrator.Execution/Processes/DefenderExclusionPreferences.ps1"));
        Assert.Contains("Get-MpPreference", script);
        Assert.Contains("Add-MpPreference -ExclusionPath $ExclusionPath", script);
        Assert.DoesNotContain("Remove-" + "MpPreference", script);
        var program = File.ReadAllText(Path.Combine(root, "src/Mcg.AgentOrchestrator.App/Program.cs"));
        var hostEntry = program.IndexOf("CliHostExclusionsCommand.Run(args)", StringComparison.Ordinal);
        var workspaceEntry = program.IndexOf("OrchestratorProjectSelection.FromArgs", StringComparison.Ordinal);
        Assert.NotEqual(-1, hostEntry);
        Assert.NotEqual(-1, workspaceEntry);
        Assert.True(hostEntry < workspaceEntry);
    }

    private static int Run(bool apply, FakePreferences preferences, TextWriter output, TextWriter error) =>
        CliHostExclusionsCommand.Run(apply ? ["host-exclusions", "--apply"] : ["host-exclusions"],
            Roots, preferences, preferences, output, error);

    private sealed class FakePreferences(IEnumerable<string> present) : IHostExclusionReader, IHostExclusionWriter
    {
        private readonly HashSet<string> _present = present.ToHashSet(StringComparer.OrdinalIgnoreCase);
        public bool IsElevated { get; init; } = true;
        public bool IgnoreWrites { get; init; }
        public bool ThrowOnRead { get; init; }
        public bool ThrowOnFinalRead { get; init; }
        public string? ThrowForPath { get; init; }
        public List<string> Writes { get; } = [];
        public int Reads { get; private set; }

        public HostExclusionSnapshot Read()
        {
            Reads++;
            if (ThrowOnRead || (ThrowOnFinalRead && Reads > 1))
                throw new InvalidOperationException("reader failed");
            return new(IsElevated, _present.ToArray());
        }

        public void AddExclusionPath(string path)
        {
            Writes.Add(path);
            if (path == ThrowForPath) throw new InvalidOperationException("writer failed");
            if (!IgnoreWrites) _present.Add(path);
        }
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var root)) return root;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
            directory is not null; directory = directory.Parent)
            if ((File.Exists(Path.Combine(directory.FullName, ".git")) ||
                 Directory.Exists(Path.Combine(directory.FullName, ".git"))) &&
                File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
