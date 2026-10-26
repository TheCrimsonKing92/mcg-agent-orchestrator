using ArchUnitNET.Loader;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;
using Architecture = ArchUnitNET.Domain.Architecture;

// Parallel-safe: private counting caches and event gates; Process only publishes immutable shared models.
public sealed class SharedArchitectureCacheTests
{
    [Fact]
    public void TwoCallersOfOneAssemblySetShareOneModelAndOneBuild()
    {
        var builds = 0;
        var cache = new SharedArchitectureCache(assemblies =>
        {
            Interlocked.Increment(ref builds);
            return new ArchLoader().LoadAssemblies(assemblies).Build();
        });

        var first = CallerA(cache);
        var second = CallerB(cache);
        var duplicate = cache.GetOrBuild(typeof(ProviderSmokeTester).Assembly, typeof(ProviderSmokeTester).Assembly);

        Assert.Same(first, second);
        Assert.Same(first, duplicate);
        Assert.Equal(1, builds);
        Assert.Equal(1, cache.BuildCount(typeof(ProviderSmokeTester).Assembly));
    }

    [Fact]
    public async Task ConcurrentCallersOfOneSetShareOneBuild()
    {
        var builds = 0;
        var cache = new SharedArchitectureCache(assemblies =>
        {
            Interlocked.Increment(ref builds);
            return new ArchLoader().LoadAssemblies(assemblies).Build();
        });
        using var ready = new CountdownEvent(16);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            ready.Signal();
            await release.Task;
            return cache.GetOrBuild(typeof(ProviderSmokeTester).Assembly, typeof(DiscordDecisionApplier).Assembly);
        })).ToArray();
        bool reachedGate;
        try
        {
            reachedGate = ready.Wait(TimeSpan.FromSeconds(30));
        }
        finally
        {
            release.TrySetResult();
        }
        var results = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(reachedGate, "Architecture callers did not reach the release gate.");
        Assert.Equal(16, results.Length);
        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Equal(1, builds);
        Assert.Equal(1, cache.BuildCount(typeof(ProviderSmokeTester).Assembly, typeof(DiscordDecisionApplier).Assembly));
    }

    [Fact]
    public void DifferentAssemblySetsGetDifferentModels()
    {
        var builds = 0;
        var cache = new SharedArchitectureCache(assemblies =>
        {
            Interlocked.Increment(ref builds);
            return new ArchLoader().LoadAssemblies(assemblies).Build();
        });
        var providers = typeof(ProviderSmokeTester).Assembly;
        var operatorComms = typeof(DiscordDecisionApplier).Assembly;
        var providerModel = cache.GetOrBuild(providers);
        var operatorModel = cache.GetOrBuild(operatorComms);
        var combinedModel = cache.GetOrBuild(providers, operatorComms);

        Assert.NotSame(providerModel, operatorModel);
        Assert.NotSame(providerModel, combinedModel);
        Assert.NotSame(operatorModel, combinedModel);
        Assert.Equal(3, builds);
        Assert.Equal(1, cache.BuildCount(providers));
        Assert.Equal(1, cache.BuildCount(operatorComms));
        Assert.Equal(1, cache.BuildCount(providers, operatorComms));
        Assert.NotEmpty(providerModel.Types);
        Assert.All(providerModel.Types, type => Assert.Equal(providers.FullName, type.Assembly.FullName));
        Assert.Equal(new[] { providers.FullName, operatorComms.FullName }.Order(StringComparer.Ordinal),
            combinedModel.Types.Select(type => type.Assembly.FullName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ProcessCacheServesTheFourAppClassesOneModel()
    {
        var application = SharedArchitectureCache.Get(typeof(GoalMonitoringQuery).Assembly);
        var conductor = SharedArchitectureCache.Get(typeof(BoardFillCliGoalIntake).Assembly);
        var console = SharedArchitectureCache.Get(typeof(AttentionAnswerHandlerAdapter).Assembly);
        var orchestration = SharedArchitectureCache.Get(typeof(OrphanFixtureReaper).Assembly);

        Assert.Same(application, conductor);
        Assert.Same(application, console);
        Assert.Same(application, orchestration);
        Assert.Equal(1, SharedArchitectureCache.Process.BuildCount(typeof(GoalMonitoringQuery).Assembly));
    }

    private static Architecture CallerA(SharedArchitectureCache cache) => cache.GetOrBuild(typeof(ProviderSmokeTester).Assembly);
    private static Architecture CallerB(SharedArchitectureCache cache) => cache.GetOrBuild(typeof(ProviderSmokeTester).Assembly);
}
