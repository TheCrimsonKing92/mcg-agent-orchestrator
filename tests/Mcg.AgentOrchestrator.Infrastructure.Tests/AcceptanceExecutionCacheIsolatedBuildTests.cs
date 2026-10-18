using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: minimal purpose-built Core -> Execution -> MTP fixture, owned outputs/cache,
// explicitly hermetic children, no machine-global process operations. Child capture owns cleanup.
public sealed class AcceptanceExecutionCacheIsolatedBuildTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "execution-real", Guid.NewGuid().ToString("N"));
    private readonly string _repository = RepositoryRoot();
    private readonly List<string> _buildOutputs = [];

    [Fact]
    public async Task ColdThenWarm_ProveExecutionProvenanceAndRejectStaleInputs()
    {
        var tree = Path.Combine(_root, "tree");
        Seed(tree, 42);
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        var cold = await Build(tree, "cold", cache, importOrder: "targets-later");
        Assert.Contains("Execution=miss", cold.Summary);
        Assert.Contains("miss_reasons=Core=not-found,Execution=not-found", cold.Summary);
        var coldHash = Hash(Assembly(cold.Environment, ExecutionProject));
        ValidateReceiptAndTests(tree, cold.Environment, "cold");

        var warm = await Build(tree, "warm", cache, expectedRestoredHash: coldHash, importOrder: "tie");
        Assert.Contains("Execution=hit", warm.Summary);
        Assert.Contains("miss_reasons=none", warm.Summary);
        Assert.Equal(coldHash, Hash(Assembly(warm.Environment, ExecutionProject)));
        ValidateReceiptAndTests(tree, warm.Environment, "warm");
        // The consumer is compiled in both runs. Compare diagnostic code, severity, source location,
        // project and message, not exit code alone. PathMap makes the Execution PE hash meaningful.
        Assert.Equal(Diagnostics(_buildOutputs[0]), Diagnostics(_buildOutputs[1]));
        Assert.NotEmpty(Diagnostics(_buildOutputs[0]));

        // Deliberately leave the scoped closure unchanged: identity must catch even a missed diff.
        File.WriteAllText(Path.Combine(tree, Path.GetDirectoryName(ExecutionProject)!, "Value.cs"),
            "public static class ExecutionValue { public static int Read() => CoreValue.Read() * 4; }");
        WriteTest(tree, 84);
        var changed = await Build(tree, "changed", cache);
        Assert.Contains("Execution=miss", changed.Summary);
        Assert.Contains("Execution=not-found[Mcg.AgentOrchestrator.Execution:items+Mcg.AgentOrchestrator.Execution:project-tree]", changed.Summary);
        Assert.Contains("Core=not-found[Mcg.AgentOrchestrator.Execution:items+Mcg.AgentOrchestrator.Execution:project-tree]", changed.Summary);
        Assert.NotEqual(coldHash, Hash(Assembly(changed.Environment, ExecutionProject)));
        ValidateReceiptAndTests(tree, changed.Environment, "changed");

        var sibling = Path.Combine(_root, "sibling");
        Seed(sibling, 84);
        File.WriteAllText(Path.Combine(sibling, Path.GetDirectoryName(CoreProject)!, "Value.cs"),
            "public static class CoreValue { public static int Read() => 42; }");
        var siblingEnvironment = EnvironmentFor("sibling");
        CopyTree(Path.GetDirectoryName(Assembly(warm.Environment, InfrastructureTestsProject))!,
            Path.Combine(siblingEnvironment.ArtifactsPath, "bin", Path.GetFileNameWithoutExtension(InfrastructureTestsProject), "debug"));
        var rejected = ValidateReceipt(sibling, siblingEnvironment, expectValid: false);
        Assert.Contains("does not match", rejected.Stdout + rejected.Stderr);
        var rebuilt = await Build(sibling, "sibling", cache);
        Assert.Contains("Execution=miss", rebuilt.Summary);
        ValidateReceiptAndTests(sibling, rebuilt.Environment, "sibling");
    }

    [Fact]
    public async Task ImportWriteOrder_KeepsIdentity_ImportContentChangesIt()
    {
        var tree = Path.Combine(_root, "tree");
        Seed(tree, 42);
        var orderTie = await Resolve("order-tie", "tie");
        var targetsLater = await Resolve("order-targets-later", "targets-later");
        Assert.Equal(orderTie.Identity.Fingerprint, targetsLater.Identity.Fingerprint);
        Assert.Equal(orderTie.Identity.Components, targetsLater.Identity.Components);
        var tieAgain = await Resolve("order-tie-again", "tie");
        Assert.Equal(orderTie.Identity.Fingerprint, tieAgain.Identity.Fingerprint);
        Assert.Equal(orderTie.Identity.Components, tieAgain.Identity.Components);
        Assert.Equal(orderTie.Commands, tieAgain.Commands);
        var importContent = await Resolve("import-content", "tie", changeImport: true);
        Assert.NotEqual(orderTie.Identity.Fingerprint, importContent.Identity.Fingerprint);
        Assert.Equal("Mcg.AgentOrchestrator.Core:imports",
            DotnetBaseBuildCacheMissDetail.DifferingComponents(orderTie.Identity.Components, importContent.Identity.Components));

        async Task<(DotnetBaseBuildCacheIdentity Identity, int Commands)> Resolve(string slot, string order, bool changeImport = false)
        {
            var environment = EnvironmentFor(slot);
            var commands = 0;
            var identity = await DotnetBaseBuildCacheIdentityResolver.ResolveAsync(tree, [CoreProject, ExecutionProject],
                ["dotnet", "build", InfrastructureTestsProject, "-v:minimal"], environment, args =>
                {
                    commands++;
                    var result = Run(tree, environment, args);
                    Assert.Equal(0, result.ExitCode);
                    if (args.Contains("-t:Restore")) ApplyImportOrder(environment, order, changeImport);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(result.ExitCode, result.Stdout));
                }, ProcessEnvironment(tree, environment));
            Assert.Equal(6, commands);
            var resolved = Assert.IsType<DotnetBaseBuildCacheIdentity>(identity);
            output.WriteLine("ISOLATED_EXECUTION_IDENTITY " + JsonSerializer.Serialize(new
            {
                slot, fingerprint = resolved.Fingerprint, components = resolved.Components
            }));
            return (resolved, commands);
        }
    }

    private static void ApplyImportOrder(DotnetBuildEnvironment environment, string? order, bool changeImport = false)
    {
        if (order is null) return;
        Assert.True(order is "tie" or "targets-later", $"Unknown import order: {order}");
        var transformed = 0;
        foreach (var project in new[] { CoreProject, ExecutionProject })
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var prefix = Path.Combine(environment.ArtifactsPath, "obj", name, $"{name}.csproj.nuget.g.");
            var props = prefix + "props";
            var targets = prefix + "targets";
            if (!File.Exists(props) || !File.Exists(targets)) continue;
            if (changeImport && project == CoreProject)
            {
                const string probe = "<!-- import content probe -->";
                var content = File.ReadAllText(props);
                if (!content.Contains(probe, StringComparison.Ordinal))
                {
                    var closing = content.LastIndexOf("</Project>", StringComparison.Ordinal);
                    Assert.True(closing >= 0, $"Missing closing Project tag in {props}");
                    File.WriteAllText(props, content.Insert(closing, probe));
                }
            }
            var propsTime = File.GetLastWriteTimeUtc(props);
            File.SetLastWriteTimeUtc(targets, order == "tie" ? propsTime : propsTime.AddSeconds(1));
            Assert.Equal(order == "tie" ? propsTime : propsTime.AddSeconds(1), File.GetLastWriteTimeUtc(targets));
            transformed++;
        }
        Assert.True(transformed > 0, "Restore produced no fixture import pairs to order.");
    }

    private async Task<(DotnetBuildEnvironment Environment, string Summary)> Build(
        string tree, string slot, DotnetBaseBuildCache cache, string? expectedRestoredHash = null, string? importOrder = null)
    {
        var environment = EnvironmentFor(slot);
        var plan = new DotnetBaseBuildCachePlan(new string('a', 40), [CoreProject, ExecutionProject],
            [InfrastructureTestsProject], [CoreProject, ExecutionProject, InfrastructureTestsProject]);
        var phase = new DotnetTestBuildPhase(["dotnet", "build", InfrastructureTestsProject, "-v:minimal"], plan);
        var restored = false;
        var sourceInputs = Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories)
            .OrderBy(file => Path.GetRelativePath(tree, file), StringComparer.Ordinal)
            .Select(file => new { Path = Path.GetRelativePath(tree, file), Sha256 = Hash(file) }).ToArray();
        output.WriteLine("ISOLATED_EXECUTION_INPUTS " + JsonSerializer.Serialize(new
        {
            slot, tree, projects = plan.RestoreProjects, configuration = "Debug", targetFramework = "net10.0",
            buildArguments = phase.BuildArguments, environmentArguments = environment.Arguments,
            sourceIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sourceInputs)))),
            sourceInputs, artifacts = environment.ArtifactsPath,
            note = "Fixture inputs only; cache compatibility fingerprints below bind evaluated inputs, environment, packages and toolchain contents."
        }));
        var buildMilliseconds = 0d;
        var identityMilliseconds = 0d;
        var identityCommandMilliseconds = 0d;
        var cacheRestoreMilliseconds = 0d;
        var commandIndex = 0;
        var hooks = new AcceptanceDotnetBuildPhaseHooks((arguments, _, prepare, supplied) =>
        {
            Assert.Same(environment, supplied);
            var preparation = Stopwatch.StartNew();
            prepare?.Invoke(environment);
            preparation.Stop();
            if (prepare is not null) cacheRestoreMilliseconds += preparation.Elapsed.TotalMilliseconds;
            if (prepare is not null)
            {
                Assert.NotNull(expectedRestoredHash);
                Assert.Equal(expectedRestoredHash, Hash(Assembly(environment, ExecutionProject)));
                restored = true;
            }
            var result = MeasureCommand([.. arguments, .. environment.Arguments], "build");
            _buildOutputs.Add(result.Stdout);
            Assert.Equal(0, result.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            return Task.FromResult((new AcceptanceCheckResult("fixture-build", true, result.ExitCode, null,
                environment.ArtifactsPath, "fixture", environment.LeaseId), false));
        }, _ => environment, name => name, () => cache, async (candidate, owned) =>
        {
            var watch = Stopwatch.StartNew();
            var identity = await DotnetBaseBuildCacheIdentityResolver.ResolveAsync(tree, candidate.CachePlan!.RestoreProjects,
                candidate.BuildArguments, owned, args =>
                {
                    var result = MeasureCommand(args, "identity");
                    if (args.Contains("-t:Restore") && result.ExitCode == 0) ApplyImportOrder(owned, importOrder);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(result.ExitCode, result.Stdout));
                }, ProcessEnvironment(tree, owned));
            watch.Stop();
            identityMilliseconds += watch.Elapsed.TotalMilliseconds;
            output.WriteLine("ISOLATED_EXECUTION_IDENTITY " + JsonSerializer.Serialize(new
            {
                slot, fingerprint = identity?.Fingerprint, components = identity?.Components, elapsedMs = watch.Elapsed.TotalMilliseconds
            }));
            return identity;
        });
        var pipeline = Stopwatch.StartNew();
        DotnetTestBuildPhaseResult completed;
        try
        {
            completed = await AcceptanceDotnetBuildPhase.EnsureAsync(phase, slot, hooks);
        }
        finally
        {
            pipeline.Stop();
            output.WriteLine("ISOLATED_EXECUTION_COST " + JsonSerializer.Serialize(new
            {
                slot, cachePipelineElapsedMs = pipeline.Elapsed.TotalMilliseconds, buildCommandElapsedMs = buildMilliseconds,
                identityElapsedMs = identityMilliseconds, identityCommandElapsedMs = identityCommandMilliseconds,
                identityNonCommandElapsedMs = identityMilliseconds - identityCommandMilliseconds,
                cacheRestoreElapsedMs = cacheRestoreMilliseconds,
                note = "Wall times include process startup/capture. Identity non-command time includes hashing, evaluation processing and measurement output, not compiler time. MSBuild cumulative task/target times are rounded, overlapping observations, not additive wall-time components. Cache probe/publication timing is unavailable separately. Fixture results do not prove production speedup."
            }));
        }
        Assert.True(completed.Run.Result.Passed);
        Assert.Equal(expectedRestoredHash is not null, restored);
        Assert.NotNull(completed.Run.Result.ResultSummary);
        output.WriteLine($"ISOLATED_EXECUTION_CACHE tree={tree} slot={slot} {completed.Run.Result.ResultSummary}");
        return (environment, completed.Run.Result.ResultSummary!);

        TestChildProcessCapture.Result MeasureCommand(string[] arguments, string kind)
        {
            var isRestore = arguments.Contains("-t:Restore");
            // Keep evaluation JSON intact. PerformanceSummary only changes console logging.
            var measuredArguments = kind == "build" || isRestore
                ? arguments.Append("-clp:PerformanceSummary").ToArray() : arguments;
            var watch = Stopwatch.StartNew();
            var result = Run(tree, environment, measuredArguments);
            watch.Stop();
            if (kind == "build") buildMilliseconds += watch.Elapsed.TotalMilliseconds;
            else identityCommandMilliseconds += watch.Elapsed.TotalMilliseconds;
            output.WriteLine("ISOLATED_EXECUTION_COMMAND " + JsonSerializer.Serialize(new
            {
                slot, index = ++commandIndex, kind, project = arguments[2], arguments = measuredArguments,
                elapsedMs = watch.Elapsed.TotalMilliseconds, result.ExitCode,
                compiler = Performance(result.Stdout, "Task", "Csc"),
                restore = Performance(result.Stdout, "Target", "Restore"),
                timingSource = "MSBuild PerformanceSummary; null means unavailable/unparsed, zero means no matching row in a parsed section. Times cover this command including project references, not exclusive project costs. Csc task time includes compiler task overhead."
            }));
            if (arguments.Any(argument => argument.StartsWith("-getProperty:", StringComparison.Ordinal)) && result.ExitCode == 0)
            {
                using var evaluation = JsonDocument.Parse(result.Stdout);
                var properties = evaluation.RootElement.GetProperty("Properties");
                output.WriteLine("ISOLATED_EXECUTION_TOOLCHAIN " + JsonSerializer.Serialize(new
                {
                    slot, project = arguments[2],
                    properties = new[] { "Configuration", "TargetFramework", "RuntimeIdentifier", "NETCoreSdkVersion", "MSBuildSDKsPath" }
                        .ToDictionary(name => name, name => properties.GetProperty(name).GetString())
                }));
            }
            return result;
        }
    }

    private static object? Performance(string output, string section, string name)
    {
        var table = Regex.Match(output, $@"(?m)^{section} Performance Summary:\s*\r?\n(?<rows>(?:[^\r\n]*\r?\n)*?)(?=\r?\n|\z)");
        if (!table.Success) return null;
        var rows = Regex.Matches(table.Groups["rows"].Value,
            @"(?m)^\s*(?<ms>[\d,]+)\s+ms\s+(?<name>\S+)\s+(?<calls>\d+)\s+calls\s*$");
        if (rows.Count == 0) return null;
        var matching = rows.Where(row => row.Groups["name"].Value == name).ToArray();
        return new
        {
            cumulativeMs = matching.Sum(row => double.Parse(row.Groups["ms"].Value, CultureInfo.InvariantCulture)),
            calls = matching.Sum(row => int.Parse(row.Groups["calls"].Value, CultureInfo.InvariantCulture))
        };
    }

    private DotnetBuildEnvironment EnvironmentFor(string slot)
    {
        var owned = Path.Combine(_root, "owned");
        var artifacts = Path.Combine(owned, "artifacts", slot);
        return new(slot, owned, artifacts, Path.Combine(owned, "lock"),
            ["--artifacts-path", artifacts, "-maxcpucount:1", "-p:BuildInParallel=false", "-p:UseSharedCompilation=false",
             $"-p:PathMap={artifacts}=/_/out%2C{Path.Combine(_root, "tree")}=/_/source"], "fixture");
    }

    private void Seed(string tree, int expected)
    {
        Directory.CreateDirectory(tree);
        File.Copy(Path.Combine(_repository, "Directory.Build.targets"), Path.Combine(tree, "Directory.Build.targets"));
        File.WriteAllText(Path.Combine(tree, "Directory.Build.props"),
            "<Project><PropertyGroup><UseSharedCompilation>false</UseSharedCompilation><Deterministic>true</Deterministic></PropertyGroup></Project>");
        WriteProject(tree, CoreProject, "");
        WriteProject(tree, ExecutionProject, $"<ProjectReference Include=\"{Path.GetFullPath(CoreProject, tree)}\" />");
        WriteProject(tree, InfrastructureTestsProject,
            $"<ProjectReference Include=\"{Path.GetFullPath(ExecutionProject, tree)}\" />" +
            "<PackageReference Include=\"xunit.v3.mtp-v2\" Version=\"3.2.2\" />" +
            "<PackageReference Include=\"Microsoft.Testing.Extensions.TrxReport\" Version=\"2.3.2\" />", testProject: true);
        File.WriteAllText(Path.Combine(tree, Path.GetDirectoryName(CoreProject)!, "Value.cs"),
            "public static class CoreValue { public static int Read() => 21; }");
        File.WriteAllText(Path.Combine(tree, Path.GetDirectoryName(ExecutionProject)!, "Value.cs"),
            "public static class ExecutionValue { public static int Read() => CoreValue.Read() * 2; }");
        WriteTest(tree, expected);
    }

    private static void WriteProject(string tree, string project, string references, bool testProject = false)
    {
        var path = Path.GetFullPath(project, tree);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            "<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>" +
            (testProject ? "<OutputType>Exe</OutputType><IsTestProject>true</IsTestProject><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>" : "") +
            $"</PropertyGroup><ItemGroup>{references}</ItemGroup></Project>");
    }

    private static void WriteTest(string tree, int expected) => File.WriteAllText(
        Path.Combine(tree, Path.GetDirectoryName(InfrastructureTestsProject)!, "FixtureTests.cs"),
        "#warning CacheDiagnostic\npublic sealed class FixtureTests { [Xunit.Fact] public void ReadsExecution() => " +
        $"Xunit.Assert.Equal({expected}, ExecutionValue.Read()); }}");

    private void ValidateReceiptAndTests(string tree, DotnetBuildEnvironment environment, string label)
    {
        ValidateReceipt(tree, environment, expectValid: true);
        var results = Path.Combine(_root, "results", label);
        Directory.CreateDirectory(results);
        var run = Run(tree, environment, ["dotnet", Assembly(environment, InfrastructureTestsProject),
            "--report-trx", "--report-trx-filename", "fixture.trx", "--results-directory", results,
            "--no-ansi", "--progress", "off"]);
        Assert.Equal(0, run.ExitCode);
        var trx = XDocument.Load(Path.Combine(results, "fixture.trx"));
        var ns = trx.Root!.Name.Namespace;
        var counters = Assert.Single(trx.Descendants(ns + "Counters"));
        Assert.Equal("1", counters.Attribute("executed")!.Value);
        Assert.Equal("1", counters.Attribute("passed")!.Value);
        Assert.Equal("0", counters.Attribute("failed")!.Value);
        var result = Assert.Single(trx.Descendants(ns + "UnitTestResult"));
        Assert.Equal("Passed", result.Attribute("outcome")!.Value);
        Assert.Contains("ReadsExecution", result.Attribute("testName")!.Value);
    }

    private TestChildProcessCapture.Result ValidateReceipt(string tree, DotnetBuildEnvironment environment, bool expectValid)
    {
        var script = Path.Combine(_root, "receipt-check.ps1");
        File.WriteAllText(script, """
            param($Module, $Directory, $Root, $Project)
            $ErrorActionPreference = 'Stop'
            Import-Module $Module -Force
            try {
                $null = Assert-MtpBuildReceipt -Directory $Directory -ManagedAssemblyLeaf 'Mcg.AgentOrchestrator.Infrastructure.Tests.dll' -RepositoryRoot $Root -ProjectPath $Project -Configuration Debug -TargetFramework net10.0 -RuntimeIdentifier '' -ExpectedSourcePaths @((Join-Path (Split-Path $Project) 'FixtureTests.cs'))
                Write-Output 'RECEIPT_VALID'
            } catch { Write-Output $_.Exception.Message; exit 24 }
            """);
        var result = Run(tree, environment, [WorkerShell.Executable, "-NoProfile", "-NonInteractive", "-File", script,
            Path.Combine(_repository, "scripts/MtpTestRunner.psm1"), Path.GetDirectoryName(Assembly(environment, InfrastructureTestsProject))!,
            tree, Path.GetFullPath(InfrastructureTestsProject, tree)]);
        Assert.Equal(expectValid ? 0 : 24, result.ExitCode);
        if (expectValid) Assert.Contains("RECEIPT_VALID", result.Stdout);
        return result;
    }

    private static string Assembly(DotnetBuildEnvironment environment, string project) =>
        Directory.EnumerateFiles(Path.Combine(environment.ArtifactsPath, "bin", Path.GetFileNameWithoutExtension(project)),
            $"{Path.GetFileNameWithoutExtension(project)}.dll", SearchOption.AllDirectories).Single();

    private static string[] Diagnostics(string output) => Regex.Matches(output,
        @"[^\r\n]*FixtureTests.cs\(\d+,\d+\): warning CS1030:[^\r\n]+")
        .Select(match => match.Value.Trim()).Distinct().Order(StringComparer.Ordinal).ToArray();

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static Dictionary<string, string> ProcessEnvironment(string tree, DotnetBuildEnvironment environment)
    {
        var values = new ProcessStartInfo().Environment.ToDictionary(pair => pair.Key, pair => pair.Value);
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(values, tree, environment.RootPath);
        values["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        return values.ToDictionary(pair => pair.Key, pair => pair.Value ?? "");
    }

    private static TestChildProcessCapture.Result Run(string tree, DotnetBuildEnvironment environment, string[] args)
    {
        var start = new ProcessStartInfo(args[0]) { WorkingDirectory = tree, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args.Skip(1)) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var pair in ProcessEnvironment(tree, environment)) start.Environment[pair.Key] = pair.Value;
        return TestChildProcessCapture.Run(start, hangBound: TimeSpan.FromMinutes(3));
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var path = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(file, path, overwrite: true);
        }
    }

    private static string RepositoryRoot([CallerFilePath] string source = "") =>
        VerifiedRepositoryRoot.Resolve(source, Environment.GetEnvironmentVariable);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
