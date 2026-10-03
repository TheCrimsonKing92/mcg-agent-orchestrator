using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique temporary directories and no machine-global probes.
public sealed class GateHostHealthLedgerTests
{
    [Fact]
    public void ReaderSkipsMalformedValuesAndKeepsNewestRecordsInAppendOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), "host-health-ledger", Guid.NewGuid().ToString("N"));
        var path = GateHostHealthLedger.ResolveStorePath(root);
        try
        {
            for (var index = 0; index < GateHostHealthEvaluator.RetainedRecordWindow + 2; index++)
                GateHostHealthLedger.Append(path, new(DateTimeOffset.UnixEpoch.AddMinutes(index), $"attempt-{index}", index, null));
            File.AppendAllLines(path, ["malformed", "null", "{\"launchMs\":-5}",
                JsonSerializer.Serialize(new HostHealthLedgerRecord(DateTimeOffset.UnixEpoch, "future", 300, null, Version: 2),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)), "{\"launchMs\":"]);
            var records = GateHostHealthLedger.ReadRecent(path);
            Assert.Equal(GateHostHealthEvaluator.RetainedRecordWindow, records.Count);
            Assert.Equal("attempt-2", records[0].GateAttemptId);
            Assert.Equal("attempt-201", records[^1].GateAttemptId);
            Assert.Null(records[^1].PagedPoolMb);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void GoalWorktreesResolveToSameHostLedger()
    {
        var root = Path.Combine(Path.GetTempPath(), "host-health-root", Guid.NewGuid().ToString("N"));
        var host = GateHostHealthLedger.ResolveStorePath(root);
        Assert.Equal(host, GateHostHealthLedger.ResolveStorePath(Path.Combine(root, ".orchestrator-worktrees", "one")));
        Assert.Equal(host, GateHostHealthLedger.ResolveStorePath(Path.Combine(root, ".orchestrator-worktrees", "two")));
        Assert.Empty(GateHostHealthLedger.ReadRecent(host));
    }
}
