using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public enum ProvenanceStatus
{
    Backed,
    Unbacked
}

public sealed record GoalProvenanceRecord(
    GoalId GoalId,
    string Objective,
    GoalStatus GoalStatus,
    ProvenanceStatus ProvenanceStatus,
    int CompletedTaskCount,
    int UnbackedTaskCount);

public sealed record UnbackedDogfoodReference(
    string GoalIdPrefix,
    bool IsAbsent,
    string ReferenceContext);

public sealed record ProvenanceReportSnapshot(
    int CompletedGoalCount,
    int BackedGoalCount,
    int UnbackedGoalCount,
    IReadOnlyList<GoalProvenanceRecord> Goals,
    IReadOnlyList<UnbackedDogfoodReference> UnbackedDogfoodReferences,
    IReadOnlyList<string> UnbackedCommitShas);

public static class ProvenanceReport
{
    // Matches "goal XXXXXXXX" or "goal/XXXXXXXX" patterns where X is a hex char.
    // The 8-hex group is the goal ID prefix to look up in state.
    private static readonly Regex GoalRefPattern =
        new(@"\bgoal[\s/]+([0-9a-f]{8})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches backtick-quoted hex strings or hex strings after "commit"/"committed".
    private static readonly Regex CommitShaPattern =
        new(@"(?:`([0-9a-f]{7,40})`|\bcommit(?:ted)?\s+`?([0-9a-f]{7,40})`?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ProvenanceReportSnapshot Build(
        IEnumerable<Goal> goals,
        string dogfoodLogText,
        Func<string, bool>? shaExists = null)
    {
        var goalList = goals.ToList();
        var completedGoals = goalList.Where(g => g.Status is GoalStatus.Verified or GoalStatus.Completed).ToList();
        var goalRecords = completedGoals.Select(BuildGoalRecord).ToList();

        var backedCount = goalRecords.Count(r => r.ProvenanceStatus == ProvenanceStatus.Backed);
        var unbackedCount = goalRecords.Count(r => r.ProvenanceStatus == ProvenanceStatus.Unbacked);

        var completedGoalByPrefix = goalRecords
            .ToDictionary(r => r.GoalId.Value[..8].ToLowerInvariant());

        var unbackedRefs = ExtractUnbackedDogfoodReferences(dogfoodLogText, completedGoalByPrefix);

        var unbackedCommitShas = shaExists is not null
            ? ExtractUnbackedCommitShas(dogfoodLogText, shaExists)
            : (IReadOnlyList<string>)[];

        return new ProvenanceReportSnapshot(
            completedGoals.Count,
            backedCount,
            unbackedCount,
            goalRecords,
            unbackedRefs,
            unbackedCommitShas);
    }

    private static GoalProvenanceRecord BuildGoalRecord(Goal goal)
    {
        var completedTasks = goal.Tasks.Where(t => t.Status == WorkTaskStatus.Completed).ToList();
        var unbackedCount = completedTasks.Count(t => t.VerificationHistory.Count == 0);
        var status = unbackedCount > 0 ? ProvenanceStatus.Unbacked : ProvenanceStatus.Backed;

        return new GoalProvenanceRecord(
            goal.Id,
            goal.Objective,
            goal.Status,
            status,
            completedTasks.Count,
            unbackedCount);
    }

    private static List<string> ExtractUnbackedCommitShas(
        string text,
        Func<string, bool> shaExists)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unbacked = new List<string>();

        foreach (Match match in CommitShaPattern.Matches(text))
        {
            var sha = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).ToLowerInvariant();
            if (!seen.Add(sha)) continue;

            if (!shaExists(sha))
            {
                unbacked.Add(sha);
            }
        }

        return unbacked;
    }

    private static List<UnbackedDogfoodReference> ExtractUnbackedDogfoodReferences(
        string text,
        Dictionary<string, GoalProvenanceRecord> completedGoalByPrefix)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<UnbackedDogfoodReference>();

        foreach (Match match in GoalRefPattern.Matches(text))
        {
            var prefix = match.Groups[1].Value.ToLowerInvariant();
            if (!seen.Add(prefix)) continue;

            if (!completedGoalByPrefix.TryGetValue(prefix, out var record))
            {
                refs.Add(new UnbackedDogfoodReference(prefix, IsAbsent: true, match.Value.Trim()));
            }
            else if (record.ProvenanceStatus == ProvenanceStatus.Unbacked)
            {
                refs.Add(new UnbackedDogfoodReference(prefix, IsAbsent: false, match.Value.Trim()));
            }
        }

        return refs;
    }
}
