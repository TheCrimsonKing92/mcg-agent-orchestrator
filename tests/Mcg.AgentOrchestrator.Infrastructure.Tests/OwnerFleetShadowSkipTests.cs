using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.Text;

// Parallel-safe: each test owns its filesystem ledger and fixed clock.
public sealed class OwnerFleetShadowSkipTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-18T18:00:00Z");

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(100)]
    public void RecentSkips_InclusiveBoundary_PreserveFullSentenceAtEveryWidth(int width)
    {
        using var ledger = new Ledger([Now.AddMinutes(-1), Now.AddHours(-3), Now.AddHours(-23),
            Now.AddHours(-24), Now.AddHours(-24).AddSeconds(-1), Now.AddDays(-3)]);
        var report = RemoteExecutorReportReader.Read(ledger.Workspace, null, 10, Now.AddHours(-24));
        Assert.Equal(4, report.NoExecutorFreeCount);
        Assert.Equal(Now.AddMinutes(-1), report.NoExecutorFreeLastAt);
        var model = OwnerFleetViewModelBuilder.Load(ledger.Workspace, new OwnerConsoleHarness().Liveness, new Clock());
        Assert.Equal(4, model.Host.NoExecutorFreeCount);
        var diagram = OwnerFleetDiagram.Render(model, width);
        var rows = diagram.Split('\n');
        Assert.All(rows, row => Assert.Equal(width, row.GetColumns()));
        var stamp = TimeZoneInfo.ConvertTime(Now.AddMinutes(-1), TimeZoneInfo.Local)
            .ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
        var contents = string.Join(" ", rows.Select(row => row.Trim('│', '║', ' ')));
        Assert.Contains($"shadow skipped, no free slot: 4 in last 24h (last {stamp})", contents);
        Assert.DoesNotContain("no executor free", diagram);

        var allTime = RemoteExecutorReportReader.Read(ledger.Workspace, null, 10);
        Assert.Equal(6, allTime.NoExecutorFreeCount);
        Assert.Equal(2, Assert.Single(allTime.Executors).Attempts);
        var expected = Assert.Single(allTime.Executors);
        var actual = Assert.Single(report.Executors);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(JsonSerializer.Serialize(OwnerFleetViewModelBuilder.Build(allTime, false, TimeZoneInfo.Local).Executors),
            JsonSerializer.Serialize(model.Executors));
    }

    [Fact]
    public void BoundaryRow_OneSecondOlder_IsExcluded()
    {
        using var ledger = new Ledger([Now.AddHours(-24)]);
        Assert.Equal(1, RemoteExecutorReportReader.Read(ledger.Workspace, null, 10, Now.AddHours(-24)).NoExecutorFreeCount);
        Assert.Equal(0, RemoteExecutorReportReader.Read(ledger.Workspace, null, 10, Now.AddHours(-24).AddSeconds(1)).NoExecutorFreeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldOrAbsentSkips_OmitSummary(bool old)
    {
        using var ledger = new Ledger(old ? [Now.AddHours(-24).AddSeconds(-1), Now.AddDays(-3)] : [], attempts: old);
        var model = OwnerFleetViewModelBuilder.Load(ledger.Workspace, new OwnerConsoleHarness().Liveness, new Clock());
        Assert.Equal(0, model.Host.NoExecutorFreeCount);
        Assert.Null(model.Host.NoExecutorFreeLastAt);
        var diagram = OwnerFleetDiagram.Render(model);
        Assert.DoesNotContain("shadow skipped", diagram);
        Assert.DoesNotContain("no executor free", diagram);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Ledger : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "shadow-skip-" + Guid.NewGuid().ToString("N"));
        internal OrchestratorWorkspace Workspace { get; }

        internal Ledger(IReadOnlyList<DateTimeOffset> skips, bool attempts = true)
        {
            var directory = Path.Combine(_root, ".orchestrator");
            Directory.CreateDirectory(directory);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            File.WriteAllText(Path.Combine(directory, "remote-lane-executors.json"),
                """{"executors":[{"id":"executor","slots":2}],"lanes":[]}""");
            var rows = skips.Select((at, index) => Row(at, "", "skip-" + index, "shadow-skipped-no-executor"));
            if (attempts) rows = rows.Concat([Row(Now.AddDays(-4), "executor", "old", "accepted"), Row(Now.AddHours(-1), "executor", "recent", "fallback")]);
            File.WriteAllLines(Path.Combine(directory, RemoteExecutorHealthLedger.FileName), rows);
        }

        private static string Row(DateTimeOffset at, string executor, string attempt, string outcome) => JsonSerializer.Serialize(new
        { observed_at = at, executor_id = executor, gate_attempt_id = attempt, lane = "lane", outcome, reason = (string?)null });

        public void Dispose() => Directory.Delete(_root, true);
    }
}
