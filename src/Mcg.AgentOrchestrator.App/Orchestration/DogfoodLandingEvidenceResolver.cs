using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A read-only projection of existing authority, never a landing authorization.
internal static class DogfoodLandingEvidenceResolver
{
    private sealed record Reference(string Source, string Group, string? Receipt, string? Commit,
        string? PriorIntegration, string? State, bool MemberLanded);

    internal static DogfoodLandingEvidence Resolve(GoalId goal, string executionDirectory,
        string storeDirectory, GoalOperationJournalSummary journal, DogfoodLandingEvidence solo,
        Func<string[], (int ExitCode, string Output)>? git = null)
    {
        git ??= args =>
        {
            var result = GitCli.Run(executionDirectory, args);
            return (result.ExitCode, result.Output);
        };
        var failures = new List<string>();
        var landingIntent = GoalOperationJournal.TryGetLatestLandingIntent(journal);
        var intentSource = landingIntent?.Source switch
        {
            "LandingExecutor.ExecuteCohort" => "cohort",
            "LandingExecutor.ExecuteMergeTrain" => "merge-train",
            _ => null
        };
        GoalPromotionFact? promotion = null;
        try { promotion = ReadPromotion(Path.Combine(storeDirectory, "state.db"), goal); }
        catch (Exception ex) when (IsEvidenceFailure(ex))
        { failures.Add($"Promotion authority unreadable: {ex.Message}"); }

        var references = new List<Reference>();
        var sourceFailures = new List<(string Source, string Reason)>();
        ReadSource("cohort", "cohort-acceptance.db");
        ReadSource("merge-train", "merge-train-acceptance.db");
        if (intentSource is not null)
        {
            var group = landingIntent!.IntegrationBranch[(landingIntent.IntegrationBranch.IndexOf('/') + 1)..];
            if (!references.Any(r => r.Source == intentSource && r.Group == group))
                failures.Add($"{intentSource} landing reference has no canonical membership for goal {goal.Value}: {group}");
        }
        var terminal = references.Where(r => r.State == "finalized" ||
            (promotion is not null && r.Source == PromotionSource(promotion) &&
             (r.Commit is null || r.Commit == promotion.PromotedMainRevision))).ToArray();
        foreach (var failure in sourceFailures)
        {
            // An unavailable optional source is not evidence against a separate, bound landing.
            // With no terminal anchor, an unreadable source can conceal conflicting group authority.
            if (failure.Source == intentSource || failure.Source == (promotion is null ? null : PromotionSource(promotion)) ||
                (intentSource is null && promotion is null && terminal.Length > 0))
                failures.Add(failure.Reason);
        }
        var proofs = new List<DogfoodLandingProvenance>();
        foreach (var reference in terminal)
        {
            try
            {
                var proof = Validate(reference);
                proofs.Add(proof);
            }
            catch (Exception ex) when (IsEvidenceFailure(ex))
            { failures.Add($"{reference.Source} {reference.Group}: {ex.Message}"); }
        }

        if (proofs.Distinct().Count() > 1)
            failures.Add("Conflicting canonical terminal landing authorities");
        if (proofs.Count > 0 && failures.Count == 0)
            return new(DogfoodAcceptanceDisposition.Passed, "passed", solo.WasManuallyLanded, proofs[0]);
        if (failures.Count > 0)
            return new(DogfoodAcceptanceDisposition.Inconclusive, "grouped-authority-unavailable",
                solo.WasManuallyLanded, Reason: string.Join("; ", failures.Distinct()));
        if (promotion is not null && PromotionSource(promotion) is not null)
            return new(DogfoodAcceptanceDisposition.Inconclusive, "missing-grouped-receipt",
                solo.WasManuallyLanded, Reason: "Grouped promotion has no matching canonical member receipt and landing reference");
        // A prepared group does not own a separate solo/manual landing boundary.
        if (intentSource is not null ||
            (landingIntent is null && promotion is null &&
             (references.Any(r => r.Commit is not null) ||
              (references.Count > 0 && solo.AcceptanceDisposition != DogfoodAcceptanceDisposition.Passed))))
            return new(DogfoodAcceptanceDisposition.Inconclusive, "group-not-landed", solo.WasManuallyLanded,
                Reason: "Grouped receipt has no successful member landing or promotion authority");
        return solo;

        void ReadSource(string source, string file)
        {
            var path = Path.Combine(storeDirectory, file);
            if (!File.Exists(path)) return;
            try
            {
                using var connection = Open(path);
                using var command = connection.CreateCommand();
                // Start from the goal index. Do not deserialize unrelated receipts or discover artifacts.
                command.CommandText = source == "cohort" ? """
                    SELECT m.cohort_id, l.receipt_id, l.combined_commit_revision,
                           l.prior_integration_revision, l.state, m.landed
                    FROM cohort_members m LEFT JOIN cohort_landing_intents l ON l.cohort_id=m.cohort_id
                    WHERE m.goal_id=$goal
                    """ : """
                    SELECT m.train_id, l.receipt_id, l.commit_revision,
                           l.prior_integration_revision, l.state, 1
                    FROM merge_train_receipt_members m LEFT JOIN merge_train_landings l ON l.train_id=m.train_id
                    WHERE m.goal_id=$goal
                    """;
                command.Parameters.AddWithValue("$goal", goal.Value);
                using var reader = command.ExecuteReader();
                while (reader.Read()) references.Add(new(source, reader.GetString(0), Nullable(reader, 1),
                    Nullable(reader, 2), Nullable(reader, 3), Nullable(reader, 4), reader.GetInt32(5) == 1));
            }
            catch (Exception ex) when (IsEvidenceFailure(ex))
            { sourceFailures.Add((source, $"{source} authority unreadable: {ex.Message}")); }
        }

        DogfoodLandingProvenance Validate(Reference reference)
        {
            string receiptId, candidate, tree, main;
            bool authoritative;
            if (reference.Source == "cohort")
            {
                using var connection = Open(Path.Combine(storeDirectory, "cohort-acceptance.db"));
                using var transaction = connection.BeginTransaction(deferred: true);
                var receipt = ReadCohort(connection, transaction, reference.Group);
                receiptId = receipt.ReceiptId;
                candidate = receipt.Identity.Members.Single(m => m.GoalId == goal).CandidateRevision;
                tree = receipt.Identity.CombinedTreeRevision;
                main = receipt.Identity.ObservedMainRevision;
                authoritative = receipt.HasAuthoritativeLandingEvidence;
                ValidateVerdict(receipt.GateEvidenceArtifacts, reference.Group, receiptId, cohort: true);
            }
            else
            {
                var path = Path.Combine(storeDirectory, "merge-train-acceptance.db");
                var receipt = MergeTrainAcceptanceStore.OpenReadOnly(path).TryReadReceipt(reference.Group)
                    ?? throw new InvalidDataException("Canonical receipt missing");
                Require(receipt.Identity.Value == reference.Group, "Canonical group/member identity mismatch");
                using var connection = Open(path);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT receipt_id FROM merge_train_receipts WHERE train_id=$group";
                command.Parameters.AddWithValue("$group", reference.Group);
                Require((string?)command.ExecuteScalar() == receipt.ReceiptId, "Stored receipt identity mismatch");
                receiptId = receipt.ReceiptId;
                candidate = receipt.Identity.Members.Single(m => m.GoalId == goal).CandidateRevision;
                tree = receipt.Identity.TrainTreeRevision;
                main = receipt.Identity.ObservedMainRevision;
                authoritative = receipt.HasAuthoritativeLandingEvidence;
                ValidateVerdict(receipt.GateEvidenceArtifacts, reference.Group, receiptId, cohort: false);
            }
            Require(authoritative, "Receipt is failed, invalidated, or lacks valid content-bound gate evidence");
            Require(reference.Receipt is null || reference.Receipt == receiptId, "Landing receipt identity mismatch");
            var promoted = reference.Commit ?? promotion?.PromotedMainRevision;
            Require(!string.IsNullOrWhiteSpace(promoted), "Promoted main identity missing");
            // prior_integration_revision is the prior integration ref, which may predate main.
            if (reference.PriorIntegration is not null)
                Require(git(["merge-base", "--is-ancestor", reference.PriorIntegration, main]).ExitCode == 0,
                    "Prior integration revision is not an ancestor of observed main");
            var finalized = reference.State == "finalized" && reference.MemberLanded;
            if (promotion is not null)
            {
                Require(PromotionSource(promotion) == reference.Source && promotion.CandidateRevision == candidate &&
                    promotion.PriorMainRevision == main && promotion.PromotedMainRevision == promoted,
                    "Promotion candidate, source, observed main or promoted main binding mismatch");
            }
            Require(finalized || promotion is not null, "Member landing is not finalized and no successful promotion matches");
            var intent = landingIntent;
            if (intent is not null && !solo.WasManuallyLanded)
            {
                Require(intent.GoalId == goal.Value && intent.MergeCommitSha == promoted &&
                    (intentSource is null || (intentSource == reference.Source &&
                        intent.IntegrationBranch.EndsWith('/' + reference.Group, StringComparison.Ordinal))) &&
                    (intent.BoundMainRevision is null || intent.BoundMainRevision == main) &&
                    (intent.PreviousIntegrationRevision is null || intent.PreviousIntegrationRevision == reference.PriorIntegration),
                    "Goal landing intent conflicts with canonical grouped landing");
            }
            Require(Git("rev-parse", "--verify", $"{promoted}^{{tree}}") == tree, "Promoted main tree differs from combined tested tree");
            Require(Git("rev-parse", "--verify", $"{promoted}^{{commit}}") == promoted,
                "Promoted main must name its exact immutable commit");
            Require(git(["merge-base", "--is-ancestor", main, promoted!]).ExitCode == 0,
                "Observed main is not an ancestor of promoted main");
            Require(Git("rev-parse", "--verify", $"{candidate}^{{commit}}") == candidate,
                "Member candidate must name its exact immutable commit");
            return new(reference.Source, goal.Value, reference.Group, receiptId, candidate, tree, main,
                promoted!, promotion?.Provenance ?? "finalized-member-landing");
        }

        string Git(params string[] args)
        {
            var result = git(args);
            Require(result.ExitCode == 0, "Historical Git identity unavailable");
            return result.Output.Trim();
        }
    }

    private static string? PromotionSource(GoalPromotionFact fact) => fact.Provenance switch
    {
        GoalPromotionFact.CohortProvenance => "cohort",
        GoalPromotionFact.MergeTrainProvenance => "merge-train",
        _ => null
    };

    private static GoalPromotionFact? ReadPromotion(string path, GoalId goal)
    {
        if (!File.Exists(path)) return null;
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name='goal_promotion_facts' AND type='table'";
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return null;
        command.CommandText = """
            SELECT candidate_revision, prior_main_revision, promoted_main_revision, promoted_at, provenance
            FROM goal_promotion_facts WHERE goal_id=$goal
            """;
        command.Parameters.AddWithValue("$goal", goal.Value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(goal.Value, reader.GetString(0), reader.GetString(1), reader.GetString(2),
            DateTimeOffset.ParseExact(reader.GetString(3), "O", CultureInfo.InvariantCulture), reader.GetString(4)) : null;
    }

    // Cohort's public constructor mutates legacy state. Reconstruct only the referenced receipt
    // on a read-only snapshot, using the same canonical identity and evidence validators.
    private static AcceptanceCohortReceipt ReadCohort(SqliteConnection connection, SqliteTransaction transaction, string group)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT receipt_id, main_revision, combined_tree_revision, manifest_identity, outcome,
                   attribution, valid_for_landing, completed_at, gate_elapsed_ms, failed_checks_json,
                   gate_exit_code, gate_test_result_paths_json, gate_evidence_artifacts_json,
                   (SELECT COUNT(*) FROM cohort_invalidations i WHERE i.cohort_id=r.cohort_id)
            FROM cohort_receipts r WHERE cohort_id=$group
            """;
        command.Parameters.AddWithValue("$group", group);
        using var reader = command.ExecuteReader();
        Require(reader.Read(), "Canonical cohort receipt missing");
        var id = reader.GetString(0);
        var main = reader.GetString(1);
        var tree = reader.GetString(2);
        var manifest = reader.GetString(3);
        var outcome = Enum.Parse<AcceptanceCohortGateOutcome>(reader.GetString(4));
        var attribution = Enum.Parse<AcceptanceCohortAttributionOutcome>(reader.GetString(5));
        var valid = reader.GetInt32(6) == 1;
        var at = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture);
        var elapsed = reader.GetInt64(8);
        var failed = JsonSerializer.Deserialize<string[]>(reader.GetString(9)) ?? [];
        int? exit = reader.IsDBNull(10) ? null : reader.GetInt32(10);
        var paths = JsonSerializer.Deserialize<string[]>(reader.GetString(11)) ?? [];
        var artifacts = JsonSerializer.Deserialize<AcceptanceCohortEvidenceArtifact[]>(reader.GetString(12)) ?? [];
        Require(reader.GetInt32(13) == 0, "Cohort receipt invalidated");
        reader.Close();
        command.CommandText = """
            SELECT goal_id, branch_revision, candidate_revision, landing_paths_json, resource_keys_json,
                   risk_tier, promotion_disposition, merge_status, merge_reason, brief_version
            FROM cohort_members WHERE cohort_id=$group ORDER BY member_ordinal
            """;
        using var membersReader = command.ExecuteReader();
        var members = new List<AcceptanceCohortMemberBinding>();
        while (membersReader.Read()) members.Add(new(new GoalId(membersReader.GetString(0)),
            membersReader.GetString(1), membersReader.GetString(2),
            JsonSerializer.Deserialize<string[]>(membersReader.GetString(3)) ?? [],
            JsonSerializer.Deserialize<string[]>(membersReader.GetString(4)) ?? [],
            Enum.Parse<ChangeRiskTier>(membersReader.GetString(5)),
            Enum.Parse<ConductorTransitionDecision>(membersReader.GetString(6)),
            membersReader.GetString(7), membersReader.GetString(8),
            membersReader.IsDBNull(9) ? null : membersReader.GetInt32(9)));
        var identity = AcceptanceCohortIdentity.Create(members, main, tree, manifest);
        Require(identity.Value == group, "Canonical cohort group/member identity mismatch");
        var checks = ReadDeclaredChecks(artifacts);
        return new(id, identity, outcome, at, elapsed, failed, exit, paths, attribution, valid)
        {
            GateEvidenceArtifacts = artifacts, GateChecks = checks,
            DeclaredEvidenceValidated = CohortTestResultEvidence.Assess(checks) is { Coherent: true, Passed: true }
        };
    }

    private static IReadOnlyList<AcceptanceCohortCheckEvidence> ReadDeclaredChecks(
        IReadOnlyList<AcceptanceCohortEvidenceArtifact> artifacts)
    {
        var verdicts = artifacts.Where(a => a.Kind == "gate-verdict").ToArray();
        if (verdicts.Length != 1 || !AcceptanceCohortGateEvidence.IsContentBoundArtifact(verdicts[0])) return [];
        using var document = JsonDocument.Parse(File.ReadAllBytes(verdicts[0].Path));
        return document.RootElement.TryGetProperty("GateChecks", out var checks)
            ? checks.Deserialize<AcceptanceCohortCheckEvidence[]>() ?? [] : [];
    }

    private static void ValidateVerdict(IReadOnlyList<AcceptanceCohortEvidenceArtifact> artifacts,
        string group, string receipt, bool cohort)
    {
        var verdicts = artifacts.Where(a => a.Kind == "gate-verdict").ToArray();
        Require(verdicts.Length == 1 && AcceptanceCohortGateEvidence.IsContentBoundArtifact(verdicts[0]),
            "Missing or corrupt content-bound gate verdict");
        using var document = JsonDocument.Parse(File.ReadAllBytes(verdicts[0].Path));
        var root = document.RootElement;
        Require(root.GetProperty(cohort ? "CohortId" : "value").GetString() == group,
            "Gate verdict group identity mismatch");
        if (cohort) Require(root.GetProperty("ReceiptId").GetString() == receipt, "Gate verdict receipt identity mismatch");
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static string? Nullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidDataException(reason);
    }
    private static bool IsEvidenceFailure(Exception ex) => ex is SqliteException or IOException or InvalidDataException or
        UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException;
}
