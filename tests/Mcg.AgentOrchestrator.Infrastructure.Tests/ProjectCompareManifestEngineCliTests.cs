using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every invocation owns its fixtures, registry, data root and writer.
public sealed class ProjectCompareManifestEngineCliTests
{
    private const string Usage = "Usage: project compare-manifest-engine [name] [--root <path>] --manifest <path>";

    [Fact]
    public void Compare_NetHealthSettings_PrintsOrderedFactsAndReturnsOneWithoutWriting()
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("no-solution");
        var registry = Registry(home);
        var manifestPath = Path.Combine(source.Root, "manifest.json");
        File.WriteAllText(manifestPath, """
            { "engine": { "slotCount": 1, "maxConcurrentShards": 2, "enforceStructuralCoverage": false,
                          "timeouts": { "defaultMinutes": 10, "buildServerShutdownMinutes": 1, "discoveryMinutes": 2 } },
              "forbiddenChangedPathGlobs": [],
              "checks": [{ "type": "dotnet-test", "project": "sample.csproj", "arguments": ["--verbosity", "minimal"] }] }
            """);
        var before = Snapshot(home.Root, source.Root, registry.DataRootDirectory);
        using var output = new StringWriter();

        var exit = ProjectCliCommand.Execute(
            ["project", "compare-manifest-engine", "--root", source.Root, "--manifest", manifestPath],
            registry, home.Root, null, output);

        Assert.Equal(1, exit);
        var defaults = new AcceptanceGateEngineSettings();
        AssertLines(output,
        [
            "DiffersFromGateDefault engine.maxConcurrentShards hand=2 learned=1",
            "Match engine.enforceStructuralCoverage hand=false learned=false",
            $"DiffersFromGateDefault engine.timeouts.defaultMinutes hand=10 learned={Minutes(defaults.ResolveCheckTimeout(null))}",
            $"DiffersFromGateDefault engine.timeouts.buildServerShutdownMinutes hand=1 learned={Minutes(defaults.ResolveBuildServerShutdownTimeout())}",
            $"DiffersFromGateDefault engine.timeouts.discoveryMinutes hand=2 learned={Minutes(defaults.ResolveDiscoveryTimeout())}",
            "NotConsumed engine.slotCount hand=1 learned=none",
            "Match forbiddenChangedPathGlobs hand=none learned=none",
            "Match check[sample.csproj].filter hand=none learned=none",
            "Match check[sample.csproj].timeoutMinutes hand=none learned=none",
            "Match check[sample.csproj].advisory hand=false learned=false",
            "Match check[sample.csproj].estimatedSerialSeconds hand=0 learned=0",
            "Match check[sample.csproj].arguments hand=--verbosity minimal learned=none"
        ]);
        AssertUnchanged(before, home.Root, source.Root, registry.DataRootDirectory);
    }

    [Theory]
    [InlineData("vstest", "Match check[sample.csproj].arguments hand=--verbosity minimal learned=none")]
    [InlineData("mtp", "Match check[sample.csproj].arguments hand=none learned=none")]
    public void Compare_DefaultSettingsAndUnconsumedFacts_ReturnsZeroAndIgnoresProjectSelection(string runner, string arguments)
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("no-solution");
        var registry = Registry(home);
        var manifestPath = Path.Combine(source.Root, "manifest.json");
        File.WriteAllText(manifestPath, $$"""
            { "engine": { "maxConcurrentShards": 1, "slotCount": 1 },
              "checks": [{ "type": "dotnet-test", "project": "sample.csproj", "runner": "{{runner}}",
                           "arguments": ["--verbosity", "minimal"] }] }
            """);
        var before = Snapshot(home.Root, source.Root, registry.DataRootDirectory);
        using var output = new StringWriter();

        Assert.Equal(0, ProjectCliCommand.Execute(
            ["project", "compare-manifest-engine", "unknown-project", "--root=" + Path.Combine(source.Root, "absent"),
                "--manifest=" + manifestPath], registry, home.Root, "also-unknown", output));

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(runner == "mtp" ? 13 : 12, lines.Length);
        Assert.Contains(lines, line => line.StartsWith("NotConsumed engine.slotCount hand=1 learned=none (", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith(arguments + " (", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("DiffersFromGateDefault", StringComparison.Ordinal));
        if (runner == "mtp")
            Assert.Contains(lines, line => line.StartsWith(
                "NotConsumed check[sample.csproj].arguments hand=--verbosity minimal learned=none (", StringComparison.Ordinal));
        AssertUnchanged(before, home.Root, source.Root, registry.DataRootDirectory);
    }

    [Theory]
    [InlineData("no-manifest", false)]
    [InlineData("missing-file", true)]
    [InlineData("directory", true)]
    [InlineData("malformed-json", true)]
    [InlineData("non-object", true)]
    [InlineData("invalid-shards", true)]
    [InlineData("invalid-checks", true)]
    [InlineData("unknown-option", false)]
    [InlineData("duplicate-manifest", false)]
    [InlineData("duplicate-root", false)]
    [InlineData("missing-value", false)]
    public void Compare_InvalidInput_ThrowsOwnUsageAndLeavesAllRootsUnchanged(string scenario, bool readError)
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("no-solution");
        var registry = Registry(home);
        var manifestPath = Path.Combine(source.Root, "manifest.json");
        File.WriteAllText(manifestPath, scenario switch
        {
            "malformed-json" => "{",
            "non-object" => "[]",
            "invalid-shards" => "{\"engine\":{\"maxConcurrentShards\":0}}",
            "invalid-checks" => "{\"checks\":{}}",
            _ => "{}"
        });
        string[] options = scenario switch
        {
            "no-manifest" => [],
            "missing-file" => ["--manifest", Path.Combine(source.Root, "absent.json")],
            "directory" => ["--manifest", source.Root],
            "unknown-option" => ["--manifest", manifestPath, "--unknown"],
            "duplicate-manifest" => ["--manifest", manifestPath, "--manifest", manifestPath],
            "duplicate-root" => ["--manifest", manifestPath, "--root", source.Root, "--root", source.Root],
            "missing-value" => ["--manifest"],
            _ => ["--manifest", manifestPath]
        };
        var before = Snapshot(home.Root, source.Root, registry.DataRootDirectory);
        using var output = new StringWriter();

        var error = Assert.Throws<ArgumentException>(() => ProjectCliCommand.Execute(
            ["project", "compare-manifest-engine", .. options], registry, home.Root, null, output));

        if (readError)
            Assert.StartsWith(Usage + Environment.NewLine + "Manifest could not be read: ", error.Message);
        else
            Assert.Equal(Usage, error.Message);
        Assert.Empty(output.ToString());
        AssertUnchanged(before, home.Root, source.Root, registry.DataRootDirectory);
    }

    private static void AssertLines(StringWriter output, string[] expectedPrefixes)
    {
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expectedPrefixes.Length, lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            var reasonStart = lines[index].LastIndexOf(" (", StringComparison.Ordinal);
            Assert.True(reasonStart > 0);
            Assert.Equal(expectedPrefixes[index], lines[index][..reasonStart]);
            Assert.EndsWith(")", lines[index]);
            Assert.False(string.IsNullOrWhiteSpace(lines[index][(reasonStart + 2)..^1]));
        }
    }

    private static OrchestratorProjectRegistry Registry(ProjectOnboardingFixture home)
    {
        var dataRoot = Path.Combine(home.Root, "data");
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "sentinel.txt"), "unchanged");
        return new(Path.Combine(home.Root, "registry"), dataRoot);
    }

    private static Dictionary<string, string> Snapshot(params string[] roots) => roots.Distinct(StringComparer.Ordinal)
        .SelectMany(root => Directory.Exists(root)
            ? Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root) : [])
        .Distinct(StringComparer.Ordinal).ToDictionary(path => path,
            path => Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path)));

    private static void AssertUnchanged(Dictionary<string, string> before, params string[] roots)
    {
        var after = Snapshot(roots);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, contents) in before) Assert.Equal(contents, after[path]);
    }

    private static string Minutes(TimeSpan timeout) => timeout.TotalMinutes.ToString(CultureInfo.InvariantCulture);
}
