using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repositories, stores, and storage; no real child starts.
public sealed class GcEnvironmentEvidenceLaunchTests
{
    [Fact]
    public void Launch_EnabledPolicy_AddsBothEntriesAndKeepsStorage()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        WritePolicy(fixture, ConductorAutonomyPolicy.Conservative with
        { BuildGcConserveMemory = true, BuildGcConserveMemoryLevel = 3, BuildGcHeapCount = 16 });
        var environment = Capture(fixture);
        Assert.Equal("3", environment["DOTNET_GCConserveMemory"]);
        Assert.Equal("10", environment["DOTNET_GCHeapCount"]);
        Assert.Equal(fixture.Storage.RootPath, environment[DotnetBuildStorageLayout.IsolatedRootOverrideVariable]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(4)]
    public void Launch_DisabledOrHeapOnly_PreservesBaseline(int? heaps)
    {
        var baselineFixture = new CompletionBuildEvidenceFixture();
        var baseline = Capture(baselineFixture);
        var fixture = new CompletionBuildEvidenceFixture();
        if (heaps is { } count)
            WritePolicy(fixture, ConductorAutonomyPolicy.Conservative with { BuildGcHeapCount = count });
        // Match all non-GC inputs across the two fixtures, including the storage root.
        var actual = Capture(fixture, baselineFixture.Storage);
        var expected = new Dictionary<string, string?>(baseline);
        if (heaps == 4) expected["DOTNET_GCHeapCount"] = "4";
        Assert.Equal(expected.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
        Assert.Equal(baseline.GetValueOrDefault("DOTNET_GCConserveMemory"),
            actual.GetValueOrDefault("DOTNET_GCConserveMemory"));
    }

    private static void WritePolicy(CompletionBuildEvidenceFixture fixture, ConductorAutonomyPolicy policy) =>
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.StatePath)!, "conductor-policy.json"), policy.ToJson());

    private static Dictionary<string, string?> Capture(CompletionBuildEvidenceFixture fixture,
        DotnetBuildStorageRoot? storage = null)
    {
        Dictionary<string, string?>? recorded = null;
        Process Start(ProcessStartInfo info)
        {
            recorded = new(info.Environment);
            throw new IOException("injected GC environment capture");
        }
        var coordinator = storage is null ? fixture.Coordinator(Start) :
            new CompletionBuildEvidenceCoordinator(fixture.StatePath, fixture.Root,
                utcNow: () => fixture.Clock.UtcNow, startProcess: Start, storageRoot: storage);
        fixture.Runner(coordinator).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Contains("injected GC environment capture", fixture.Task.CompletionBuildEvidence.Diagnostic);
        Assert.Null(fixture.Task.CompletionBuildEvidence.ChildPid);
        return Assert.IsType<Dictionary<string, string?>>(recorded);
    }
}
