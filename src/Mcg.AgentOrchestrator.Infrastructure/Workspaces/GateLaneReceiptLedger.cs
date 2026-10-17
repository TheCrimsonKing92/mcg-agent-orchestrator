using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GateLaneReceiptLedger
{
    internal const string FileName = "gate-lane-receipts.jsonl";

    internal static string ResolveStorePath(string worktreePath) =>
        Path.Combine(HostLedgerStateDirectory.Resolve(worktreePath), FileName);

    internal static void Record(
        string worktreePath, string? goalId, string laneName, AcceptanceCheckResult result,
        TimeSpan wall, DateTimeOffset now, Action<string, string>? appender = null)
    {
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
    }
}
