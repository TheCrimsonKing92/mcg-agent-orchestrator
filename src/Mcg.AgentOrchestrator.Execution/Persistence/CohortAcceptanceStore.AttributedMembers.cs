using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class CohortAcceptanceStore
{
    public IReadOnlySet<string> ReadAttributedMemberKeys()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT attributed_members_json FROM cohort_receipts WHERE attributed_members_json <> '[]';";
        using var reader = command.ExecuteReader();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            foreach (var member in JsonSerializer.Deserialize<Mcg.AgentOrchestrator.Core.AcceptanceCohortAttributedMember[]>(reader.GetString(0)) ?? [])
            {
                keys.Add($"{member.GoalId.Value}:{member.CandidateRevision}");
            }
        }
        return keys;
    }
}
