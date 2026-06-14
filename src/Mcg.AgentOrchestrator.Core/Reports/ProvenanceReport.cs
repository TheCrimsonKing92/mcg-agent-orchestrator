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
    IReadOnlyList<UnbackedDogfoodReference> UnbackedDogfoodReferences);

public static class ProvenanceReport
{
    // Matches "goal XXXXXXXX" or "goal/XXXXXXXX" patterns where X is a hex char.
    // The 8-hex group is the goal ID prefix to look up in state.
    private static readonly Regex GoalRefPattern =
        new(@"\bgoal[\s/]+([0-9a-f]{8})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ProvenanceReportSnapshot Build(IEnumerable<Goal> goals, string dogfoodLogText)
    {
        var goalList = goals.ToList();
        var completedGoals = goalList.Where(g => g.Status == GoalStatus.Completed).ToList();
        var goalRecords = completedGoals.Select(BuildGoalRecord).ToList();

        var backedCount = goalRecords.Count(r => r.ProvenanceStatus == ProvenanceStatus.Backed);
        var unbackedCount = goalRecords.Count(r => r.ProvenanceStatus == ProvenanceStatus.Unbacked);

        var completedGoalByPrefix = goalRecords
            .ToDictionary(r => r.GoalId.Value[..8].ToLowerInvariant());

        var unbackedRefs = ExtractUnbackedDogfoodReferences(dogfoodLogText, completedGoalByPrefix);

        return new ProvenanceReportSnapshot(
            completedGoals.Count,
            backedCount,
            unbackedCount,
            goalRecords,
            unbackedRefs);
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
