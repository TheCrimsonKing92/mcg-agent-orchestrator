using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class RemoteExecutorProbeLedger
{
    internal const string FileName = "remote-executor-probe.jsonl";
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    internal static string ResolveStorePath(string worktreePath) => Path.Combine(
        AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath), ".orchestrator", FileName);
    internal static void Append(string path, RemoteExecutorProbeRow row)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            SharedJsonlFile.AppendLine(path, JsonSerializer.Serialize(row, JsonOptions));
        }
        catch (Exception) { }
    }
}
