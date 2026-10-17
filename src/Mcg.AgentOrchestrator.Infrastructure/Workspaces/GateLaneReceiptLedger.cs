using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GateLaneReceiptLedger
{
    internal const string FileName = "gate-lane-receipts.jsonl";

    internal static string ResolveStorePath(string worktreePath) =>
        Path.Combine(HostLedgerStateDirectory.Resolve(worktreePath), FileName);

    internal static GateLaneSlowdownDiff.GateLaneSlowdownResult? Record(
        string worktreePath, string? goalId, string laneName, AcceptanceCheckResult result,
        TimeSpan wall, DateTimeOffset now, Action<string, string>? appender = null)
    {
        GateLaneSlowdownDiff.GateLaneSlowdownResult? slowdown = null;
        string? unavailable = null;
        LaneTestDurationSnapshot? snapshot = null;
        try
        {
            snapshot = LaneTestDurationSnapshot.Read(result.TestResultPaths);
            slowdown = GateLaneSlowdownDiff.Compute(wall.TotalMilliseconds, snapshot, GateLaneBaselineStore.ReadHistory(worktreePath, laneName));
        }
        catch (Exception error) { unavailable = "slowdown-error:" + error.GetType().Name; }
        // These independent writes must survive failure of any other diagnostic artifact.
        try
        {
            if (slowdown?.Status == "slow")
            {
                var path = Path.Combine(HostLedgerStateDirectory.Resolve(worktreePath), GateLaneSlowdownDiff.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                (appender ?? SharedJsonlFile.AppendLine)(path,
                    JsonSerializer.Serialize(GateLaneSlowdownDiff.ToRow(slowdown, goalId, laneName, result.TestResultRunOrdinal, now)));
            }
        }
        catch (Exception error) { unavailable = "slowdown-write:" + error.GetType().Name; }
        try
        {
            if (snapshot is not null)
            {
                var baselineReason = GateLaneBaselineStore.TryAppend(worktreePath, laneName,
                    new(now, wall.TotalMilliseconds, snapshot.Tests), result.Passed && !result.TestResultIsExplicitCrossAttemptReuse);
                unavailable ??= baselineReason;
            }
        }
        catch (Exception error) { unavailable ??= "baseline-error:" + error.GetType().Name; }
        try
        {
            GateLoadContextProbe.BindHostLogDirectory(Path.Combine(HostLedgerStateDirectory.Resolve(worktreePath), "logs"));
        }
        catch (Exception) { /* Binding is independent of receipt persistence. */ }

        try
        {
            var receipt = GateLaneReceipt.FromSummary(laneName, result.TestResultRunOrdinal, wall,
                result.ResultSummary, GateHostHealthProbe.CapturePhysicalAvailableMb());
            var row = new Dictionary<string, object?>
            {
                ["version"] = 1, ["goal_id"] = goalId, ["lane"] = receipt.LaneName,
                ["run_ordinal"] = receipt.RunOrdinal, ["accounting_source"] = receipt.AccountingSource,
                ["recorded_at"] = now
            };
            Add("wall_ms", receipt.WallMilliseconds);
            Add("cpu_ms", receipt.CpuMilliseconds);
            Add("peak_mem_bytes", receipt.PeakMemoryBytes);
            Add("host_available_memory_mb", receipt.HostAvailableMemoryMb);
            if (slowdown is not null) row["slowdown_status"] = slowdown.Status;
            if (slowdown?.Ratio is { } ratio) row["slowdown_ratio"] = Math.Round(ratio, 2);
            if (slowdown?.Status == "insufficient-history") row["slowdown_history_runs"] = slowdown.HistoryRuns;
            if (slowdown?.UnavailableReason is not null || unavailable is not null)
                row["slowdown_unavailable_reason"] = unavailable ?? slowdown?.UnavailableReason;
            var path = ResolveStorePath(worktreePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            (appender ?? SharedJsonlFile.AppendLine)(path, JsonSerializer.Serialize(row));

            void Add(string field, GateLoadSample sample)
            {
                row[field] = sample.Value;
                row[field + "_unavailable_reason"] = sample.UnavailableReason;
            }
        }
        catch (Exception) { /* Diagnostic receipts must never affect the gate verdict. */ }
        return slowdown;
    }
}
