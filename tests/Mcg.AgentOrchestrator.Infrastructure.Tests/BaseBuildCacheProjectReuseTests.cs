using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: plain library graph, private trees/cache/artifacts, hermetic child environments.
// Real builds deliberately verify compiler execution and restored bytes across worktree roots.
public sealed class BaseBuildCacheProjectReuseTests(ITestOutputHelper output) : IDisposable
{
    private const string Shared = "src/Shared/Shared.csproj";
    private const string LeafA = "src/LeafA/LeafA.csproj";
    private const string LeafB = "src/LeafB/LeafB.csproj";
    private const string Consumer = "tests/Consumer/Consumer.csproj";
    private const string External = "scripts/External/External.csproj";
    private static readonly string[] RestoreProjects = [Shared, LeafA, LeafB];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "project-reuse", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnsureAsync_LeafEditAcrossRoots_CompilesOnlyLeafAndConsumer()
    {
        var one = Path.Combine(_root, "one");
        var two = Path.Combine(_root, "two");
        Seed(one);
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        var cold = await Gate(one, "cold", cache);
        AssertMisses(cold);
        Assert.Equal(4, cold.CompilerCalls);
        CopyTree(one, two);
        File.AppendAllText(Source(two, LeafA), "\npublic class LeafEdit { }");
        var warm = await Gate(two, "leaf", cache, mainSha: new string('b', 40));
        Assert.Contains($"{Shared}=hit", warm.Summary);
        Assert.Contains($"{LeafB}=hit", warm.Summary);
        Assert.Contains($"{LeafA}=miss", warm.Summary);
        Assert.Equal(new[] { LeafA, Consumer }, BuiltProjects(warm));
        Assert.Equal(2, warm.CompilerCalls);
        foreach (var project in new[] { Shared, LeafB })
            Assert.Equal(File.ReadAllBytes(Assembly(cold.Environment, project)), File.ReadAllBytes(Assembly(warm.Environment, project)));

        File.AppendAllText(Source(two, Shared), "\npublic class SharedEdit { }");
        var shared = await Gate(two, "shared", cache);
        AssertMisses(shared);
        Assert.Equal(4, shared.CompilerCalls);

        var props = Path.Combine(two, "Directory.Build.props");
        File.WriteAllText(props, File.ReadAllText(props).Replace("</PropertyGroup>",
            "<CacheProbe>changed</CacheProbe></PropertyGroup>", StringComparison.Ordinal));
        var propsOnly = await Gate(two, "props", cache);
        AssertMisses(propsOnly);
        Assert.Equal(4, propsOnly.CompilerCalls);
    }

    [Fact]
    public async Task EnsureAsync_NonPlanReferenceMiss_BuildsReferenceBeforeDependent()
    {
        var tree = Path.Combine(_root, "tree");
        Seed(tree);
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        await Gate(tree, "cold", cache);
        WriteProject(tree, External, []);
        File.WriteAllText(Source(tree, External), "public static class ExternalValue { public static int Read() => 7; }");
        WriteProject(tree, Consumer, [LeafA, LeafB, External]);
        File.AppendAllText(Source(tree, Consumer), "\npublic class UsesExternal { public int Read() => ExternalValue.Read(); }");
        var warm = await Gate(tree, "external", cache);
        Assert.Equal(new[] { External, Consumer }, BuiltProjects(warm));
        Assert.Equal(new[] { External, Consumer }, warm.Commands.Select(args => args[2]).ToArray());
        Assert.All(warm.Commands, args => Assert.Contains("-p:BuildProjectReferences=false", args));
        Assert.Equal(2, warm.CompilerCalls);
        Assert.True(File.Exists(Assembly(warm.Environment, External)));
        Assert.True(File.Exists(Assembly(warm.Environment, Consumer)));
    }

    [Fact]
    public async Task ResolveAsync_EquivalentRoots_NormalizesOnlySourceProjectKeys()
    {
        var first = await InjectedIdentity(Path.Combine(_root, "first"));
        var second = await InjectedIdentity(Path.Combine(_root, "second"));
        Assert.True(DotnetBaseBuildCacheProjectKeys.TryGet(first.ProjectKeys, Shared, out var firstSource));
        Assert.True(DotnetBaseBuildCacheProjectKeys.TryGet(second.ProjectKeys, Shared, out var secondSource));
        Assert.Equal(firstSource, secondSource);
        Assert.True(DotnetBaseBuildCacheProjectKeys.TryGet(first.ProjectKeys, Consumer, out var firstTest));
        Assert.True(DotnetBaseBuildCacheProjectKeys.TryGet(second.ProjectKeys, Consumer, out var secondTest));
        Assert.NotEqual(firstTest, secondTest);
        Assert.Equal(first, await InjectedIdentity(Path.Combine(_root, "first")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compute_UnresolvedOrCyclicReference_OmitsDependentKeys(bool cycle)
    {
        var projects = new Dictionary<string, DotnetBaseBuildCacheProjectKeys.Inputs>
        {
            [Shared] = new([], [LeafA]),
            [LeafB] = new([], [Shared])
        };
        if (cycle) projects[LeafA] = new([], [Shared]);
        var keys = DotnetBaseBuildCacheProjectKeys.Compute(_root, [], [], projects);
        Assert.Empty(keys);
        Assert.False(DotnetBaseBuildCacheProjectKeys.TryGet(keys, Shared, out _));
        Assert.False(DotnetBaseBuildCacheProjectKeys.TryGet(keys, LeafB, out _));
    }

    private sealed record GateResult(DotnetBuildEnvironment Environment, string Summary, int CompilerCalls, List<string[]> Commands);

    private async Task<GateResult> Gate(string tree, string slot, DotnetBaseBuildCache cache, string? mainSha = null)
    {
        var environment = EnvironmentFor(slot);
        var plan = new DotnetBaseBuildCachePlan(mainSha ?? new string('a', 40), RestoreProjects,
            [Consumer], [Shared, LeafA, LeafB, Consumer]);
        var phase = new DotnetTestBuildPhase(["dotnet", "build", Consumer, "-v:minimal"], plan);
        var compilerCalls = 0;
        var commands = new List<string[]>();
        var hooks = new AcceptanceDotnetBuildPhaseHooks((args, _, prepare, supplied) =>
        {
            Assert.Same(environment, supplied);
            prepare?.Invoke(environment);
            commands.Add(args);
            // This proves missing non-plan outputs exist before compiling their consumer.
            if (args[2] == Consumer && args.Contains("-p:BuildProjectReferences=false") && File.Exists(Path.GetFullPath(External, tree)))
                Assert.True(File.Exists(Assembly(environment, External)));
            var result = Run(tree, environment, [.. args, .. environment.Arguments, "-clp:PerformanceSummary"]);
            output.WriteLine($"BUILD {slot} {args[2]} exit={result.ExitCode}\n{result.Stdout}\n{result.Stderr}");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Task Performance Summary:", result.Stdout);
            compilerCalls += Regex.Matches(result.Stdout, @"(?m)^\s*[\d,]+\s+ms\s+Csc\s+(?<calls>\d+)\s+calls\s*$")
                .Sum(match => int.Parse(match.Groups["calls"].Value, CultureInfo.InvariantCulture));
            return Task.FromResult((new AcceptanceCheckResult("fixture-build", true, result.ExitCode, null,
                environment.ArtifactsPath, "fixture", environment.LeaseId), false));
        }, _ => environment, value => value, () => cache, async (candidate, owned) =>
            await DotnetBaseBuildCacheIdentityResolver.ResolveAsync(tree, candidate.CachePlan!.RestoreProjects,
                candidate.BuildArguments, owned, args =>
                {
                    var result = Run(tree, owned, args);
                    Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(result.ExitCode, result.Stdout));
                }, ProcessEnvironment(tree, owned), candidate.CachePlan.BuildProjects));
        var completed = await AcceptanceDotnetBuildPhase.EnsureAsync(phase, slot, hooks);
        Assert.True(completed.Run.Result.Passed);
        Assert.NotEmpty(commands);
        Assert.NotNull(completed.Run.Result.ResultSummary);
        var summary = completed.Run.Result.ResultSummary!;
        Assert.DoesNotContain("unresolved-build-inputs", summary);
        output.WriteLine($"REUSE {slot} Csc={compilerCalls} {summary}");
        return new(environment, summary, compilerCalls, commands);
    }

    private async Task<DotnetBaseBuildCacheIdentity> InjectedIdentity(string tree)
    {
        foreach (var relative in new[] { Shared, Consumer, "src/Shared/Value.cs", "tests/Consumer/Value.cs",
                     "dotnet/sdk/10.0/compiler.dll", "dotnet/packs/ref.dll", "Directory.Build.props" })
        {
            var path = Path.GetFullPath(relative, tree);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, relative);
        }
        var environment = EnvironmentFor(Path.GetFileName(tree));
        var identity = await DotnetBaseBuildCacheIdentityResolver.ResolveAsync(tree, [Shared, Consumer],
            ["dotnet", "build", Consumer], environment, args =>
            {
                var project = args[2];
                var assets = Path.Combine(environment.ArtifactsPath, "obj", Path.GetFileNameWithoutExtension(project), "project.assets.json");
                Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
                File.WriteAllText(assets, JsonSerializer.Serialize(new
                {
                    packageFolders = new Dictionary<string, object> { [Path.Combine(tree, "packages")] = new { } },
                    libraries = new Dictionary<string, object>()
                }));
                var preprocess = args.SingleOrDefault(arg => arg.StartsWith("-preprocess:", StringComparison.Ordinal));
                if (preprocess is not null) File.WriteAllText(preprocess[12..], "import " + tree);
                if (!args.Any(arg => arg.StartsWith("-getProperty:", StringComparison.Ordinal)))
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                var items = args.Single(arg => arg.StartsWith("-getItem:", StringComparison.Ordinal))[9..].Split(',')
                    .ToDictionary(name => name, _ => (object)Array.Empty<object>());
                var directory = Path.GetDirectoryName(project)! + Path.DirectorySeparatorChar;
                // Match real MSBuild metadata: Directory omits RootDir, unlike FullPath.
                items["Compile"] = new[] { new { FullPath = Path.Combine(directory, "Value.cs"),
                    RootDir = Path.GetPathRoot(project), Directory = directory[Path.GetPathRoot(project)!.Length..] } };
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, JsonSerializer.Serialize(new
                {
                    Properties = new { TargetFramework = "net10.0", TargetFrameworks = "", NETCoreSdkVersion = "10.0",
                        MSBuildSDKsPath = Path.Combine(tree, "dotnet/sdk/10.0/Sdks"), ProjectAssetsFile = assets },
                    Items = items
                })));
            }, new Dictionary<string, string> { ["ROOT"] = tree, ["ESCAPED_ROOT"] = tree.Replace("\\", "\\\\", StringComparison.Ordinal) });
        return Assert.IsType<DotnetBaseBuildCacheIdentity>(identity);
    }

    private DotnetBuildEnvironment EnvironmentFor(string slot)
    {
        var owned = Path.Combine(_root, "owned");
        var artifacts = Path.Combine(owned, "artifacts", slot);
        return new(slot, owned, artifacts, Path.Combine(owned, "lock"),
            ["--artifacts-path", artifacts, "-maxcpucount:1", "-p:BuildInParallel=false", "-p:UseSharedCompilation=false"], "fixture");
    }

    private static void Seed(string tree)
    {
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "Directory.Build.props"),
            "<Project><PropertyGroup><UseSharedCompilation>false</UseSharedCompilation><Deterministic>true</Deterministic></PropertyGroup></Project>");
        WriteProject(tree, Shared, []);
        WriteProject(tree, LeafA, [Shared]);
        WriteProject(tree, LeafB, [Shared]);
        WriteProject(tree, Consumer, [LeafA, LeafB]);
        File.WriteAllText(Source(tree, Shared), "public static class SharedValue { public static int Read() => 21; }");
        File.WriteAllText(Source(tree, LeafA), "public class LeafAValue { public int Read() => SharedValue.Read(); }");
        File.WriteAllText(Source(tree, LeafB), "public class LeafBValue { public int Read() => SharedValue.Read(); }");
        File.WriteAllText(Source(tree, Consumer), "public class ConsumerValue { public int Read() => new LeafAValue().Read() + new LeafBValue().Read(); }");
    }

    private static void WriteProject(string tree, string project, string[] references)
    {
        var path = Path.GetFullPath(project, tree);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var items = string.Concat(references.Select(reference =>
            $"<ProjectReference Include=\"{Path.GetRelativePath(Path.GetDirectoryName(path)!, Path.GetFullPath(reference, tree))}\" />"));
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            "</PropertyGroup><ItemGroup>" + items + "</ItemGroup></Project>");
    }

    private static string Source(string tree, string project) => Path.Combine(tree, Path.GetDirectoryName(project)!, "Value.cs");
    private static string Assembly(DotnetBuildEnvironment environment, string project) => Directory.EnumerateFiles(
        Path.Combine(environment.ArtifactsPath, "bin", Path.GetFileNameWithoutExtension(project)),
        Path.GetFileNameWithoutExtension(project) + ".dll", SearchOption.AllDirectories).Single();
    private static string[] BuiltProjects(GateResult result) => Regex.Match(result.Summary, @"\bbuilt_projects=(\S+)")
        .Groups[1].Value.Split(',');
    private static void AssertMisses(GateResult result)
    {
        foreach (var project in RestoreProjects) Assert.Contains(project + "=miss", result.Summary);
        Assert.Equal(new[] { Shared, LeafA, LeafB, Consumer }, BuiltProjects(result));
    }

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
        return TestChildProcessCapture.Run(start, hangBound: TimeSpan.FromMinutes(5));
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var path = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(file, path);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
