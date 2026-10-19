using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Fixture = Mcg.AgentOrchestrator.Infrastructure.Tests.AcceptanceLaneCarryShadowTests.Fixture;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: unique fixture trees, scoped private caches and injected timestamps.
public sealed class AcceptanceLaneCarryShadowBudgetTests
{
    private static readonly string[] NormalProperties = ["goal_id", "attempt_id", "candidate_tree", "completed_at",
        "elapsed_ms", "skipped_baseline_files", "lanes", "summary"];

    [Fact]
    public async Task UnrelatedRoot_BlockedExtract_DoesNotConsumeShadowBudget()
    {
        using var fixture = new Fixture();
        var cache = new AcceptanceTestClassSourceCache();
        using var scope = AcceptanceTestClassSourceCache.Use(cache);
        fixture.Seed("baseline");
        fixture.SetCompletion("baseline", DateTimeOffset.UnixEpoch);
        var otherRoot = Path.Combine(fixture.Root, "unrelated");
        Directory.CreateDirectory(otherRoot);
        var otherFile = Path.Combine(otherRoot, "Other.cs");
        File.WriteAllText(otherFile, "class Other {}");
        long timestamp = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var blocked = Task.Run(() => cache.Extract(otherRoot, [otherFile], _ =>
        {
            entered.SetResult();
            release.Wait();
            Interlocked.Add(ref timestamp, 11 * Stopwatch.Frequency);
            return new AcceptanceTestClassSourceScanner.FileExtract([], []);
        }));
        Task? completion = null;
        var calls = 0;
        try
        {
            await TestHangGuard.WaitAsync(entered.Task, "unrelated-root extract entered");
            var shadow = fixture.Shadow("current", (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return ["data/unrelated.bin"];
            }, () => Volatile.Read(ref timestamp));
            shadow.Observe(Fixture.Check(), "lane", fixture.Result());
            completion = Task.Run(shadow.Complete);
            await TestHangGuard.WaitAsync(completion, "shadow wrote its record while unrelated extract remained blocked");

            Assert.False(blocked.IsCompleted);
            Assert.True(File.Exists(fixture.RecordPath("current")));
            using var document = fixture.Read("current");
            var record = document.RootElement;
            var lane = Assert.Single(record.GetProperty("lanes").EnumerateArray());
            Assert.Equal("found", lane.GetProperty("baseline_status").GetString());
            Assert.False(record.TryGetProperty("budget_exceeded_phase", out _));
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            release.Set();
            await TestHangGuard.WaitAsync(blocked, "unrelated-root extract completed after release");
            if (completion is not null) await TestHangGuard.WaitAsync(completion, "shadow completion finished");
        }
    }

    [Fact]
    public void Diff_ExceedsBudget_WritesPhaseRecordAndPreservesBaseline()
    {
        using var fixture = new Fixture();
        fixture.Seed("baseline");
        fixture.SetCompletion("baseline", DateTimeOffset.UnixEpoch);
        long timestamp = 0;
        var calls = 0;
        var shadow = fixture.Shadow("current", (_, _, _) =>
        {
            calls++;
            Interlocked.Add(ref timestamp, 11 * Stopwatch.Frequency);
            return ["data/unrelated.bin"];
        }, () => Volatile.Read(ref timestamp));
        shadow.Observe(Fixture.Check(), "lane", fixture.Result());

        var log = AsyncLocalConsoleRouter.Capture(() => Assert.Null(Record.Exception(shadow.Complete)));

        Assert.Equal(1, calls);
        Assert.Contains("LANE_CARRY_SHADOW_UNAVAILABLE budget-exceeded:diff", log);
        Assert.True(File.Exists(fixture.RecordPath("current")));
        using var document = fixture.Read("current");
        var record = document.RootElement;
        AssertProperties(record, ["goal_id", "attempt_id", "candidate_tree", "completed_at", "elapsed_ms",
            "budget_exceeded_phase", "lanes"]);
        Assert.Equal(Fixture.Goal.Value, record.GetProperty("goal_id").GetString());
        Assert.Equal("current", record.GetProperty("attempt_id").GetString());
        Assert.Equal("tree-current", record.GetProperty("candidate_tree").GetString());
        Assert.Equal("diff", record.GetProperty("budget_exceeded_phase").GetString());
        Assert.Equal(11000, record.GetProperty("elapsed_ms").GetInt64());
        Assert.Empty(record.GetProperty("lanes").EnumerateArray());
        // Make the budget record eligible for reading regardless of wall-clock resolution.
        fixture.SetCompletion("current", DateTimeOffset.UnixEpoch.AddDays(1));

        var next = fixture.Shadow("next");
        next.Observe(Fixture.Check(), "lane", fixture.Result());
        next.Complete();
        using var following = fixture.Read("next");
        Assert.Equal(0, following.RootElement.GetProperty("skipped_baseline_files").GetInt32());
        var lane = Assert.Single(following.RootElement.GetProperty("lanes").EnumerateArray());
        Assert.Equal("baseline", lane.GetProperty("baseline_attempt").GetString());
    }

    [Fact]
    public void Diff_UnderBudget_MatchesFrozenClockRecordExceptTiming()
    {
        using var frozenFixture = new Fixture();
        using var advancingFixture = new Fixture();
        using var frozen = RunFailedCarry(frozenFixture, 0);
        using var advancing = RunFailedCarry(advancingFixture, 9);
        AssertProperties(frozen.RootElement, NormalProperties);
        AssertProperties(advancing.RootElement, NormalProperties);
        Assert.False(frozen.RootElement.TryGetProperty("budget_exceeded_phase", out _));
        Assert.False(advancing.RootElement.TryGetProperty("budget_exceeded_phase", out _));
        Assert.Equal(0, frozen.RootElement.GetProperty("elapsed_ms").GetInt64());
        Assert.Equal(9000, advancing.RootElement.GetProperty("elapsed_ms").GetInt64());
        var frozenNode = JsonNode.Parse(frozen.RootElement.GetRawText())!.AsObject();
        var advancingNode = JsonNode.Parse(advancing.RootElement.GetRawText())!.AsObject();
        foreach (var node in new[] { frozenNode, advancingNode })
        {
            node.Remove("completed_at");
            node.Remove("elapsed_ms");
        }
        Assert.True(JsonNode.DeepEquals(frozenNode, advancingNode));
    }

    private static JsonDocument RunFailedCarry(Fixture fixture, int elapsedSeconds)
    {
        fixture.Seed("baseline");
        fixture.SetCompletion("baseline", DateTimeOffset.UnixEpoch);
        long timestamp = 0;
        var calls = 0;
        var shadow = fixture.Shadow("current", (_, _, _) =>
        {
            calls++;
            Interlocked.Add(ref timestamp, elapsedSeconds * Stopwatch.Frequency);
            return ["data/unmentioned.bin"];
        }, () => Volatile.Read(ref timestamp));
        shadow.Observe(Fixture.Check(), "lane", fixture.Result(passed: false));
        shadow.Complete();
        Assert.Equal(1, calls);
        var document = fixture.Read("current");
        var lane = Assert.Single(document.RootElement.GetProperty("lanes").EnumerateArray());
        Assert.Equal("found", lane.GetProperty("baseline_status").GetString());
        Assert.Equal("failed", lane.GetProperty("outcome").GetString());
        Assert.All(lane.GetProperty("tiers").EnumerateObject(), tier =>
        {
            Assert.True(tier.Value.GetProperty("would_carry").GetBoolean());
            Assert.True(tier.Value.GetProperty("false_carry").GetBoolean());
        });
        return document;
    }

    private static void AssertProperties(JsonElement record, string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal),
            record.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
}
