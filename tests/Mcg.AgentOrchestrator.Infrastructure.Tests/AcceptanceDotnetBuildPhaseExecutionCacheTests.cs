using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: injected build runner and independently owned temporary outputs/cache.
public sealed class AcceptanceDotnetBuildPhaseExecutionCacheTests : IDisposable
{
    private const string MainSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "execution-cache", Guid.NewGuid().ToString("N"));
    private static readonly AcceptanceShardPolicySwitches Switches = new(FullShards: false, ChangeScoped: true);

    [Theory]
    [InlineData("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/Example.cs", false)]
    [InlineData("src/Mcg.AgentOrchestrator.Execution/Processes/Example.cs", true)]
    public void ExecutionChange_PartitionsEligibleProjectAndConsumers(string path, bool changed)
    {
        var phase = Create(path);
        var plan = Assert.IsType<DotnetBaseBuildCachePlan>(phase.CachePlan);
        Assert.Equal(changed, plan.BuildProjects.Contains(ExecutionProject));
        Assert.Equal(!changed, plan.RestoreProjects.Contains(ExecutionProject));
        Assert.Contains(InfrastructureProject, plan.BuildProjects);
        Assert.Contains(AppProject, plan.BuildProjects);
        Assert.Empty(plan.RestoreProjects.Intersect(plan.BuildProjects));
    }

    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("global.json")]
    [InlineData("src/Mcg.AgentOrchestrator.Execution/Mcg.AgentOrchestrator.Execution.csproj")]
    public void BuildInputChange_DisablesReuse(string path) => Assert.Null(Create(path).CachePlan);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedRestore_UsesExecutionOrFallsBackOnLostEntry(bool loseEntry)
    {
        var phase = Create("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ExampleTests.cs");
        var plan = Assert.IsType<DotnetBaseBuildCachePlan>(phase.CachePlan);
        Assert.Contains(ExecutionProject, plan.RestoreProjects);
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        var seed = Path.Combine(_root, "seed");
        foreach (var project in plan.RestoreProjects)
            WriteOutputs(seed, project, "base");
        Assert.All(cache.Publish(MainSha, seed, plan.RestoreProjects, new("fixture-v1")).Projects, receipt => Assert.Equal("published", receipt.Status));
        var environment = EnvironmentFor(Path.Combine(_root, "owned"));
        var calls = new List<string[]>();
        var callbacks = 0;
        var hooks = new AcceptanceDotnetBuildPhaseHooks((arguments, _, prepare, supplied) =>
        {
            Assert.Same(environment, supplied);
            if (prepare is not null)
            {
                callbacks++;
                if (loseEntry)
                {
                    // The runner seam gates the exact Probe -> prepared Restore boundary.
                    var manifest = Directory.EnumerateFiles(Path.Combine(_root, "cache"), "manifest.json", SearchOption.AllDirectories)
                        .Single(file => File.ReadAllText(file).Contains(ExecutionProject, StringComparison.Ordinal));
                    File.Delete(manifest);
                }
                prepare(environment);
                Assert.Equal("base", File.ReadAllText(OutputFile(environment.ArtifactsPath, ExecutionProject)));
            }
            else if (loseEntry)
            {
                Assert.False(File.Exists(OutputFile(environment.ArtifactsPath, CoreProject)));
                Assert.False(File.Exists(OutputFile(environment.ArtifactsPath, ExecutionProject)));
            }
            calls.Add(arguments);
            return Task.FromResult((new AcceptanceCheckResult("build", true, 0, null,
                environment.ArtifactsPath, "test", environment.LeaseId), false));
        }, _ => environment, label => label, () => cache, (_, _) => Task.FromResult<DotnetBaseBuildCacheIdentity?>(new("fixture-v1")));

        var result = await AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks);

        Assert.True(result.Run.Result.Passed);
        Assert.Equal(1, callbacks);
        var call = Assert.Single(calls);
        if (loseEntry)
        {
            Assert.Equal(phase.BuildArguments, call);
            Assert.Contains("Execution=miss", result.Run.Result.ResultSummary);
        }
        else
        {
            Assert.Equal(InfrastructureTestsProject, call[2]);
            Assert.Contains("Execution=hit", result.Run.Result.ResultSummary);
        }
        Assert.Same(environment, phase.BuildEnvironment);
    }

    private DotnetTestBuildPhase Create(string path) => AcceptanceDotnetBuildPhase.Create(_root,
        [new() { Type = "dotnet-test", Project = InfrastructureTestsProject }], [path],
        BuildPolicyShardPlan([path], Switches), _ => MainSha);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdBuild_InputChangePreventsPublication(bool changeInputs)
    {
        var phase = Create("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ExampleTests.cs");
        var plan = Assert.IsType<DotnetBaseBuildCachePlan>(phase.CachePlan);
        var environment = EnvironmentFor(Path.Combine(_root, "owned"));
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        var resolutions = 0;
        var builds = 0;
        var hooks = new AcceptanceDotnetBuildPhaseHooks((arguments, _, prepare, _) =>
        {
            Assert.Null(prepare);
            Assert.Equal(phase.BuildArguments, arguments);
            builds++;
            foreach (var project in plan.RestoreProjects) WriteOutputs(environment.ArtifactsPath, project, "built");
            return Task.FromResult((new AcceptanceCheckResult("build", true, 0, null), false));
        }, _ => environment, label => label, () => cache, (_, _) =>
            Task.FromResult<DotnetBaseBuildCacheIdentity?>(new(++resolutions == 2 && changeInputs ? "changed" : "original")));
        Assert.True((await AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks)).Run.Result.Passed);
        Assert.Equal(1, builds);
        Assert.Equal(2, resolutions);
        Assert.Equal(!changeInputs, cache.Probe(MainSha, plan.RestoreProjects, new("original")).AllHit);
    }

    [Fact]
    public async Task UnresolvedIdentity_UsesFullBuildWithoutProbingOrPublishing()
    {
        var phase = Create("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ExampleTests.cs");
        var environment = EnvironmentFor(Path.Combine(_root, "owned"));
        var calls = new List<string[]>();
        var hooks = new AcceptanceDotnetBuildPhaseHooks((arguments, _, prepare, _) =>
        {
            Assert.Null(prepare);
            calls.Add(arguments);
            return Task.FromResult((new AcceptanceCheckResult("build", true, 0, null), false));
        }, _ => environment, label => label,
        () => throw new InvalidOperationException("Unresolved inputs must never authorize cache access."),
        (_, _) => Task.FromResult<DotnetBaseBuildCacheIdentity?>(null));
        Assert.True((await AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks)).Run.Result.Passed);
        Assert.Equal(phase.BuildArguments, Assert.Single(calls));
    }

    private static DotnetBuildEnvironment EnvironmentFor(string path) =>
        new("owned", path, path, Path.Combine(path, "lock"), [], "owner");

    private static string OutputFile(string root, string project) =>
        Path.Combine(root, "bin", Path.GetFileNameWithoutExtension(project), "debug_net10.0", "cache.txt");

    private static void WriteOutputs(string root, string project, string value)
    {
        foreach (var kind in new[] { "bin", "obj" })
        {
            var directory = Path.Combine(root, kind, Path.GetFileNameWithoutExtension(project), "debug_net10.0");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "cache.txt"), value);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
