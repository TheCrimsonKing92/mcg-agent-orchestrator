using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: owned roots; the real ledger appends only to this case's store.
public sealed class GateLaneReceiptSlotTimingTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("receipt-slot-").FullName;

    [Theory]
    [InlineData(1250.5, 3000.25)]
    [InlineData(0, 0)]
    public void Record_SidecarPresent_EmitsTotalsOnlyOnMeasuredRow(double wait, double held)
    {
        var trx = Path.Combine(root, "lane.trx");
        File.WriteAllText(trx, """
            <TestRun><Results><UnitTestResult testName="Example.Tests.Method" outcome="Passed" duration="00:00:05" /></Results></TestRun>
            """);
        var timingPath = LaneSlotTimingReader.PathFor(trx);
        File.WriteAllText(timingPath, JsonSerializer.Serialize(new
        {
            test_name = "Example.Tests.Method", wait_ms = wait, held_ms = held,
            slot_count = 2, process_id = Environment.ProcessId
        }) + Environment.NewLine);
        var result = new AcceptanceCheckResult("Alpha", true, 0, null, TestResultPaths: [trx]);
        GateLaneReceiptLedger.Record(root, "goal", "Alpha", result,
            TimeSpan.FromSeconds(10), DateTimeOffset.UnixEpoch);
        File.Delete(timingPath);
        GateLaneReceiptLedger.Record(root, "goal", "Alpha", result,
            TimeSpan.FromSeconds(10), DateTimeOffset.UnixEpoch.AddSeconds(1));

        var lines = File.ReadAllLines(GateLaneReceiptLedger.ResolveStorePath(root));
        Assert.Equal(2, lines.Length);
        using var measured = JsonDocument.Parse(lines[0]);
        using var absent = JsonDocument.Parse(lines[1]);
        Assert.Equal(wait, measured.RootElement.GetProperty("slot_wait_ms").GetDouble());
        Assert.Equal(held, measured.RootElement.GetProperty("slot_held_ms").GetDouble());
        Assert.Equal(2, measured.RootElement.GetProperty("slot_count").GetInt32());
        Assert.False(measured.RootElement.TryGetProperty("slot_timing_unavailable_reason", out _));
        foreach (var name in new[] { "slot_wait_ms", "slot_held_ms", "slot_count" })
            Assert.False(absent.RootElement.TryGetProperty(name, out _));
        var diagnostic = absent.RootElement.GetProperty("slot_timing_unavailable_reason").GetString();
        Assert.Contains("slot-timing-unreadable:", diagnostic);
        Assert.Contains(timingPath, diagnostic);
        Assert.Contains("FileNotFoundException", diagnostic);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
