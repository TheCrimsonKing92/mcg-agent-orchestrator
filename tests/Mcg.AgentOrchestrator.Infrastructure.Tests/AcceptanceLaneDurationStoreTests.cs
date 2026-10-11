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
    public void Resolve_ChangedFilter_UsesNameLevelMedian()
    {
        var original = Check("Renamed", "FullyQualifiedName~OriginalTests", seed: 50);
        var changed = Check("Renamed", "FullyQualifiedName~ChangedTests", seed: 50);
        AppendSample(original, 1);
        AppendSample(original, 2);
        AppendSample(original, 3);

        Assert.Equal(2d, Resolve(original));
        Assert.Equal(2d, Resolve(changed));
    }

    [Fact]
    public void Resolve_ChangedFilter_FiveOldOneNew_UsesNameLevelMedian()
    {
        var original = Check("Remainder", "FullyQualifiedName~OriginalTests", seed: 50);
        var changed = Check("Remainder", "FullyQualifiedName~ChangedTests", seed: 50);
        foreach (var seconds in new double[] { 100, 200, 300, 400, 500 })
        {
            AppendSample(original, seconds);
        }
        AppendSample(changed, 600);

        // Journal order leaves 200, 300, 400, 500, 600 in the name-level window.
        Assert.Equal(400d, Resolve(changed));
        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        Assert.Equal(((double?)null, 1), AcceptanceLaneDurationStore.ResolveObservedSeconds(changed));
    }

    [Fact]
    public void Resolve_CurrentHashHasThreeSamples_IgnoresOlderHash()
    {
        var original = Check("Remainder", "FullyQualifiedName~OriginalTests", seed: 50);
        var changed = Check("Remainder", "FullyQualifiedName~ChangedTests", seed: 50);
        foreach (var seconds in new double[] { 900, 900, 900 })
        {
            AppendSample(original, seconds);
        }
        foreach (var seconds in new double[] { 1, 2, 3 })
        {
            AppendSample(changed, seconds);
        }

        // The name-level median is 3, but the current-hash median takes priority.
        Assert.Equal(2d, Resolve(changed));
        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        Assert.Equal(((double?)2d, 3), AcceptanceLaneDurationStore.ResolveObservedSeconds(changed));
    }

    [Fact]
    public void Resolve_NoSamples_UsesSeed()
    {
        Assert.Equal(50d, Resolve(Check("Empty", "FullyQualifiedName~EmptyTests", seed: 50)));
    }

    [Fact]
    public void Resolve_TwoNameLevelSamplesAcrossHashes_UsesSeed()
    {
        var original = Check("Sparse", "FullyQualifiedName~OriginalTests", seed: 50);
        var changed = Check("Sparse", "FullyQualifiedName~ChangedTests", seed: 50);
        AppendSample(original, 100);
        AppendSample(changed, 200);

        Assert.Equal(50d, Resolve(changed));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void SortEstimate_InvalidCurrentMedian_UsesValidNameSamples(double invalid)
    {
        Assert.Equal(20d, AcceptanceLaneSortEstimate.Resolve(invalid, [30, 10, 20], 50));
    }

    [Fact]
    public void SortEstimate_InvalidNameSamples_DoNotCountTowardMinimum()
    {
        Assert.Equal(50d, AcceptanceLaneSortEstimate.Resolve(null, [double.NaN, 0, -1, 10, 20], 50));
        Assert.Equal(20d, AcceptanceLaneSortEstimate.Resolve(null, [double.PositiveInfinity, 30, 10, 20, 0], 50));
    }

    [Fact]
    public void SortEstimate_FourNameSamples_UsesMiddlePairMeanWithoutMutatingInput()
    {
        double[] samples = [40, 10, 30, 20];

        Assert.Equal(25d, AcceptanceLaneSortEstimate.Resolve(null, samples, 50));
        Assert.Equal(new double[] { 40, 10, 30, 20 }, samples);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void SortEstimate_NoValidHistory_ReturnsManifestVerbatim(double manifest)
    {
        Assert.Equal(manifest, AcceptanceLaneSortEstimate.Resolve(null, [], manifest));
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

    [Fact]
    public void LocalEstimate_FiveOldOneNew_UsesNameMedianAndFiveSamples()
    {
        var original = Check("Remainder", "FullyQualifiedName~OriginalTests", seed: 50);
        var changed = Check("Remainder", "FullyQualifiedName~ChangedTests", seed: 50);
        foreach (var seconds in new double[] { 100, 200, 300, 400, 500 })
            AppendSample(original, seconds);
        AppendSample(changed, 600);

        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        var estimate = AcceptanceLaneDurationStore.ResolveLocalEstimate(changed);
        Assert.Equal(((double?)400d, 5), estimate);
        Assert.Equal(estimate.MedianSeconds, AcceptanceLaneDurationStore.ResolveSortSeconds(changed));
        Assert.Equal(((double?)null, 1), AcceptanceLaneDurationStore.ResolveObservedSeconds(changed));
    }

    [Fact]
    public void LocalEstimate_ThreeCurrentSamples_IgnoresOlderHash()
    {
        var original = Check("Remainder", "FullyQualifiedName~OriginalTests", seed: 50);
        var changed = Check("Remainder", "FullyQualifiedName~ChangedTests", seed: 50);
        foreach (var seconds in new double[] { 900, 900, 900 })
            AppendSample(original, seconds);
        foreach (var seconds in new double[] { 1, 2, 3 })
            AppendSample(changed, seconds);

        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        var estimate = AcceptanceLaneDurationStore.ResolveLocalEstimate(changed);
        Assert.Equal(((double?)2d, 3), estimate);
        Assert.Equal(estimate.MedianSeconds, AcceptanceLaneDurationStore.ResolveSortSeconds(changed));
        Assert.Equal(estimate, AcceptanceLaneDurationStore.ResolveObservedSeconds(changed));
    }

    [Fact]
    public void LocalEstimate_NoSamples_ReturnsNullAndZero()
    {
        var check = Check("Empty", "FullyQualifiedName~EmptyTests", seed: 50);
        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);

        Assert.Equal(((double?)null, 0), AcceptanceLaneDurationStore.ResolveLocalEstimate(check));
        Assert.Equal(check.EstimatedSerialSeconds, AcceptanceLaneDurationStore.ResolveSortSeconds(check));
    }

    [Fact]
    public void LocalEstimate_TwoNameSamples_KeepsCurrentHashCount()
    {
        var original = Check("Sparse", "FullyQualifiedName~OriginalTests", seed: 50);
        var changed = Check("Sparse", "FullyQualifiedName~ChangedTests", seed: 50);
        AppendSample(original, 100);
        AppendSample(changed, 200);
        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);

        Assert.Equal(((double?)null, 1), AcceptanceLaneDurationStore.ResolveLocalEstimate(changed));
        Assert.Equal(changed.EstimatedSerialSeconds, AcceptanceLaneDurationStore.ResolveSortSeconds(changed));
    }

    [Fact]
    public void LocalEstimate_NoRecordingScope_ReturnsNullAndZero()
    {
        var check = Check("Unscoped", "FullyQualifiedName~UnscopedTests", seed: 50);
        AppendSample(check, 100);
        AppendSample(check, 200);
        AppendSample(check, 300);

        Assert.Equal(((double?)null, 0), AcceptanceLaneDurationStore.ResolveLocalEstimate(check));
    }

    [Fact]
    public void LocalEstimate_InvalidNameSamples_DoNotCountTowardMinimum()
    {
        double[] sparse = [double.NaN, 0, -1, double.PositiveInfinity, double.NegativeInfinity, 10, 20];
        double[] sufficient = [double.NaN, 0, -1, double.PositiveInfinity, double.NegativeInfinity, 30, 10, 20];

        Assert.Equal(((double?)null, 2), AcceptanceLaneLocalEstimate.Resolve(null, 2, sparse));
        Assert.Equal(((double?)20d, 3), AcceptanceLaneLocalEstimate.Resolve(null, 2, sufficient));
        Assert.Equal(50d, AcceptanceLaneSortEstimate.Resolve(null, sparse, 50));
        Assert.Equal(AcceptanceLaneSortEstimate.Resolve(null, sufficient, 50),
            AcceptanceLaneLocalEstimate.Resolve(null, 2, sufficient).MedianSeconds);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void LocalEstimate_InvalidCurrentMedian_UsesValidNameSamples(double invalid)
    {
        double[] samples = [30, 10, 20];

        var estimate = AcceptanceLaneLocalEstimate.Resolve(invalid, 1, samples);

        Assert.Equal(((double?)20d, 3), estimate);
        Assert.Equal(AcceptanceLaneSortEstimate.Resolve(invalid, samples, 50), estimate.MedianSeconds);
    }

    [Fact]
    public void LocalEstimate_ValidCurrentMedian_PreservesCurrentCount()
    {
        Assert.Equal(((double?)2d, 4), AcceptanceLaneLocalEstimate.Resolve(2, 4, [900, 900, 900]));
    }

    [Fact]
    public void LocalEstimate_FourNameSamples_UsesSortMedianWithoutMutatingInput()
    {
        double[] samples = [40, 10, 30, 20];
        var original = samples.ToArray();

        var estimate = AcceptanceLaneLocalEstimate.Resolve(null, 1, samples);

        Assert.Equal(((double?)25d, 4), estimate);
        Assert.Equal(AcceptanceLaneSortEstimate.Resolve(null, samples, 50), estimate.MedianSeconds);
        Assert.Equal(original, samples);
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
