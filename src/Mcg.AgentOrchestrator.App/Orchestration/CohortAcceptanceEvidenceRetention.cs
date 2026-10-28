using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class CohortAcceptanceEvidenceRetention
{
    internal static readonly TimeSpan MinimumAge = StorageRetentionMaintenance.AcceptanceArtifactMaxAge;

    internal static void Sweep(
        string orchestratorDirectory, IReadOnlyCollection<StorageRetentionGoal> goals,
        DateTimeOffset now, List<EvidenceRetentionDecision> decisions)
    {
        var root = Path.GetFullPath(Path.Combine(orchestratorDirectory, "cohort-acceptance.db.artifacts"));
        var storePath = Path.Combine(orchestratorDirectory, "cohort-acceptance.db");
        if (!Directory.Exists(root) || !File.Exists(storePath) || !goals.Any(goal => !goal.IsStoreStandIn)) return;

        Dictionary<string, Receipt> receipts;
        ILookup<string, string> members;
        try
        {
            // Load both tables and release the read-only connection before touching any file.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = storePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            connection.Open();
            receipts = ReadReceipts(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT cohort_id, goal_id FROM cohort_members";
            using var reader = command.ExecuteReader();
            var rows = new List<(string CohortId, string GoalId)>();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
            members = rows.ToLookup(row => row.CohortId, row => row.GoalId, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException
            or FormatException or JsonException)
        {
            decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.CohortAcceptanceEvidence,
                EvidenceRetentionAction.RetainedUndecidable, root, null, EvidenceOwnerResolution.Unrecorded,
                "cohort-store-unavailable", FailureExceptionType: ex.GetType().Name));
            return;
        }

        var goalsById = goals.ToLookup(goal => goal.GoalId, StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var cohortId = Path.GetFileName(directory);
            if (!receipts.TryGetValue(cohortId, out var receipt))
            {
                if (Directory.EnumerateDirectories(directory).Any(child =>
                    Directory.EnumerateFiles(child).Any(IsResultArtifact)))
                    Keep(directory, EvidenceRetentionAction.RetainedUndecidable,
                        EvidenceOwnerResolution.Unrecorded, "cohort-receipt-not-recorded", decisions);
                continue;
            }

            var candidates = receipt.Paths.Where(path => path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase) ||
                    receipt.NormalizedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)).Select(Path.GetFullPath)
                .Where(path => IsDirectCustodyFile(path, directory) && File.Exists(path))
                .Order(StringComparer.Ordinal).ToArray();
            if (candidates.Length == 0) continue;

            if (receipt.LandingEvidence)
            {
                Keep(directory, EvidenceRetentionAction.Preserved,
                    EvidenceOwnerResolution.Unrecorded, "cohort-landing-evidence", decisions);
                continue;
            }
            if (now - receipt.CompletedAt <= MinimumAge)
            {
                Keep(directory, EvidenceRetentionAction.Preserved,
                    EvidenceOwnerResolution.Unrecorded, "cohort-within-age", decisions);
                continue;
            }
            if (receipt.Attribution == "BothMembersFailed" || receipt.AttributionSource == "main-suspect")
            {
                Keep(directory, EvidenceRetentionAction.Preserved,
                    EvidenceOwnerResolution.Unrecorded, "cohort-judge-panel-trigger", decisions);
                continue;
            }

            var memberGoals = members[cohortId].Select(id => goalsById[id].ToArray()).ToArray();
            if (memberGoals.Length == 0 || memberGoals.Any(matches => matches.Length != 1 || matches[0].IsStoreStandIn))
            {
                Keep(directory, EvidenceRetentionAction.RetainedUndecidable,
                    EvidenceOwnerResolution.Unmatched, "cohort-member-unresolved", decisions);
                continue;
            }
            if (memberGoals.Any(matches => !StorageRetentionMaintenance.IsReclaimableTerminalStatus(matches[0].Status)))
            {
                Keep(directory, EvidenceRetentionAction.Preserved,
                    EvidenceOwnerResolution.NonTerminal, "cohort-member-not-terminal", decisions);
                continue;
            }

            foreach (var path in candidates)
            {
                var length = StorageRetentionMaintenance.SafeLength(path);
                var deletion = StorageRetentionMaintenance.TryDeleteExclusive(path);
                decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.CohortAcceptanceEvidence,
                    deletion.Success ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.DeferredLocked,
                    path, null, EvidenceOwnerResolution.UniqueTerminal,
                    deletion.Success ? "cohort-red-trx-past-age" : "cohort-trx-locked",
                    BytesAttempted: length, BytesReclaimed: deletion.Success ? length : 0,
                    FailureExceptionType: deletion.ExceptionType));
            }
        }
    }

    private static Dictionary<string, Receipt> ReadReceipts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cohort_id, outcome, valid_for_landing, gate_exit_code, completed_at,
                   attribution, attribution_source, gate_test_result_paths_json, gate_evidence_artifacts_json FROM cohort_receipts
            """;
        using var reader = command.ExecuteReader();
        var receipts = new Dictionary<string, Receipt>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var completed = reader.IsDBNull(4) ? throw new FormatException("Missing cohort completion time.") : reader.GetString(4);
            var receipt = new Receipt(
                reader.GetString(1) == "Passed" && !reader.IsDBNull(2) && reader.GetInt64(2) == 1 &&
                    !reader.IsDBNull(3) && reader.GetInt64(3) == 0,
                DateTimeOffset.Parse(completed, CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? [] : JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? [],
                (reader.IsDBNull(8) ? [] : JsonSerializer.Deserialize<AcceptanceCohortEvidenceArtifact[]>(reader.GetString(8)) ?? [])
                    .Where(artifact => artifact.Kind == AcceptanceCohortCheckEvidence.NormalizedFormat).Select(artifact => artifact.Path).ToArray());
            if (!receipts.TryAdd(reader.GetString(0), receipt))
                throw new InvalidOperationException("Duplicate cohort receipt identity.");
        }
        return receipts;
    }

    private static bool IsResultArtifact(string path) => path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".ctrf.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectCustodyFile(string path, string directory)
    {
        var parent = Path.GetDirectoryName(path);
        return parent is not null && string.Equals(Path.GetDirectoryName(parent), Path.GetFullPath(directory),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void Keep(string path, EvidenceRetentionAction action, EvidenceOwnerResolution owner,
        string reason, List<EvidenceRetentionDecision> decisions) =>
        decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.CohortAcceptanceEvidence,
            action, path, null, owner, reason));

    private sealed record Receipt(bool LandingEvidence, DateTimeOffset CompletedAt,
        string? Attribution, string? AttributionSource, string[] Paths, string[] NormalizedPaths);
}
