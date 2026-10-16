using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum ExecutorScreeningVerdict { Pass, Fail, Incomplete }

internal sealed record ExecutorScreeningLaneOutcome(string LaneName, string RemoteLaneName, string FilterHash,
    RemoteLaneOutcomeCode Outcome, IReadOnlyList<string> FailingTests,
    IReadOnlyList<string> IgnoredKnownDivergentTests, double DurationSeconds);

internal sealed record ExecutorScreeningExcludedLane(string LaneName, string Reason);

internal sealed record ExecutorScreeningReceipt(string ReceiptId, string GoalId, string CandidateCommit,
    string TreeSha, string MainSha, string ExecutorId, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    ExecutorScreeningVerdict Verdict, IReadOnlyList<ExecutorScreeningLaneOutcome> LaneOutcomes,
    IReadOnlyList<ExecutorScreeningExcludedLane> ExcludedLanes, int Version = 1);

// Receipts describe observations only. No acceptance or landing policy consumes this ledger.
internal static class ExecutorScreeningReceiptLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters =
        {
            new JsonStringEnumConverter<RemoteLaneOutcomeCode>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<ExecutorScreeningVerdict>(JsonNamingPolicy.KebabCaseLower)
        }
    };

    internal static string ResolveStorePath(string worktreePath) => Path.Combine(
        AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath), ".orchestrator", "executor-screening-receipts.jsonl");

    internal static void Append(string path, ExecutorScreeningReceipt receipt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        SharedJsonlFile.AppendLine(path, JsonSerializer.Serialize(receipt, JsonOptions));
    }

    internal static IReadOnlyList<ExecutorScreeningReceipt> ReadAll(string path)
    {
        var receipts = new List<ExecutorScreeningReceipt>();
        foreach (var line in SharedJsonlFile.ReadLines(path))
        {
            try
            {
                if (JsonSerializer.Deserialize<ExecutorScreeningReceipt>(line, JsonOptions) is { } receipt &&
                    receipt.Version == 1 && !string.IsNullOrWhiteSpace(receipt.GoalId) &&
                    !string.IsNullOrWhiteSpace(receipt.CandidateCommit) && receipt.LaneOutcomes is not null &&
                    receipt.ExcludedLanes is not null) receipts.Add(receipt);
            }
            catch (JsonException) { }
        }
        return receipts;
    }

    internal static ExecutorScreeningVerdict DecideVerdict(IReadOnlyList<ExecutorScreeningLaneOutcome> outcomes)
    {
        if (outcomes.Any(lane => lane.Outcome == RemoteLaneOutcomeCode.RemoteRed && lane.FailingTests.Count > 0))
            return ExecutorScreeningVerdict.Fail;
        return outcomes.Count > 0 && outcomes.All(lane => lane.Outcome == RemoteLaneOutcomeCode.Accepted ||
            lane.Outcome == RemoteLaneOutcomeCode.RemoteRed && lane.FailingTests.Count == 0 &&
            lane.IgnoredKnownDivergentTests.Count > 0)
            ? ExecutorScreeningVerdict.Pass : ExecutorScreeningVerdict.Incomplete;
    }
}
