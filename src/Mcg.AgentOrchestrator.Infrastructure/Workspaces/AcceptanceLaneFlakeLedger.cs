using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceLaneFlakeRow(
    string GoalId,
    string AttemptId,
    string LaneName,
    string CandidateTreeSha,
    string FirstInvocationId,
    IReadOnlyList<string> FirstTestResultPaths,
    IReadOnlyList<string> FirstFailingTestIdentities,
    string FirstPredicate,
    string RerunInvocationId,
    IReadOnlyList<string> RerunTestResultPaths,
    IReadOnlyList<string> RerunFailingTestIdentities,
    string? RerunPredicate,
    string Outcome,
    DateTimeOffset RecordedAt)
{
    internal static AcceptanceLaneFlakeRow FromEvidence(
        string goalId, string attemptId, string laneName, string candidateTreeSha,
        AcceptanceLaneRerunEvidence evidence, DateTimeOffset recordedAt) =>
        new(goalId, attemptId, laneName, candidateTreeSha, evidence.FirstInvocationId,
            evidence.FirstTestResultPaths, evidence.FirstFailingTestIdentities, evidence.FirstPredicate,
            evidence.RerunInvocationId, evidence.RerunTestResultPaths, evidence.RerunFailingTestIdentities,
            evidence.RerunPredicate, evidence.Outcome, recordedAt);
}

internal sealed class AcceptanceLaneFlakeLedger
{
    private static readonly object AppendGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly Action<AcceptanceLaneFlakeRow>? _appendOverride;

    internal AcceptanceLaneFlakeLedger(string worktreePath, Action<AcceptanceLaneFlakeRow>? appendOverride = null)
    {
        _path = Path.Combine(AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath),
            ".orchestrator", "lane-flakes.jsonl");
        _appendOverride = appendOverride;
    }

    internal void Append(AcceptanceLaneFlakeRow row)
    {
        if (_appendOverride is not null)
        {
            _appendOverride(row);
            return;
        }

        lock (AppendGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            SharedJsonlFile.AppendLines(_path, [JsonSerializer.Serialize(row, JsonOptions)]);
        }
    }

    internal IReadOnlyList<AcceptanceLaneFlakeRow> Read(string? laneName = null, DateTimeOffset? since = null)
    {
        var rows = new List<AcceptanceLaneFlakeRow>();
        foreach (var line in SharedJsonlFile.ReadLines(_path))
        {
            AcceptanceLaneFlakeRow? row;
            try
            {
                row = JsonSerializer.Deserialize<AcceptanceLaneFlakeRow>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (row is null || string.IsNullOrWhiteSpace(row.LaneName) || row.RecordedAt == default ||
                row.FirstTestResultPaths is null || row.RerunTestResultPaths is null ||
                row.FirstFailingTestIdentities is null || row.RerunFailingTestIdentities is null ||
                row.Outcome is not (AcceptanceLaneRerunEvidence.Flake or AcceptanceLaneRerunEvidence.ConfirmedFailure) ||
                (laneName is not null && !string.Equals(row.LaneName, laneName, StringComparison.Ordinal)) ||
                (since is not null && row.RecordedAt < since.Value))
            {
                continue;
            }

            rows.Add(row);
        }

        return rows;
    }
}
