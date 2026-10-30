using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique roots, fake owned launch callbacks, no real process or shared store.
public sealed class AcceptanceAttemptGcEnvironmentTests
{
    [Fact]
    public void Solo_PropagatesOrchestratorDataRootAfterHermeticScrub_AndLanesScrubItAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var environment = EnvironmentFor(Launch(root, focused: false));
            Assert.Equal(OrchestratorDataRoot.Resolve().RootDirectory, environment[OrchestratorDataRoot.EnvironmentVariable]);
            GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(environment, root);
            Assert.False(environment.ContainsKey(OrchestratorDataRoot.EnvironmentVariable));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Solo_CapturedControls_ApplyToGateAndFocusedChildren(bool focused)
    {
        var root = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var attempt = Launch(root, focused);
            Assert.Equal(focused ? ConductorParallelAcceptanceAttemptCoordinator.PreReviewEvidenceDispatchKind :
                ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind, attempt.Kind);
            var baseline = EnvironmentFor(attempt);
            var enabled = EnvironmentFor(attempt with { PolicyJson = (ConductorAutonomyPolicy.Conservative with
            { BuildGcConserveMemory = true, BuildGcConserveMemoryLevel = 3, BuildGcHeapCount = 16 }).ToJson() });
            Assert.Equal("3", enabled["DOTNET_GCConserveMemory"]);
            Assert.Equal("10", enabled["DOTNET_GCHeapCount"]);
            var heapOnly = EnvironmentFor(attempt with
            { PolicyJson = (ConductorAutonomyPolicy.Conservative with { BuildGcHeapCount = 4 }).ToJson() });
            var expected = new Dictionary<string, string?>(baseline) { ["DOTNET_GCHeapCount"] = "4" };
            Assert.Equal(expected.OrderBy(p => p.Key), heapOnly.OrderBy(p => p.Key));
            var disabled = EnvironmentFor(attempt with { PolicyJson = (ConductorAutonomyPolicy.Conservative with
            { BuildGcConserveMemory = false, BuildGcHeapCount = 0 }).ToJson() });
            Assert.Equal(baseline.OrderBy(p => p.Key), disabled.OrderBy(p => p.Key));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void Grouped_CapturedControls_ApplyAndInvalidJsonKeepsBaseline()
    {
        var root = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}");
        var storage = new DotnetBuildStorageRoot(Path.Combine(root, "build-storage"));
        var attempt = new ConductorGroupedGateAttempt(
            "attempt", "cohort", [], new string('a', 40), new string('b', 40),
            "manifest", "identity", DateTimeOffset.UnixEpoch, 0, 91001,
            Path.Combine(root, "attempt.json"), Path.Combine(root, "result.json"),
            Path.Combine(root, "exit"), Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"), root, ConductorAutonomyPolicy.Conservative.ToJson());
        Dictionary<string, string?> EnvironmentForPolicy(string json) => new(
            ConductorGroupedGateAttemptCoordinator.BuildOwnedProcessStartInfo(
                attempt with { PolicyJson = json }, storage, "dotnet", ["app.dll"]).Environment);
        var baseline = EnvironmentForPolicy(ConductorAutonomyPolicy.Conservative.ToJson());
        var enabled = EnvironmentForPolicy((ConductorAutonomyPolicy.Conservative with
        { BuildGcConserveMemory = true, BuildGcConserveMemoryLevel = 3, BuildGcHeapCount = 16 }).ToJson());
        Assert.Equal("3", enabled["DOTNET_GCConserveMemory"]);
        Assert.Equal("10", enabled["DOTNET_GCHeapCount"]);
        var heapOnly = EnvironmentForPolicy((ConductorAutonomyPolicy.Conservative with { BuildGcHeapCount = 4 }).ToJson());
        var expected = new Dictionary<string, string?>(baseline) { ["DOTNET_GCHeapCount"] = "4" };
        Assert.Equal(expected.OrderBy(p => p.Key), heapOnly.OrderBy(p => p.Key));
        var disabled = EnvironmentForPolicy((ConductorAutonomyPolicy.Conservative with
        { BuildGcConserveMemory = false, BuildGcHeapCount = 0 }).ToJson());
        Assert.Equal(baseline.OrderBy(p => p.Key), disabled.OrderBy(p => p.Key));
        Assert.Equal(baseline.OrderBy(p => p.Key), EnvironmentForPolicy("not json").OrderBy(p => p.Key));
    }

    [Fact]
    public void HermeticScrub_GcEntries_PassThroughToLanes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}");
        var environment = new Dictionary<string, string?>
        { ["DOTNET_GCConserveMemory"] = "3", ["DOTNET_GCHeapCount"] = "10" };
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(environment, root);
        Assert.Equal("3", environment["DOTNET_GCConserveMemory"]);
        Assert.Equal("10", environment["DOTNET_GCHeapCount"]);
    }

    private static Dictionary<string, string?> EnvironmentFor(ConductorParallelAcceptanceAttempt attempt) => new(
        ConductorParallelAcceptanceAttemptCoordinator.BuildOwnedProcessStartInfo(
            attempt, null, "dotnet", ["Mcg.AgentOrchestrator.App.dll"]).Environment);

    private static ConductorParallelAcceptanceAttempt Launch(string root, bool focused)
    {
        var goal = new Goal(GoalId.New(), "GC attempt environment",
            [new TaskSpec(TaskId.New(), "Verify launch", AgentRole.Developer)]);
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Feature.cs"], "branch", "main");
        ConductorParallelAcceptanceAttempt? captured = null;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, "main", executionDirectory: root,
            isProcessAlive: _ => true, launchOwnedProcess: launch =>
            {
                captured = launch.Attempt;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(8700);
            });
        if (focused)
            _ = coordinator.EvaluateFocusedEvidence(candidate, ConductorAutonomyPolicy.Conservative, "GC evidence",
                (_, _, _, _) => throw new InvalidOperationException("Fake launch must not execute checks."));
        else
            _ = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative,
                (_, _, _, _, _) => throw new InvalidOperationException("Fake launch must not execute checks."));
        return Assert.IsType<ConductorParallelAcceptanceAttempt>(captured);
    }
}
