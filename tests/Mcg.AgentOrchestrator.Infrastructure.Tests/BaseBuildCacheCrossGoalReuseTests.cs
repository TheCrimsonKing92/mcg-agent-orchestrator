using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private source trees, goal roots, outputs and cache; injected evaluation/builds.
public sealed class BaseBuildCacheCrossGoalReuseTests : IDisposable
{
    private const string Shared = "src/Shared/Shared.csproj";
    private const string MidA = "src/MidA/MidA.csproj";
    private const string MidB = "src/MidB/MidB.csproj";
    private const string Consumer = "src/Consumer/Consumer.csproj";
    private const string TestConsumer = "tests/TestConsumer/TestConsumer.csproj";
    private static readonly string[] RestoreProjects = [Shared, MidA, MidB];
    private static readonly string[] AllProjects = [Shared, MidA, MidB, Consumer];
    private static readonly string MainSha = new('a', 40);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cross-goal-cache", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ResolveAsync_DifferentGoalRoots_PreservesSourceKey()
    {
        var (first, second) = Trees();
        var firstEnvironment = EnvironmentFor("alpha");
        var secondEnvironment = EnvironmentFor("bravo");
        var firstChild = ChildEnvironment(first, firstEnvironment);
        var secondChild = ChildEnvironment(second, secondEnvironment);
        Assert.NotEqual(firstEnvironment.RootPath, secondEnvironment.RootPath);
        Assert.NotEqual(firstChild["DOTNET_CLI_HOME"], secondChild["DOTNET_CLI_HOME"]);
        var one = await Resolve(first, firstEnvironment, firstChild);
        var two = await Resolve(second, secondEnvironment, secondChild);
        foreach (var project in AllProjects)
            Assert.Equal(Key(one, project), Key(two, project));
    }

    [Fact]
    public async Task EnsureAsync_ConsumerEditAcrossGoals_RestoresThreeProjects()
    {
        var (first, second) = Trees();
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        var cold = await Gate(first, "alpha", cache);
        AssertCold(cold);
        File.AppendAllText(Source(second, Consumer), "\n// consumer edit");
        var warm = await Gate(second, "bravo", cache);
        foreach (var project in RestoreProjects)
        {
            Assert.Contains(project + "=hit", warm.Summary);
            foreach (var kind in new[] { "bin", "obj" })
                Assert.Equal(File.ReadAllBytes(Output(cold.Environment, project, kind)),
                    File.ReadAllBytes(Output(warm.Environment, project, kind)));
        }
        Assert.Equal(new[] { Consumer }, BuiltProjects(warm));
        Assert.Equal(new[] { Consumer }, warm.Builds);
        Assert.Equal(1, warm.Prepares);
    }

    [Fact]
    public async Task EnsureAsync_SharedEditAcrossGoals_InvalidatesDependents()
    {
        var (first, second) = Trees();
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        AssertCold(await Gate(first, "alpha", cache));
        File.AppendAllText(Source(second, Shared), "\n// shared edit");
        var changed = await Gate(second, "bravo", cache);
        AssertCold(changed);
    }

    [Fact]
    public async Task EnsureAsync_ExtraDotnetVariable_InvalidatesSharedProject()
    {
        var (first, second) = Trees();
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        AssertCold(await Gate(first, "alpha", cache));
        var changed = await Gate(second, "bravo", cache, extraVariable: true);
        Assert.Contains(Shared + "=miss", changed.Summary);
        Assert.Equal(AllProjects, BuiltProjects(changed));
        Assert.Equal(AllProjects, changed.Builds);
    }

    [Fact]
    public async Task ResolveAsync_TestConsumerAcrossGoals_KeepsWorktreeSpecificKey()
    {
        var (first, second) = Trees();
        var one = await Resolve(first, EnvironmentFor("alpha"));
        var two = await Resolve(second, EnvironmentFor("bravo"));
        Assert.Equal(Key(one, Shared), Key(two, Shared));
        Assert.NotEqual(Key(one, TestConsumer), Key(two, TestConsumer));
    }

    private sealed record GateResult(DotnetBuildEnvironment Environment, string Summary,
        string[] Builds, int Prepares);

    private async Task<GateResult> Gate(string tree, string goal, DotnetBaseBuildCache cache,
        bool extraVariable = false)
    {
        var environment = EnvironmentFor(goal);
        var child = ChildEnvironment(tree, environment);
        if (extraVariable) child["DOTNET_CACHE_TEST_PROBE"] = "outside-any-build-root";
        var plan = new DotnetBaseBuildCachePlan(MainSha, RestoreProjects, [Consumer], AllProjects);
        var phase = new DotnetTestBuildPhase(["dotnet", "build", Consumer, "-v:minimal"], plan);
        var builds = new List<string>();
        var prepares = 0;
        var hooks = new AcceptanceDotnetBuildPhaseHooks((args, _, prepare, supplied) =>
        {
            Assert.Same(environment, supplied);
            if (prepare is not null)
            {
                prepare(environment);
                prepares++;
                Assert.All(RestoreProjects, project => Assert.True(File.Exists(Output(environment, project, "bin"))));
            }
            var projects = prepare is null ? AllProjects : [args[2]];
            foreach (var project in projects)
            {
                builds.Add(project);
                foreach (var kind in new[] { "bin", "obj" })
                {
                    var file = Output(environment, project, kind);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, $"{goal}:{project}:{kind}");
                }
            }
            return Task.FromResult((new AcceptanceCheckResult("injected-build", true, 0, null,
                environment.ArtifactsPath, "fixture", environment.LeaseId), false));
        }, _ => environment, value => value, () => cache, (candidate, owned) =>
            ResolveRaw(tree, owned, candidate.CachePlan!.RestoreProjects, candidate.BuildArguments,
                child, candidate.CachePlan.BuildProjects));
        var completed = await AcceptanceDotnetBuildPhase.EnsureAsync(phase, goal, hooks);
        Assert.True(completed.Run.Result.Passed);
        Assert.NotEmpty(builds);
        var summary = Assert.IsType<string>(completed.Run.Result.ResultSummary);
        Assert.Contains("built_projects=", summary);
        Assert.DoesNotContain("unresolved-build-inputs", summary);
        return new(environment, summary, builds.ToArray(), prepares);
    }

    private async Task<DotnetBaseBuildCacheIdentity> Resolve(string tree, DotnetBuildEnvironment environment,
        IReadOnlyDictionary<string, string>? child = null) =>
        Assert.IsType<DotnetBaseBuildCacheIdentity>(await ResolveRaw(tree, environment,
            [.. RestoreProjects, TestConsumer], ["dotnet", "build", Consumer, "-v:minimal"],
            child ?? ChildEnvironment(tree, environment), [Consumer]));

    private static Task<DotnetBaseBuildCacheIdentity?> ResolveRaw(string tree,
        DotnetBuildEnvironment environment, IReadOnlyList<string> projects, string[] arguments,
        IReadOnlyDictionary<string, string> child, IReadOnlyList<string> buildProjects) =>
        DotnetBaseBuildCacheIdentityResolver.ResolveAsync(tree, projects, arguments, environment, args =>
        {
            var project = args[2];
            var id = Path.GetRelativePath(tree, project).Replace('\\', '/');
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
            items["Compile"] = new[] { new { FullPath = Path.Combine(directory, "Value.cs"),
                RootDir = Path.GetPathRoot(project), Directory = directory[Path.GetPathRoot(project)!.Length..] } };
            items["ProjectReference"] = References(id).Select(reference => new { FullPath = Path.GetFullPath(reference, tree) }).ToArray();
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, JsonSerializer.Serialize(new
            {
                Properties = new { TargetFramework = "net10.0", TargetFrameworks = "", NETCoreSdkVersion = "10.0",
                    MSBuildSDKsPath = Path.Combine(tree, "dotnet/sdk/10.0/Sdks"), ProjectAssetsFile = assets },
                Items = items
            })));
        }, child, buildProjects);

    private (string First, string Second) Trees()
    {
        var first = Path.Combine(_root, "trees", "one");
        var second = Path.Combine(_root, "trees", "two");
        Seed(first);
        Seed(second);
        return (first, second);
    }

    private static void Seed(string tree)
    {
        foreach (var project in AllProjects.Append(TestConsumer))
        {
            var path = Path.GetFullPath(project, tree);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "<Project />");
            File.WriteAllText(Source(tree, project), "// " + project);
        }
        foreach (var relative in new[] { "Directory.Build.props", "dotnet/sdk/10.0/compiler.dll", "dotnet/packs/ref.dll" })
        {
            var path = Path.GetFullPath(relative, tree);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, relative);
        }
    }

    private static string[] References(string project) => project switch
    {
        MidA or MidB => [Shared],
        Consumer or TestConsumer => [MidA, MidB],
        Shared => [],
        _ => throw new InvalidOperationException("Unexpected injected project: " + project)
    };

    private DotnetBuildEnvironment EnvironmentFor(string goal)
    {
        var root = Path.Combine(_root, "goals", goal);
        var artifacts = Path.Combine(root, "artifacts");
        return new(goal, root, artifacts, Path.Combine(root, "lock"), ["--artifacts-path", artifacts], "fixture");
    }

    private Dictionary<string, string> ChildEnvironment(string tree, DotnetBuildEnvironment environment)
    {
        var values = new Dictionary<string, string?> { ["NUGET_PACKAGES"] = Path.Combine(_root, "packages") };
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(values, tree, environment.RootPath);
        return values.ToDictionary(pair => pair.Key, pair => pair.Value ?? "");
    }

    private static string Key(DotnetBaseBuildCacheIdentity identity, string project)
    {
        Assert.True(DotnetBaseBuildCacheProjectKeys.TryGet(identity.ProjectKeys, project, out var key));
        return key;
    }

    private static string Source(string tree, string project) => Path.Combine(tree, Path.GetDirectoryName(project)!, "Value.cs");
    private static string Output(DotnetBuildEnvironment environment, string project, string kind) =>
        Path.Combine(environment.ArtifactsPath, kind, Path.GetFileNameWithoutExtension(project), "fixture.bin");
    private static string[] BuiltProjects(GateResult result) =>
        Regex.Match(result.Summary, @"\bbuilt_projects=(\S+)").Groups[1].Value.Split(',');

    private static void AssertCold(GateResult result)
    {
        foreach (var project in RestoreProjects) Assert.Contains(project + "=miss", result.Summary);
        Assert.Equal(AllProjects, BuiltProjects(result));
        Assert.Equal(AllProjects, result.Builds);
        Assert.Equal(0, result.Prepares);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
