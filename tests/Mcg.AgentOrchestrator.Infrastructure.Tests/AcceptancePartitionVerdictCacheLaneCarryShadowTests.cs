using Mcg.AgentOrchestrator.Core;
using Xunit;
using Fixture = Mcg.AgentOrchestrator.Infrastructure.Tests.AcceptanceLaneCarryShadowTests.Fixture;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: both caches and their journals live in fixture-owned temporary roots.
public sealed class AcceptancePartitionVerdictCacheLaneCarryShadowTests
{
    [Fact]
    public void CompleteShadows_DefersCarryAndPreservesPerClassResolverCount()
    {
        using var fixture = new Fixture();
        Seed(fixture);
        var carryCalls = 0;
        var classCalls = 0;
        var cache = CreateCache(fixture, "current", (_, baseline, candidate) =>
        {
            carryCalls++;
            Assert.Equal("tree-baseline", baseline);
            Assert.Equal("tree-current", candidate);
            return ["data/unrelated.bin"];
        }, (_, _, _) => { classCalls++; return ["docs/unrelated.md"]; });
        cache.RecordExecution(Fixture.Check(), fixture.Result());
        Assert.NotNull(cache.CompleteAttempt());
        Assert.False(File.Exists(fixture.RecordPath("current")));
        Assert.True(cache.HasPendingShadows);
        Assert.Equal(0, carryCalls);
        Assert.Equal(0, classCalls);
        cache.CompleteShadows();
        Assert.True(File.Exists(fixture.RecordPath("current")));
        Assert.False(cache.HasPendingShadows);
        Assert.Equal(1, carryCalls);
        Assert.Equal(1, classCalls);
        cache.CompleteShadows();
        Assert.Equal(1, carryCalls);
        Assert.Equal(1, classCalls);
        using var document = fixture.Read("current");
        Assert.Equal("found", Assert.Single(document.RootElement.GetProperty("lanes").EnumerateArray())
            .GetProperty("baseline_status").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThrowingCarryDelegate_CannotChangeCompletionOrReuse(bool passed)
    {
        using var controlRoot = new Fixture();
        using var failingRoot = new Fixture();
        // The identical TRX artifact makes differences in reuse receipts observable without path normalization.
        var baselineResult = controlRoot.Result();
        Seed(controlRoot, baselineResult);
        Seed(failingRoot, baselineResult);
        var controlCalls = 0;
        var failedCalls = 0;
        var control = CreateCache(controlRoot, "current", (_, _, _) => { controlCalls++; return ["data/unrelated.bin"]; });
        var failing = CreateCache(failingRoot, "current", (_, _, _) => { failedCalls++; throw new IOException("tree missing"); });
        var check = Fixture.Check();
        AssertSameReuse(control.TryReuse(check), failing.TryReuse(check));
        Assert.Equal(0, controlCalls);
        Assert.Equal(0, failedCalls);
        var execution = controlRoot.Result(passed);
        control.RecordExecution(check, execution);
        failing.RecordExecution(check, execution);
        var expected = control.CompleteAttempt();
        var actual = failing.CompleteAttempt();
        AssertSameReceipt(expected, actual);
        control.CompleteShadows();
        Assert.Null(Record.Exception(failing.CompleteShadows));
        Assert.Equal(1, controlCalls);
        Assert.Equal(1, failedCalls);
        AssertSameReceipt(expected, actual);
        using var failedRecord = failingRoot.Read("current");
        Assert.Equal("baseline-tree-missing", Assert.Single(failedRecord.RootElement.GetProperty("lanes").EnumerateArray())
            .GetProperty("baseline_status").GetString());
        var controlNext = CreateCache(controlRoot, "next");
        var failingNext = CreateCache(failingRoot, "next", (_, _, _) => throw new IOException());
        AssertSameReuse(controlNext.TryReuse(check), failingNext.TryReuse(check));
        Assert.Equal(controlNext.Misses, failingNext.Misses);
    }

    private static void Seed(Fixture fixture, AcceptanceCheckResult? result = null)
    {
        var cache = CreateCache(fixture, "baseline");
        cache.RecordExecution(Fixture.Check(), result ?? fixture.Result());
        Assert.NotNull(cache.CompleteAttempt());
        cache.CompleteShadows();
        Assert.True(File.Exists(fixture.RecordPath("baseline")));
    }

    private static AcceptancePartitionVerdictCache CreateCache(Fixture fixture, string attempt,
        Func<string, string, string, IReadOnlyList<string>>? carry = null,
        Func<string, string, string, IReadOnlyList<string>>? perClass = null) =>
        Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(Fixture.Goal, fixture.Root, [Fixture.Check()], 5, false,
                _ => $"tree-{attempt}", _ => "main-a", _ => "commit-a", () => attempt,
                () => "manifest-a", () => false, ResolveClosureHash: _ => "closure-a",
                ResolveChangedFiles: perClass ?? ((_, _, _) => ["docs/unrelated.md"]),
                ResolveLaneCarryChangedFiles: carry ?? ((_, _, _) => ["data/unrelated.bin"]))));

    private static void AssertSameReceipt(AcceptanceCheckResult? expected, AcceptanceCheckResult? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Passed, actual.Passed);
        Assert.Equal(expected.Advisory, actual.Advisory);
        Assert.Equal(expected.ResultSummary, actual.ResultSummary);
    }

    private static void AssertSameReuse(AcceptanceCheckResult? expected, AcceptanceCheckResult? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.Passed, actual.Passed);
        Assert.Equal(expected.ResultSummary, actual.ResultSummary);
        Assert.Equal(expected.TestResultAttemptId, actual.TestResultAttemptId);
        Assert.Equal(expected.TestResultPaths, actual.TestResultPaths);
        Assert.Equal(expected.TestResultIsExplicitCrossAttemptReuse, actual.TestResultIsExplicitCrossAttemptReuse);
    }
}
