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
        catch (Exception error)
        {
            slowdown = new("no-test-data", 0, wall.TotalMilliseconds, UnavailableReason: "slowdown-error:" + error.GetType().Name);
        }
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
            if (snapshot?.SlotTiming is { } slotTiming)
            {
                row["slot_wait_ms"] = slotTiming.WaitMs;
                row["slot_held_ms"] = slotTiming.HeldMs;
                row["slot_count"] = slotTiming.SlotCount;
            }
            if (snapshot?.SlotTimingUnavailableReason is { } slotReason)
                row["slot_timing_unavailable_reason"] = slotReason;
            row["slowdown_status"] = slowdown.Status;
            row["slowdown_ratio"] = slowdown.Status == "slow" && slowdown.Ratio is { } ratio ? Math.Round(ratio, 2) : null;
            row["sched_samples"] = null;
            row["sched_processes"] = null;
            row["sched_root_priority"] = null;
            row["sched_root_throttle_masks"] = null;
            row["sched_below_normal_processes"] = null;
            row["sched_eco_processes"] = null;
            row["sched_opted_out_processes"] = null;
            row["sched_background_memory_processes"] = null;
            row["sched_lowest"] = null;
            row["sched_unavailable_reason"] = "not-sampled";
            try
            {
                if (result.LaneScheduling is { } scheduling)
                {
                    row["sched_samples"] = scheduling.Samples;
                    row["sched_processes"] = scheduling.Processes;
                    row["sched_root_priority"] = scheduling.RootPriority;
                    row["sched_root_throttle_masks"] = scheduling.RootThrottleMasks;
                    row["sched_below_normal_processes"] = scheduling.BelowNormalProcesses;
                    row["sched_eco_processes"] = scheduling.EcoProcesses;
                    row["sched_opted_out_processes"] = scheduling.OptedOutProcesses;
                    row["sched_background_memory_processes"] = scheduling.BackgroundMemoryProcesses;
                    row["sched_lowest"] = scheduling.Lowest;
                    row["sched_unavailable_reason"] = scheduling.UnavailableReason;
                }
            }
            catch (Exception error) { row["sched_unavailable_reason"] = "sched-error:" + error.GetType().Name; }
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
        return slowdown with { UnavailableReason = unavailable ?? slowdown.UnavailableReason };
    }
}
