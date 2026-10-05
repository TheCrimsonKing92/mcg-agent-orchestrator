using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class CohortAcceptanceStore
{
    public IReadOnlySet<string> ReadAttributedMemberKeys()
    {
        using var connection = Open();
        var retractions = new HashSet<(string Cohort, string Goal, string Candidate)>();
        using (var retracted = connection.CreateCommand())
        {
            retracted.CommandText = "SELECT earlier_cohort_id, goal_id, candidate_revision FROM cohort_attribution_retractions;";
            using var rows = retracted.ExecuteReader();
            while (rows.Read()) retractions.Add((rows.GetString(0), rows.GetString(1), rows.GetString(2)));
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cohort_id, attributed_members_json FROM cohort_receipts WHERE attributed_members_json <> '[]';";
        using var reader = command.ExecuteReader();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            foreach (var member in JsonSerializer.Deserialize<Mcg.AgentOrchestrator.Core.AcceptanceCohortAttributedMember[]>(reader.GetString(1)) ?? [])
            {
                if (!retractions.Contains((reader.GetString(0), member.GoalId.Value, member.CandidateRevision)))
                    keys.Add($"{member.GoalId.Value}:{member.CandidateRevision}");
            }
        }
        return keys;
    }
}
