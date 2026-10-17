using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each instance owns a unique temporary root; recording scopes are AsyncLocal.
public sealed class AcceptanceCheckDurationRecordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "check-durations-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PassedChecksAndTrustedBaselineFlushExactlyOnce()
    {
        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        AcceptanceLaneDurationStore.RecordCheck("core tests", true, TimeSpan.FromSeconds(95));
        AcceptanceLaneDurationStore.RecordCheck("core tests [trusted main baseline]", true, TimeSpan.FromSeconds(60));
        AcceptanceLaneDurationStore.RecordCheck("cli tests", true, TimeSpan.FromSeconds(30));

        AcceptanceLaneDurationStore.Flush();

        var rows = ReadRows();
        Assert.Equal(3, rows.Length);
        AssertRow(rows[0], "core tests", 95);
        AssertRow(rows[1], "core tests [trusted main baseline]", 60);
        AssertRow(rows[2], "cli tests", 30);
        var original = File.ReadAllText(StorePath);
        AcceptanceLaneDurationStore.Flush();
        Assert.Equal(original, File.ReadAllText(StorePath));
    }

    [Theory]
    [InlineData("core tests", false, 95)]
    [InlineData("", true, 95)]
    [InlineData("  ", true, 95)]
    [InlineData("core tests", true, 0)]
    [InlineData("core tests", true, -1)]
    [InlineData("infrastructure tests: Remainder", true, 10)]
    [InlineData("infrastructure tests: Remainder [trusted main baseline]", true, 10)]
    public void RejectedCheckAddsNothingAfterAnEarlierFlush(string name, bool passed, double seconds)
    {
        using (AcceptanceLaneDurationStore.PushRecordingScope(_root))
        {
            AcceptanceLaneDurationStore.RecordCheck("previous check", true, TimeSpan.FromSeconds(5));
            AcceptanceLaneDurationStore.Flush();
        }
        // A fresh scope and a positive control prove rejection, rather than the one-shot Flush guard.
        using (AcceptanceLaneDurationStore.PushRecordingScope(_root))
        {
            AcceptanceLaneDurationStore.RecordCheck(name, passed, TimeSpan.FromSeconds(seconds));
            AcceptanceLaneDurationStore.RecordCheck("control check", true, TimeSpan.FromSeconds(7));
            AcceptanceLaneDurationStore.Flush();
        }

        var rows = ReadRows();
        Assert.Equal(2, rows.Length);
        AssertRow(rows[0], "previous check", 5);
        AssertRow(rows[1], "control check", 7);
    }

    [Fact]
    public void NaNDurationCannotReachTimeSpanEntryPointOrWriteARow()
    {
        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        // TimeSpan stores Int64 ticks: no TimeSpan can have a NaN or infinite TotalSeconds.
        Assert.Throws<ArgumentException>(() =>
            AcceptanceLaneDurationStore.RecordCheck("core tests", true, TimeSpan.FromSeconds(double.NaN)));
        AcceptanceLaneDurationStore.RecordCheck("control check", true, TimeSpan.FromSeconds(7));
        AcceptanceLaneDurationStore.Flush();
        AssertRow(Assert.Single(ReadRows()), "control check", 7);
    }

    [Fact]
    public void WithoutScopeDoesNotThrowWriteOrLeakIntoNextScope()
    {
        Assert.Null(Record.Exception(() =>
            AcceptanceLaneDurationStore.RecordCheck("core tests", true, TimeSpan.FromSeconds(95))));
        Assert.False(File.Exists(StorePath));
        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        AcceptanceLaneDurationStore.Flush();
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void CheckHistoryLeavesInfrastructureEstimatesUnchanged()
    {
        var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "infrastructure tests: Remainder", Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~RemainderTests"], EstimatedSerialSeconds = 300
        };
        using var empty = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        var sort = AcceptanceLaneDurationStore.ResolveSortSeconds(check);
        var observed = AcceptanceLaneDurationStore.ResolveObservedSeconds(check);
        var local = AcceptanceLaneDurationStore.ResolveLocalEstimate(check);
        Assert.Equal(300d, sort);
        Assert.Equal(((double?)null, 0), observed);
        Assert.Equal(((double?)null, 0), local);
        foreach (var seconds in new[] { 90, 100, 110 })
            AcceptanceLaneDurationStore.RecordCheck("core tests", true, TimeSpan.FromSeconds(seconds));
        AcceptanceLaneDurationStore.Flush();
        Assert.Equal(3, ReadRows().Length);

        using var reloaded = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        Assert.Equal(sort, AcceptanceLaneDurationStore.ResolveSortSeconds(check));
        Assert.Equal(observed, AcceptanceLaneDurationStore.ResolveObservedSeconds(check));
        Assert.Equal(local, AcceptanceLaneDurationStore.ResolveLocalEstimate(check));
    }

    [Theory]
    [InlineData("core tests", 95000)]
    [InlineData("core tests [trusted main baseline]", 60000)]
    [InlineData("cli tests", 30000)]
    public void CheckCompletedRecordsTheEmittedNameOutcomeAndElapsed(string name, long milliseconds)
    {
        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        var events = new List<AcceptanceGateProgress>();
        AcceptanceGateLaneProgress.CheckCompleted(null,
            new AcceptanceCheckResult(name, true, 0, null, DurationMilliseconds: milliseconds), events.Add);
        var progress = Assert.Single(events);
        Assert.Equal("check-complete", progress.Phase);
        Assert.Equal(name, progress.CurrentTarget);
        Assert.Equal("passed", progress.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), progress.Elapsed);
        AcceptanceLaneDurationStore.Flush();
        AssertRow(Assert.Single(ReadRows()), name, progress.Elapsed.TotalSeconds);
    }

    [Theory]
    [InlineData(false, false, 95000L)]
    [InlineData(true, true, 95000L)]
    [InlineData(true, false, null)]
    public void CheckCompletedDoesNotRecordFailureReuseOrMissingDuration(bool passed, bool reused, long? milliseconds)
    {
        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        var events = new List<AcceptanceGateProgress>();
        AcceptanceGateLaneProgress.CheckCompleted(null,
            new AcceptanceCheckResult("core tests", passed, passed ? 0 : 1, null,
                DurationMilliseconds: milliseconds, TestResultIsExplicitCrossAttemptReuse: reused), events.Add);
        Assert.Single(events);
        AcceptanceLaneDurationStore.RecordCheck("control check", true, TimeSpan.FromSeconds(7));
        AcceptanceLaneDurationStore.Flush();
        AssertRow(Assert.Single(ReadRows()), "control check", 7);
    }

    private string StorePath => AcceptanceLaneDurationStore.ResolveStorePath(_root);

    private JsonElement[] ReadRows() => File.ReadAllLines(StorePath).Select(line =>
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }).ToArray();

    private static void AssertRow(JsonElement row, string name, double seconds)
    {
        Assert.Equal(name, row.GetProperty("laneName").GetString());
        Assert.Equal(seconds, row.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(1, row.GetProperty("version").GetInt32());
        Assert.Equal("check", row.GetProperty("filterHash").GetString());
        Assert.NotEqual(default, row.GetProperty("recordedAt").GetDateTimeOffset());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
