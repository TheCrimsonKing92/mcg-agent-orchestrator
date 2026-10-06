using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum RemoteLaneOutcomeCode
{
    Accepted, TransportUnavailable, Unreachable, LeaseExpired, LaneTimeout,
    BindingMismatchCommit, BindingMismatchTree, BindingMismatchMain, BindingMismatchFilter,
    BindingMismatchExecutor, BindingMismatchManifest, TrxIncomplete, RemoteRed,
    LateAfterFallback, NotEligibleExclusiveResource
}

internal sealed record RemoteLaneBinding(
    string ExecutorId, string Lane, string Commit, string Tree, string Main, string Filter, string Manifest);
internal sealed record RemoteExecutorHealthRecord(
    DateTimeOffset ObservedAt, string ExecutorId, string GateAttemptId, string Lane,
    RemoteLaneOutcomeCode Outcome, string? FaultOwner, RemoteLaneBinding Expected,
    RemoteLaneBinding? Observed, string? Reason = null, int Version = 1);

internal static class RemoteExecutorHealthLedger
{
    internal const string FileName = "remote-executor-health.jsonl";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter<RemoteLaneOutcomeCode>(JsonNamingPolicy.KebabCaseLower) }
    };
    internal static string ResolveStorePath(string worktreePath) => Path.Combine(
        AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath), ".orchestrator", FileName);
    internal static void Append(string path, RemoteExecutorHealthRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            SharedJsonlFile.AppendLine(path, JsonSerializer.Serialize(record, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    internal static IReadOnlyList<RemoteExecutorHealthRecord> ReadAll(string path) =>
        SharedJsonlFile.ReadLines(path).Select(line => JsonSerializer.Deserialize<RemoteExecutorHealthRecord>(line, JsonOptions))
            .OfType<RemoteExecutorHealthRecord>().ToArray();
}
