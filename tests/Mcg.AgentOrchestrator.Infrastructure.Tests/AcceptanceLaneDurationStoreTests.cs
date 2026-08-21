using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test instance owns a unique filesystem root and recording state is AsyncLocal.
public sealed class AcceptanceLaneDurationStoreTests : IDisposable
{
    private readonly string _root = CreateTempDirectory();

    [Fact]
    public void Resolve_ThreeSamples_UsesMedian()
    {
        var check = Check("Stable", "FullyQualifiedName~StableTests", seed: 50);
        AppendSample(check, 100);
        AppendSample(check, 2);
        AppendSample(check, 3);

        Assert.Equal(3d, Resolve(check));
    }

    [Fact]
    public void Resolve_TwoSamples_UsesSeed()
    {
        var check = Check("Sparse", "FullyQualifiedName~SparseTests", seed: 50);
        AppendSample(check, 100);
        AppendSample(check, 2);

        Assert.Equal(50d, Resolve(check));
    }

    [Fact]
    public void Resolve_LatestFive_UsesTrailingMedian()
    {
        var check = Check("Moving", "FullyQualifiedName~MovingTests", seed: 50);
        foreach (var seconds in new double[] { 1000, 2000, 1, 2, 3, 4, 5 })
        {
            AppendSample(check, seconds);
        }

        Assert.Equal(3d, Resolve(check));
    }

    [Fact]
    public void Resolve_ChangedFilter_UsesSeed()
    {
        var original = Check("Renamed", "FullyQualifiedName~OriginalTests", seed: 50);
        var changed = Check("Renamed", "FullyQualifiedName~ChangedTests", seed: 50);
        AppendSample(original, 1);
        AppendSample(original, 2);
        AppendSample(original, 3);

        Assert.Equal(2d, Resolve(original));
        Assert.Equal(50d, Resolve(changed));
    }

    [Fact]
    public void Resolve_MalformedLine_KeepsValidHistory()
    {
        var check = Check("Torn", "FullyQualifiedName~TornTests", seed: 50);
        AppendSample(check, 1);
        AppendSample(check, 2);
        AppendSample(check, 3);
        File.AppendAllText(AcceptanceLaneDurationStore.ResolveStorePath(_root), "{not-json");

        Assert.Equal(2d, Resolve(check));
    }

    [Fact]
    public void Record_InvalidOrReusedResult_DoesNotCreateJournal()
    {
        var check = Check("Ignored", "FullyQualifiedName~IgnoredTests", seed: 50);
        using (AcceptanceLaneDurationStore.PushRecordingScope(_root))
        {
            AcceptanceLaneDurationStore.Record(check, Result(passed: false), TimeSpan.FromSeconds(5));
            AcceptanceLaneDurationStore.Record(check, Result(passed: true), TimeSpan.Zero);
            AcceptanceLaneDurationStore.Record(
                check,
                Result(passed: true) with { TestResultIsExplicitCrossAttemptReuse = true },
                TimeSpan.FromSeconds(5));
            AcceptanceLaneDurationStore.Flush();
        }

        Assert.False(File.Exists(AcceptanceLaneDurationStore.ResolveStorePath(_root)));
    }

    [Fact]
    public void Dispose_WithoutFlush_DiscardsBufferedSamples()
    {
        var check = Check("Failed gate", "FullyQualifiedName~FailedGateTests", seed: 50);
        using (AcceptanceLaneDurationStore.PushRecordingScope(_root))
        {
            AcceptanceLaneDurationStore.Record(check, Result(passed: true), TimeSpan.FromSeconds(5));
        }

        Assert.False(File.Exists(AcceptanceLaneDurationStore.ResolveStorePath(_root)));
    }

    [Fact]
    public async Task Flush_ConcurrentScopes_AppendsWithoutLostLines()
    {
        var tasks = Enumerable.Range(1, 8)
            .Select(index => Task.Run(() =>
                AppendSample(
                    Check($"Concurrent {index}", $"FullyQualifiedName~Concurrent{index}Tests", seed: 50),
                    index)))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(
            tasks.Length,
            SharedJsonlFile.ReadAllLines(AcceptanceLaneDurationStore.ResolveStorePath(_root)).Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void AppendSample(
        GoalAcceptanceVerifier.AcceptanceManifestCheck check,
        double seconds)
    {
        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        AcceptanceLaneDurationStore.Record(check, Result(passed: true), TimeSpan.FromSeconds(seconds));
        AcceptanceLaneDurationStore.Flush();
    }

    private double Resolve(GoalAcceptanceVerifier.AcceptanceManifestCheck check)
    {
        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        return AcceptanceLaneDurationStore.ResolveSortSeconds(check);
    }

    private static GoalAcceptanceVerifier.AcceptanceManifestCheck Check(
        string laneName,
        string filter,
        double seed) =>
        new()
        {
            Name = $"infrastructure tests: {laneName}",
            Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", filter],
            EstimatedSerialSeconds = seed
        };

    private static AcceptanceCheckResult Result(bool passed) =>
        new("infrastructure tests: fixture", passed, passed ? 0 : 1, passed ? null : "failed");

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-lane-duration-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
