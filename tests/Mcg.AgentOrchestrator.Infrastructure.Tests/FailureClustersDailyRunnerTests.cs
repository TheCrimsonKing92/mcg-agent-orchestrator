using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class FailureClustersDailyRunnerTests
{
    [Fact]
    public async Task SameDayAndRelaunchEmitOnceAndNextUtcDayEmitsAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-clusters-daily", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = Path.Combine(root, "conduct-events.log");
            var state = Path.Combine(root, FailureClustersDailyRunner.StateFileName);
            var now = FailureClusterTestData.Until.AddHours(12).ToOffset(TimeSpan.FromHours(-5));
            var windows = new List<DateTimeOffset>();
            FailureClustersDailyRunner Runner() => new(until =>
            {
                windows.Add(until);
                return FailureClusterTestData.Create();
            }, log, state, () => now, work => work());
            var first = Runner();
            Assert.True(first.OnTick());
            await first.WaitForCurrentRunAsync();
            Assert.False(first.OnTick());
            Assert.Single(File.ReadAllLines(log));
            var relaunched = Runner();
            relaunched.OnTick();
            await relaunched.WaitForCurrentRunAsync();
            Assert.Single(File.ReadAllLines(log));
            now = FailureClusterTestData.Until.AddDays(1);
            Assert.True(relaunched.OnTick());
            await relaunched.WaitForCurrentRunAsync();
            var lines = File.ReadAllLines(log);
            Assert.Equal(2, lines.Length);
            Assert.Equal(new[] { FailureClusterTestData.Until, FailureClusterTestData.Until.AddDays(1) }, windows);
            var events = new List<FailureClusterConductEvent>();
            foreach (var line in lines)
            {
                using var json = JsonDocument.Parse(line);
                var e = json.RootElement;
                var kind = e.GetProperty("eventKind").GetString()!;
                var detail = e.GetProperty("detail").GetString()!;
                Assert.Equal("failure-clusters", kind);
                Assert.StartsWith("FAILURE_CLUSTERS_DAILY day=", detail);
                Assert.Equal("outcome", e.GetProperty("operator").GetString());
                Assert.Equal("outcome", ConductEventOperatorClassifier.Classify(kind, detail));
                events.Add(new(e.GetProperty("timestamp").GetDateTimeOffset(), kind, null, detail));
            }
            Assert.Contains("day=2026-10-05", lines[0]);
            Assert.Contains("day=2026-10-06", lines[1]);
            using (var firstEvent = JsonDocument.Parse(lines[0]))
            {
                var expectedTop = string.Join(',', FailureClusterTestData.Create().Build(
                    FailureClusterTestData.Since, FailureClusterTestData.Until).Take(5).Select(r =>
                        r.Key + ":" + r.TotalCost.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
                Assert.Equal("FAILURE_CLUSTERS_DAILY day=2026-10-05 top=" + expectedTop,
                    firstEvent.RootElement.GetProperty("detail").GetString());
            }
            Assert.Empty(FailureClusterReport.Build([], events, [], FailureClusterTestData.Since, now.AddDays(1)));
            Assert.Empty(FailureClusterReport.Build([
                new("failure-clusters", "escalated at WorkspaceReady — self event", "goal", null, now)
            ], [], [], FailureClusterTestData.Since, now.AddDays(1)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LogReceiptRecoversLostStateWithoutReemitting()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-clusters-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = Path.Combine(root, "conduct-events.log");
            var state = Path.Combine(root, FailureClustersDailyRunner.StateFileName);
            var now = FailureClusterTestData.Until;
            new ConductEventLogWriter(log).Append("failure-clusters", null, "FAILURE_CLUSTERS_DAILY day=2026-10-05 top=", now);
            var runner = new FailureClustersDailyRunner(_ => throw new InvalidOperationException("Must recover before reading sources"),
                log, state, () => now, work => work());
            runner.OnTick();
            await runner.WaitForCurrentRunAsync();
            Assert.Single(File.ReadAllLines(log));
            using var json = JsonDocument.Parse(File.ReadAllText(state));
            Assert.Equal("2026-10-05", json.RootElement.GetProperty("lastEmittedDay").GetString());
            Assert.Null(ConductEventOperatorClassifier.Classify("failure-clusters", "FAILURE_CLUSTERS_DAILYISH day=2026-10-05"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
