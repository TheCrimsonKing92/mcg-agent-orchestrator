using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;
using ManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each test owns its temporary directory and uses an injected SHA resolver.
public sealed class AcceptanceDotnetBuildPhasePlanTests : IDisposable
{
    private readonly string _worktreePath = Path.Combine(Path.GetTempPath(), $"build-phase-plan-{Guid.NewGuid():N}");
    private static readonly string[] ChangedFiles = ["src/Mcg.AgentOrchestrator.Core/Application/Goal.cs"];
    private static readonly string[] ExpectedCacheableProjects =
    [
        CoreProject, ProvidersProject, OperatorCommsProject, InfrastructureProject, AppProject,
        CoreTestsProject, InfrastructureTestsProject, TestSupportProject, ProviderEnvironmentTestsProject, CliTestsProject
    ];

    public AcceptanceDotnetBuildPhasePlanTests() => Directory.CreateDirectory(_worktreePath);

    [Fact]
    public void CachePlanUsesResolvedShaAndPartitionsClosureInCacheableOrder()
    {
        var resolverCalls = new List<string>();
        var phase = AcceptanceDotnetBuildPhase.Create(_worktreePath, Checks(), ChangedFiles, ScopedPlan(), path =>
        {
            resolverCalls.Add(path);
            return "abc1234";
        });

        var plan = Assert.IsType<DotnetBaseBuildCachePlan>(phase.CachePlan);
        Assert.Equal(new[] { _worktreePath }, resolverCalls);
        Assert.Equal("abc1234", plan.MainSha);
        Assert.Equal(ExpectedCacheableProjects, plan.CacheableProjects);
        Assert.Equal(new[] { CoreProject, InfrastructureTestsProject }, plan.BuildProjects);
        Assert.Equal(new[]
        {
            ProvidersProject, OperatorCommsProject, InfrastructureProject, AppProject,
            CoreTestsProject, TestSupportProject, ProviderEnvironmentTestsProject, CliTestsProject
        }, plan.RestoreProjects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingChangedFilesSkipCachePlanningAndShaResolution(bool useNull)
    {
        var resolverCalls = 0;
        var phase = AcceptanceDotnetBuildPhase.Create(_worktreePath, Checks(), useNull ? null : [], ScopedPlan(), _ =>
        {
            resolverCalls++;
            return "abc1234";
        });

        Assert.Null(phase.CachePlan);
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public void FullPlanSkipsCachePlanningAndShaResolution()
    {
        var resolverCalls = 0;
        var phase = AcceptanceDotnetBuildPhase.Create(_worktreePath, Checks(), ChangedFiles,
            PolicyShardPlan.Full("full plan", ScopedPlan().DependencyClosure), _ =>
            {
                resolverCalls++;
                return "abc1234";
            });

        Assert.Null(phase.CachePlan);
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public void MissingShaDisablesCachePlan()
    {
        var resolverCalls = 0;
        var phase = AcceptanceDotnetBuildPhase.Create(_worktreePath, Checks(), ChangedFiles, ScopedPlan(), _ =>
        {
            resolverCalls++;
            return null;
        });

        Assert.Null(phase.CachePlan);
        Assert.Equal(1, resolverCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteCacheableClosureDisablesCachePlan(bool includeAcceptanceCheck)
    {
        var checks = Checks(includeAcceptanceCheck);
        var subsetPhase = AcceptanceDotnetBuildPhase.Create(_worktreePath, checks, ChangedFiles, ScopedPlan(), _ => "abc1234");
        var subsetPlan = Assert.IsType<DotnetBaseBuildCachePlan>(subsetPhase.CachePlan);
        Assert.Equal(includeAcceptanceCheck, subsetPlan.CacheableProjects.Contains(AcceptanceTestsProject));
        var completeClosure = new HashSet<string>(subsetPlan.CacheableProjects, StringComparer.OrdinalIgnoreCase);
        var resolverCalls = 0;

        var phase = AcceptanceDotnetBuildPhase.Create(_worktreePath, checks, ChangedFiles,
            PolicyShardPlan.Scoped("complete closure", completeClosure), _ =>
            {
                resolverCalls++;
                return "abc1234";
            });

        Assert.Null(phase.CachePlan);
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public void SolutionCheckPreservesCommandBuilderArguments()
    {
        var check = new ManifestCheck
        {
            Type = "dotnet-test",
            Project = "Mcg.AgentOrchestrator.sln",
            Arguments = ["--configuration", "Release", "--filter", "FullyQualifiedName~BuildPhase", "--no-restore"]
        };
        var phase = AcceptanceDotnetBuildPhase.Create(_worktreePath, [check], ChangedFiles, ScopedPlan(), _ => "abc1234");

        Assert.Equal(AcceptanceCheckCommandBuilder.BuildDotnetTestBuildArguments(check), phase.BuildArguments);
    }

    private static ManifestCheck[] Checks(bool includeAcceptanceCheck = false) => includeAcceptanceCheck
        ? [new() { Type = "dotnet-test", Project = InfrastructureTestsProject },
           new() { Type = "dotnet-test", Project = AcceptanceTestsProject }]
        : [new() { Type = "dotnet-test", Project = InfrastructureTestsProject }];

    private static PolicyShardPlan ScopedPlan() => PolicyShardPlan.Scoped("scoped closure",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            InfrastructureTestsProject, CoreProject, "src/NonCacheable/NonCacheable.csproj"
        });

    public void Dispose() => Directory.Delete(_worktreePath, recursive: true);
}
