using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: owned temporary roots and execution-context-scoped memory probes.
public sealed class GateLaneReceiptTests
{
    private const string Resource = "RESOURCE phase=gate cpu_ms=91234 peak_mem_bytes=1288490188 io_bytes=0 accounting_source=live";

    [Theory]
    [InlineData("", "live")]
    [InlineData("Passed: 12; ", "live")]
    [InlineData("Passed: 12; ", "duplicate")]
    public void FromSummary_ResourceReceipt_PreservesMeasurements(string prefix, string source)
    {
        var receipt = Parse(prefix + Resource.Replace("source=live", "source=" + source));
        Assert.Equal("Alpha", receipt.LaneName);
        Assert.Equal(1, receipt.RunOrdinal);
        AssertAvailable(123456, receipt.WallMilliseconds);
        AssertAvailable(91234, receipt.CpuMilliseconds);
        AssertAvailable(1288490188, receipt.PeakMemoryBytes);
        AssertAvailable(4096, receipt.HostAvailableMemoryMb);
        Assert.Equal(source, receipt.AccountingSource);
    }

    [Theory]
    [InlineData(null, "no-resource-receipt")]
    [InlineData("Passed: 1", "no-resource-receipt")]
    [InlineData("RESOURCE phase=gate cpu_ms=0 peak_mem_bytes=0 accounting_source=accounting-unavailable", "accounting-unavailable")]
    public void FromSummary_MissingAccounting_PreservesUnavailable(string? summary, string reason)
    {
        var receipt = Parse(summary);
        AssertUnavailable(reason, receipt.CpuMilliseconds);
        AssertUnavailable(reason, receipt.PeakMemoryBytes);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("")]
    public void FromSummary_InvalidCpu_LeavesMemoryAvailable(string cpu)
    {
        var receipt = Parse(Resource.Replace("cpu_ms=91234", "cpu_ms=" + cpu));
        AssertUnavailable("invalid-cpu-ms", receipt.CpuMilliseconds);
        AssertAvailable(1288490188, receipt.PeakMemoryBytes);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("")]
    public void FromSummary_InvalidMemory_LeavesCpuAvailable(string memory)
    {
        var receipt = Parse(Resource.Replace("peak_mem_bytes=1288490188", "peak_mem_bytes=" + memory));
        AssertUnavailable("invalid-peak-mem-bytes", receipt.PeakMemoryBytes);
        AssertAvailable(91234, receipt.CpuMilliseconds);
    }

    [Fact]
    public void FromSummary_MultipleReceipts_UsesLastGateReceipt()
    {
        var receipt = Parse(Resource.Replace("cpu_ms=91234", "cpu_ms=1") + "; " + Resource + "; later text");
        AssertAvailable(91234, receipt.CpuMilliseconds);
        var invalidWall = GateLaneReceipt.FromSummary("Alpha", 0, TimeSpan.FromMilliseconds(-1), Resource,
            GateLoadSample.Available(1));
        AssertUnavailable("invalid-wall-ms", invalidWall.WallMilliseconds);
    }

    [Fact]
    public void Record_RealAppender_RoundTripsAvailableAndUnavailableFields()
    {
        var root = Directory.CreateTempSubdirectory("lane-receipt-").FullName;
        var now = new DateTimeOffset(2026, 10, 17, 1, 0, 0, TimeSpan.Zero);
        try
        {
            using (GateHostHealthProbe.PushPhysicalAvailableMbProbe(() => GateLoadSample.Available(8192)))
                GateLaneReceiptLedger.Record(root, "goal", "Alpha",
                    new AcceptanceCheckResult("Alpha", true, 0, null)
                    { ResultSummary = Resource, TestResultRunOrdinal = 1 }, TimeSpan.FromMilliseconds(123456), now);
            using (GateHostHealthProbe.PushPhysicalAvailableMbProbe(() => throw new InvalidOperationException()))
                GateLaneReceiptLedger.Record(root, "goal", "Remainder",
                    new AcceptanceCheckResult("Remainder", false, 7, null)
                    { ResultSummary = "RESOURCE phase=gate cpu_ms=0 peak_mem_bytes=0 accounting_source=accounting-unavailable" },
                    TimeSpan.FromMilliseconds(99), now.AddSeconds(1));

            var path = GateLaneReceiptLedger.ResolveStorePath(root);
            Assert.Equal(Path.Combine(HostLedgerStateDirectory.Resolve(root), "gate-lane-receipts.jsonl"), path);
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            using var first = JsonDocument.Parse(lines[0]);
            using var second = JsonDocument.Parse(lines[1]);
            AssertRow(first.RootElement, "Alpha", 1, 123456, "live", now);
            AssertRow(second.RootElement, "Remainder", 0, 99, "accounting-unavailable", now.AddSeconds(1));
            AssertField(first.RootElement, "cpu_ms", 91234, null);
            AssertField(first.RootElement, "peak_mem_bytes", 1288490188, null);
            AssertField(first.RootElement, "host_available_memory_mb", 8192, null);
            AssertField(second.RootElement, "cpu_ms", null, "accounting-unavailable");
            AssertField(second.RootElement, "peak_mem_bytes", null, "accounting-unavailable");
            AssertField(second.RootElement, "host_available_memory_mb", null, "probe-error:InvalidOperationException");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Record_ThrowingAppender_ReturnsNormallyAfterInvokingAppender()
    {
        var root = Directory.CreateTempSubdirectory("lane-receipt-failure-").FullName;
        using var memory = GateHostHealthProbe.PushPhysicalAvailableMbProbe(() => GateLoadSample.Available(100));
        var calls = 0;
        try
        {
            GateLaneReceiptLedger.Record(root, "goal", "Alpha", new("Alpha", false, 7, null),
                TimeSpan.Zero, DateTimeOffset.UtcNow, (_, _) => { calls++; throw new IOException(); });
            Assert.Equal(1, calls);
        }
        finally { Directory.Delete(root, true); }
    }

    private static GateLaneReceipt Parse(string? summary) => GateLaneReceipt.FromSummary(
        "Alpha", 1, TimeSpan.FromMilliseconds(123456), summary, GateLoadSample.Available(4096));

    private static void AssertAvailable(double value, GateLoadSample sample)
    {
        Assert.True(sample.IsAvailable);
        Assert.Equal(value, sample.Value);
        Assert.Null(sample.UnavailableReason);
    }

    private static void AssertUnavailable(string reason, GateLoadSample sample)
    {
        Assert.False(sample.IsAvailable);
        Assert.Null(sample.Value);
        Assert.Equal(reason, sample.UnavailableReason);
    }

    private static void AssertRow(JsonElement row, string lane, int run, double wall, string source, DateTimeOffset now)
    {
        Assert.Equal(16, row.EnumerateObject().Count());
        Assert.True(row.TryGetProperty("slowdown_status", out var slowdownStatus));
        Assert.True(row.TryGetProperty("slowdown_ratio", out var slowdownRatio));
        if (slowdownStatus.GetString() != "slow") Assert.Equal(JsonValueKind.Null, slowdownRatio.ValueKind);
        Assert.Equal(1, row.GetProperty("version").GetInt32());
        Assert.Equal("goal", row.GetProperty("goal_id").GetString());
        Assert.Equal(lane, row.GetProperty("lane").GetString());
        Assert.Equal(run, row.GetProperty("run_ordinal").GetInt32());
        AssertField(row, "wall_ms", wall, null);
        Assert.Equal(source, row.GetProperty("accounting_source").GetString());
        Assert.Equal(now, row.GetProperty("recorded_at").GetDateTimeOffset());
    }

    private static void AssertField(JsonElement row, string field, double? value, string? reason)
    {
        if (value is null) Assert.Equal(JsonValueKind.Null, row.GetProperty(field).ValueKind);
        else Assert.Equal(value.Value, row.GetProperty(field).GetDouble());
        Assert.Equal(reason, row.GetProperty(field + "_unavailable_reason").GetString());
    }
}
