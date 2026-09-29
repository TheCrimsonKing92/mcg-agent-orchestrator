using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record CohortAdmissionFairnessTransition(
    GoalId OldestEligibleGoalId,
    int PreviousOvertakeCount,
    int ResultingOvertakeCount,
    bool OldestAdmitted,
    IReadOnlyList<GoalId> AdmittedGoalIds);

public sealed record AcceptanceCohortCoverage(
    GoalId GoalId,
    string CohortId,
    string ReceiptId,
    bool Landed);

public sealed record AcceptanceCohortMaterializationFailure(
    string AttemptId,
    AcceptanceCohortMaterializationFailureKind Outcome,
    DateTimeOffset RecordedAt,
    string Detail);

public sealed record AcceptanceCohortLandingRecovery(
    AcceptanceCohortReceipt Receipt,
    string CombinedCommitRevision,
    IReadOnlyList<AcceptanceCohortCoverage> Coverage);

public sealed partial class CohortAcceptanceStore
{
    private readonly string _databasePath;

    public CohortAcceptanceStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using var connection = Open();
        EnsureSchema(connection);
    }

    public AcceptanceCohortReceipt SaveGateReceipt(AcceptanceCohortReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Identity.Members.Count != 2)
        {
            throw new ArgumentException("A shared cohort receipt must reference exactly two members.", nameof(receipt));
        }
        using var connection = Open();
        var existing = ReadReceipt(connection, receipt.Identity.Value, transaction: null);
        if (existing is not null)
        {
            return existing;
        }

        ValidateInfrastructureClassification(receipt);

        if ((receipt.Outcome == AcceptanceCohortGateOutcome.Passed || receipt.ValidForLanding) &&
            (receipt.GateExitCode != 0 ||
             !AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(receipt.GateTestResultPaths)))
        {
            throw new ArgumentException(
                "A passing cohort receipt requires exit code zero and normalized bound TRX evidence.",
                nameof(receipt));
        }

        receipt = CaptureGateEvidence(receipt);
        if ((receipt.Outcome == AcceptanceCohortGateOutcome.Passed || receipt.ValidForLanding) &&
            !receipt.HasAuthoritativeLandingEvidence)
        {
            throw new InvalidDataException("Cohort gate evidence custody did not produce content-bound landing evidence.");
        }

        using var transaction = connection.BeginTransaction();
        existing = ReadReceipt(connection, receipt.Identity.Value, transaction);
        if (existing is not null)
        {
            transaction.Commit();
            return existing;
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO cohort_receipts(
                    cohort_id, receipt_id, main_revision, combined_tree_revision, manifest_identity,
                    outcome, attribution, valid_for_landing, completed_at, gate_elapsed_ms, failed_checks_json,
                    infrastructure_reason_code, infrastructure_detail,
                    gate_exit_code, gate_test_result_paths_json, gate_evidence_artifacts_json)
                VALUES ($cohort, $receipt, $main, $tree, $manifest, $outcome, $attribution, $valid, $completed, $elapsed, $failed,
                    $infrastructureReason, $infrastructureDetail, $exitCode, $testResultPaths, $evidenceArtifacts);
                """;
            command.Parameters.AddWithValue("$cohort", receipt.Identity.Value);
            command.Parameters.AddWithValue("$receipt", receipt.ReceiptId);
            command.Parameters.AddWithValue("$main", receipt.Identity.ObservedMainRevision);
            command.Parameters.AddWithValue("$tree", receipt.Identity.CombinedTreeRevision);
            command.Parameters.AddWithValue("$manifest", receipt.Identity.ManifestIdentity);
            command.Parameters.AddWithValue("$outcome", receipt.Outcome.ToString());
            command.Parameters.AddWithValue("$attribution", receipt.Attribution.ToString());
            command.Parameters.AddWithValue("$valid", receipt.ValidForLanding ? 1 : 0);
            command.Parameters.AddWithValue("$completed", receipt.CompletedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$elapsed", receipt.GateElapsedMilliseconds);
            command.Parameters.AddWithValue("$failed", JsonSerializer.Serialize(receipt.FailedChecks));
            command.Parameters.AddWithValue(
                "$infrastructureReason",
                (object?)receipt.InfrastructureReasonCode ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$infrastructureDetail",
                (object?)receipt.InfrastructureDetail ?? DBNull.Value);
            command.Parameters.AddWithValue("$exitCode", (object?)receipt.GateExitCode ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$testResultPaths",
                JsonSerializer.Serialize(receipt.GateTestResultPaths));
            command.Parameters.AddWithValue(
                "$evidenceArtifacts",
                JsonSerializer.Serialize(receipt.GateEvidenceArtifacts));
            command.ExecuteNonQuery();
        }

        for (var index = 0; index < receipt.Identity.Members.Count; index++)
        {
            var member = receipt.Identity.Members[index];
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO cohort_members(
                    cohort_id, member_ordinal, goal_id, branch_revision, candidate_revision,
                    landing_paths_json, resource_keys_json, risk_tier, promotion_disposition,
                    merge_status, merge_reason, landed)
                VALUES ($cohort, $ordinal, $goal, $branch, $candidate, $paths, $resources,
                    $risk, $promotion, $mergeStatus, $mergeReason, 0);
                """;
            command.Parameters.AddWithValue("$cohort", receipt.Identity.Value);
            command.Parameters.AddWithValue("$ordinal", index);
            command.Parameters.AddWithValue("$goal", member.GoalId.Value);
            command.Parameters.AddWithValue("$branch", member.BranchRevision);
            command.Parameters.AddWithValue("$candidate", member.CandidateRevision);
            command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(member.LandingPaths));
            command.Parameters.AddWithValue("$resources", JsonSerializer.Serialize(member.ResourceKeys));
            command.Parameters.AddWithValue("$risk", member.ChangeRiskTier.ToString());
            command.Parameters.AddWithValue("$promotion", member.AutoPromotionDisposition.ToString());
            command.Parameters.AddWithValue("$mergeStatus", member.MergeStatus);
            command.Parameters.AddWithValue("$mergeReason", member.MergeReason);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return receipt;
    }

    public AcceptanceCohortReceipt? TryReadReceipt(string cohortId)
    {
        using var connection = Open();
        return ReadReceipt(connection, cohortId, transaction: null);
    }

    public IReadOnlyList<AcceptanceCohortReceipt> ReadPassedReceiptsForGoal(GoalId goalId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cohort_id FROM cohort_members WHERE goal_id=$goal ORDER BY rowid DESC;";
        command.Parameters.AddWithValue("$goal", goalId.Value);
        using var reader = command.ExecuteReader();
        var cohortIds = new List<string>();
        while (reader.Read())
        {
            cohortIds.Add(reader.GetString(0));
        }

        reader.Close();
        return cohortIds
            .Select(cohortId => ReadReceipt(connection, cohortId, transaction: null))
            .Where(receipt => receipt?.HasAuthoritativeLandingEvidence == true)
            .Cast<AcceptanceCohortReceipt>()
            .OrderByDescending(receipt => receipt.CompletedAt)
            .ThenByDescending(receipt => receipt.ReceiptId, StringComparer.Ordinal)
            .ToArray();
    }

    public AcceptanceCohortMaterializationFailure SaveMaterializationFailure(
        IReadOnlyList<AcceptanceCohortMemberBinding> members,
        string observedMainRevision,
        AcceptanceCohortMaterializationFailureKind outcome,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count != 2 || members[0].GoalId == members[1].GoalId)
        {
            throw new ArgumentException(
                "A cohort materialization failure requires exactly two distinct ordered members.",
                nameof(members));
        }
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        var main = AcceptanceCohortMemberBinding.NormalizeRevision(
            observedMainRevision,
            nameof(observedMainRevision));
        var payload = string.Join('\n',
            "cohort-materialization-v1",
            members[0].GoalId.Value,
            members[0].BranchRevision,
            members[0].CandidateRevision,
            members[1].GoalId.Value,
            members[1].BranchRevision,
            members[1].CandidateRevision,
            main);
        var attemptId = $"cohort-materialization-v1-{Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)))}";
        var failure = new AcceptanceCohortMaterializationFailure(
            attemptId,
            outcome,
            DateTimeOffset.UtcNow,
            detail.Length <= 512 ? detail : detail[..512]);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO cohort_materialization_failures(
                attempt_id, first_goal_id, first_branch_revision, first_candidate_revision,
                second_goal_id, second_branch_revision, second_candidate_revision,
                main_revision, outcome, detail, recorded_at)
            VALUES (
                $attempt, $firstGoal, $firstBranch, $firstCandidate,
                $secondGoal, $secondBranch, $secondCandidate,
                $main, $outcome, $detail, $recorded);
            """;
        command.Parameters.AddWithValue("$attempt", failure.AttemptId);
        command.Parameters.AddWithValue("$firstGoal", members[0].GoalId.Value);
        command.Parameters.AddWithValue("$firstBranch", members[0].BranchRevision);
        command.Parameters.AddWithValue("$firstCandidate", members[0].CandidateRevision);
        command.Parameters.AddWithValue("$secondGoal", members[1].GoalId.Value);
        command.Parameters.AddWithValue("$secondBranch", members[1].BranchRevision);
        command.Parameters.AddWithValue("$secondCandidate", members[1].CandidateRevision);
        command.Parameters.AddWithValue("$main", main);
        command.Parameters.AddWithValue("$outcome", failure.Outcome.ToString());
        command.Parameters.AddWithValue("$detail", failure.Detail);
        command.Parameters.AddWithValue("$recorded", failure.RecordedAt.ToUniversalTime().ToString("O"));
        command.ExecuteNonQuery();
        return failure;
    }

    public AcceptanceCohortReceipt SaveAttribution(
        string cohortId,
        AcceptanceCohortAttributionOutcome attribution,
        IReadOnlyList<AcceptanceCohortPartitionReceipt> partitions,
        string pairFingerprint,
        GoalId? innocentGoalId,
        IReadOnlyList<AcceptanceCohortAttributedMember>? attributedMembers = null,
        IReadOnlyList<AcceptanceCohortUnrelatedFailure>? unrelatedFailures = null)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        if (partitions.Count != 2 ||
            partitions.Select(partition => partition.MemberOrdinal).Order().SequenceEqual([0, 1]) == false ||
            partitions.Select(partition => partition.GoalId).Distinct().Count() != 2)
        {
            throw new ArgumentException("Cohort attribution requires exactly two distinct ordered partition receipts.", nameof(partitions));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(pairFingerprint);

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var partition in partitions.OrderBy(partition => partition.MemberOrdinal))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO cohort_partition_receipts(
                    cohort_id, member_ordinal, receipt_id, goal_id, candidate_revision,
                    main_revision, tree_revision, manifest_identity, outcome, elapsed_ms, test_result_paths_json, failed_checks_json, failing_test_identities_json)
                VALUES ($cohort, $ordinal, $receipt, $goal, $candidate, $main, $tree, $manifest, $outcome, $elapsed, $paths, $checks, $tests)
                ON CONFLICT(cohort_id, member_ordinal) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$cohort", cohortId);
            insert.Parameters.AddWithValue("$ordinal", partition.MemberOrdinal);
            insert.Parameters.AddWithValue("$receipt", partition.ReceiptId);
            insert.Parameters.AddWithValue("$goal", partition.GoalId.Value);
            insert.Parameters.AddWithValue("$candidate", partition.CandidateRevision);
            insert.Parameters.AddWithValue("$main", partition.ObservedMainRevision);
            insert.Parameters.AddWithValue("$tree", (object?)partition.TreeRevision ?? DBNull.Value);
            insert.Parameters.AddWithValue("$manifest", partition.ManifestIdentity);
            insert.Parameters.AddWithValue("$outcome", partition.Outcome.ToString());
            insert.Parameters.AddWithValue("$elapsed", partition.ElapsedMilliseconds);
            insert.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(partition.TestResultPaths));
            insert.Parameters.AddWithValue("$checks", JsonSerializer.Serialize(partition.FailedChecks));
            insert.Parameters.AddWithValue("$tests", JsonSerializer.Serialize(partition.FailingTestIdentities));
            insert.ExecuteNonQuery();
        }
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE cohort_receipts SET attribution=$attribution, attributed_members_json=$members, unrelated_failures_json=$unrelated WHERE cohort_id=$cohort AND outcome='Failed';";
            update.Parameters.AddWithValue("$cohort", cohortId);
            update.Parameters.AddWithValue("$attribution", attribution.ToString());
            update.Parameters.AddWithValue("$members", JsonSerializer.Serialize(attributedMembers ?? []));
            update.Parameters.AddWithValue("$unrelated", JsonSerializer.Serialize(unrelatedFailures ?? []));
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("Attribution can update only one persisted deterministic RED cohort receipt.");
            }
        }
        EnsureAttributionSideEffects(
            connection,
            transaction,
            cohortId,
            attribution,
            pairFingerprint,
            innocentGoalId);
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM cohort_partition_receipts WHERE cohort_id=$cohort;";
            count.Parameters.AddWithValue("$cohort", cohortId);
            if (Convert.ToInt32(count.ExecuteScalar()) != 2)
            {
                throw new InvalidOperationException("Cohort attribution did not persist exactly two partition receipts.");
            }
        }
        var receipt = ReadReceipt(connection, cohortId, transaction) ??
            throw new InvalidOperationException("Cohort receipt disappeared during attribution persistence.");
        transaction.Commit();
        return receipt;
    }

    public void EnsureAttributionSideEffects(
        string cohortId,
        AcceptanceCohortAttributionOutcome attribution,
        string pairFingerprint,
        GoalId? innocentGoalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairFingerprint);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        EnsureAttributionSideEffects(
            connection,
            transaction,
            cohortId,
            attribution,
            pairFingerprint,
            innocentGoalId);
        transaction.Commit();
    }

    public AcceptanceCohortReceipt InvalidateLanding(
        string cohortId,
        AcceptanceCohortInvalidationReason reason,
        string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortId);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var receipt = ReadReceipt(connection, cohortId, transaction) ??
            throw new InvalidOperationException("Cohort receipt was missing during invalidation.");
        var orderedBindings = receipt.Identity.Members.Select((member, ordinal) =>
            $"{ordinal}:{member.GoalId.Value}:{member.BranchRevision}:{member.CandidateRevision}").ToArray();
        var invalidatedAt = DateTimeOffset.UtcNow;
        var invalidationPayload = string.Join('\n',
            cohortId,
            reason.ToString(),
            receipt.Identity.ObservedMainRevision,
            receipt.Identity.CombinedTreeRevision,
            receipt.Identity.ManifestIdentity,
            string.Join('\n', orderedBindings));
        var invalidationId = $"cohort-invalidation-v1-{Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(invalidationPayload)))}";
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO cohort_invalidations(
                    invalidation_id, cohort_id, reason, detail, invalidated_at,
                    observed_main_revision, combined_tree_revision, manifest_identity,
                    ordered_member_bindings_json)
                VALUES ($id, $cohort, $reason, $detail, $at, $main, $tree, $manifest, $members)
                ON CONFLICT(invalidation_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", invalidationId);
            command.Parameters.AddWithValue("$cohort", cohortId);
            command.Parameters.AddWithValue("$reason", reason.ToString());
            command.Parameters.AddWithValue("$detail", detail.Length <= 1024 ? detail : detail[..1024]);
            command.Parameters.AddWithValue("$at", invalidatedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$main", receipt.Identity.ObservedMainRevision);
            command.Parameters.AddWithValue("$tree", receipt.Identity.CombinedTreeRevision);
            command.Parameters.AddWithValue("$manifest", receipt.Identity.ManifestIdentity);
            command.Parameters.AddWithValue("$members", JsonSerializer.Serialize(receipt.Identity.Members));
            command.ExecuteNonQuery();
        }
        using (var invalidateIntent = connection.CreateCommand())
        {
            invalidateIntent.Transaction = transaction;
            invalidateIntent.CommandText = "UPDATE cohort_landing_intents SET state='invalidated', updated_at=$updated WHERE cohort_id=$cohort;";
            invalidateIntent.Parameters.AddWithValue("$cohort", cohortId);
            invalidateIntent.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            invalidateIntent.ExecuteNonQuery();
        }
        receipt = ReadReceipt(connection, cohortId, transaction) ??
            throw new InvalidOperationException("Cohort receipt disappeared during invalidation.");
        transaction.Commit();
        return receipt;
    }

    public void PrepareLanding(
        AcceptanceCohortReceipt receipt,
        string combinedCommitRevision,
        string? priorIntegrationRevision = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.HasAuthoritativeLandingEvidence)
        {
            throw new InvalidOperationException(
                "Cohort landing cannot be prepared without authoritative positive gate evidence.");
        }
        var commit = AcceptanceCohortMemberBinding.NormalizeRevision(
            combinedCommitRevision,
            nameof(combinedCommitRevision));
        var priorIntegration = string.IsNullOrWhiteSpace(priorIntegrationRevision)
            ? null
            : AcceptanceCohortMemberBinding.NormalizeRevision(
                priorIntegrationRevision,
                nameof(priorIntegrationRevision));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cohort_landing_intents(
                cohort_id, receipt_id, combined_commit_revision, prior_integration_revision, state, updated_at)
            VALUES ($cohort, $receipt, $commit, $priorIntegration, 'prepared', $updated)
            ON CONFLICT(cohort_id) DO UPDATE SET
                receipt_id=excluded.receipt_id,
                combined_commit_revision=excluded.combined_commit_revision,
                prior_integration_revision=excluded.prior_integration_revision,
                state='prepared',
                updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$cohort", receipt.Identity.Value);
        command.Parameters.AddWithValue("$receipt", receipt.ReceiptId);
        command.Parameters.AddWithValue("$commit", commit);
        command.Parameters.AddWithValue("$priorIntegration", (object?)priorIntegration ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AcceptanceCohortLandingRecovery> RecoverPreparedLandings(string executionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionDirectory);
        var prepared = new List<(string CohortId, string ReceiptId, string Commit, string? PriorIntegration)>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT cohort_id, receipt_id, combined_commit_revision, prior_integration_revision
                FROM cohort_landing_intents WHERE state='prepared' ORDER BY cohort_id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                prepared.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        var recovered = new List<AcceptanceCohortLandingRecovery>();
        foreach (var intent in prepared)
        {
            var receipt = TryReadReceipt(intent.CohortId);
            if (receipt is null ||
                !receipt.ReceiptId.Equals(intent.ReceiptId, StringComparison.Ordinal) ||
                !receipt.HasAuthoritativeLandingEvidence)
            {
                if (receipt is not null)
                {
                    _ = InvalidateLanding(
                        intent.CohortId,
                        AcceptanceCohortInvalidationReason.RecoveryEvidenceUnavailable,
                        "Prepared cohort landing no longer has its exact authoritative receipt evidence.");
                }
                continue;
            }
            if (GitCli.Run(
                    executionDirectory,
                    "merge-base", "--is-ancestor", intent.Commit, "refs/heads/main").ExitCode != 0)
            {
                if (RestoreIntegrationAfterInterruptedLanding(executionDirectory, intent))
                {
                    MarkLandingIntentState(intent.CohortId, intent.ReceiptId, "invalidated", expectedState: "prepared");
                }
                continue;
            }
            var coverage = FinalizeLanding(intent.CohortId, intent.ReceiptId);
            recovered.Add(new AcceptanceCohortLandingRecovery(receipt, intent.Commit, coverage));
        }
        return recovered;
    }

    public IReadOnlyList<AcceptanceCohortCoverage> FinalizeLanding(string cohortId, string receiptId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var authoritativeReceipt = ReadReceipt(connection, cohortId, transaction);
        if (authoritativeReceipt is null ||
            !authoritativeReceipt.ReceiptId.Equals(receiptId, StringComparison.Ordinal) ||
            !authoritativeReceipt.HasAuthoritativeLandingEvidence)
        {
            throw new InvalidOperationException(
                "Cohort landing cannot finalize without its exact authoritative passing receipt.");
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE cohort_members SET landed=1 WHERE cohort_id=$cohort;";
            update.Parameters.AddWithValue("$cohort", cohortId);
            if (update.ExecuteNonQuery() != 2)
            {
                throw new InvalidOperationException("Cohort landing must finalize exactly two member coverage rows.");
            }
        }
        using (var verifyIntent = connection.CreateCommand())
        {
            verifyIntent.Transaction = transaction;
            verifyIntent.CommandText = """
                SELECT COUNT(*) FROM cohort_landing_intents
                WHERE cohort_id=$cohort AND receipt_id=$receipt AND state='prepared';
                """;
            verifyIntent.Parameters.AddWithValue("$cohort", cohortId);
            verifyIntent.Parameters.AddWithValue("$receipt", receiptId);
            if (Convert.ToInt32(verifyIntent.ExecuteScalar()) != 1)
            {
                throw new InvalidOperationException("Prepared cohort landing intent was missing during finalization.");
            }
        }

        var coverage = ReadCoverage(connection, cohortId, receiptId, transaction);
        transaction.Commit();
        return coverage;
    }

    public void CompleteLandingEffects(string cohortId, string receiptId) =>
        MarkLandingIntentState(cohortId, receiptId, "finalized", expectedState: "prepared");

    public CohortAdmissionFairnessTransition ApplyAdmissionFairness(
        IReadOnlyCollection<GoalId> admittedGoalIds,
        GoalId oldestEligibleGoalId)
    {
        ArgumentNullException.ThrowIfNull(admittedGoalIds);
        if (admittedGoalIds.Count != 2 || admittedGoalIds.Distinct().Count() != 2)
        {
            throw new ArgumentException("Cohort admission fairness requires exactly two distinct admitted goals.", nameof(admittedGoalIds));
        }
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var previousOvertakeCount = ReadOvertakeCount(connection, transaction, oldestEligibleGoalId);
        foreach (var goalId in admittedGoalIds)
        {
            using var reset = connection.CreateCommand();
            reset.Transaction = transaction;
            reset.CommandText = "DELETE FROM cohort_fairness WHERE goal_id=$goal;";
            reset.Parameters.AddWithValue("$goal", goalId.Value);
            reset.ExecuteNonQuery();
        }
        if (!admittedGoalIds.Contains(oldestEligibleGoalId))
        {
            IncrementOvertake(connection, transaction, oldestEligibleGoalId);
        }
        var resultingOvertakeCount = ReadOvertakeCount(connection, transaction, oldestEligibleGoalId);
        transaction.Commit();
        return new CohortAdmissionFairnessTransition(
            oldestEligibleGoalId,
            previousOvertakeCount,
            resultingOvertakeCount,
            admittedGoalIds.Contains(oldestEligibleGoalId),
            admittedGoalIds.OrderBy(goalId => goalId.Value, StringComparer.Ordinal).ToArray());
    }

    public int ReadOvertakeCount(GoalId goalId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT overtake_count FROM cohort_fairness WHERE goal_id=$goal;";
        command.Parameters.AddWithValue("$goal", goalId.Value);
        return command.ExecuteScalar() is long count ? checked((int)count) : 0;
    }

    public void RecordOvertake(GoalId goalId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cohort_fairness(goal_id, overtake_count, updated_at)
            VALUES ($goal, 1, $updated)
            ON CONFLICT(goal_id) DO UPDATE SET
                overtake_count=cohort_fairness.overtake_count+1,
                updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$goal", goalId.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void ResetOvertake(GoalId goalId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM cohort_fairness WHERE goal_id=$goal;";
        command.Parameters.AddWithValue("$goal", goalId.Value);
        command.ExecuteNonQuery();
    }

    public void SuppressPair(string pairFingerprint, string cohortId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairFingerprint);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO cohort_pair_suppressions(pair_fingerprint, cohort_id, created_at)
            VALUES ($fingerprint, $cohort, $created);
            """;
        command.Parameters.AddWithValue("$fingerprint", pairFingerprint);
        command.Parameters.AddWithValue("$cohort", cohortId);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlySet<string> ReadSuppressedPairs()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pair_fingerprint FROM cohort_pair_suppressions ORDER BY pair_fingerprint;";
        using var reader = command.ExecuteReader();
        var values = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    private static void EnsureAttributionSideEffects(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string cohortId,
        AcceptanceCohortAttributionOutcome attribution,
        string pairFingerprint,
        GoalId? innocentGoalId)
    {
        var suppressionAlreadyExists = false;
        using (var existingSuppression = connection.CreateCommand())
        {
            existingSuppression.Transaction = transaction;
            existingSuppression.CommandText = "SELECT COUNT(*) FROM cohort_pair_suppressions WHERE pair_fingerprint=$fingerprint;";
            existingSuppression.Parameters.AddWithValue("$fingerprint", pairFingerprint);
            suppressionAlreadyExists = Convert.ToInt32(existingSuppression.ExecuteScalar()) == 1;
        }
        using var marker = connection.CreateCommand();
        marker.Transaction = transaction;
        marker.CommandText = """
            INSERT OR IGNORE INTO cohort_attribution_effects(
                cohort_id, attribution, pair_fingerprint, innocent_goal_id, applied_at)
            VALUES ($cohort, $attribution, $fingerprint, $innocent, $applied);
            """;
        marker.Parameters.AddWithValue("$cohort", cohortId);
        marker.Parameters.AddWithValue("$attribution", attribution.ToString());
        marker.Parameters.AddWithValue("$fingerprint", pairFingerprint);
        marker.Parameters.AddWithValue("$innocent", (object?)innocentGoalId?.Value ?? DBNull.Value);
        marker.Parameters.AddWithValue("$applied", DateTimeOffset.UtcNow.ToString("O"));
        if (marker.ExecuteNonQuery() == 0)
        {
            return;
        }

        using (var suppress = connection.CreateCommand())
        {
            suppress.Transaction = transaction;
            suppress.CommandText = """
                INSERT OR IGNORE INTO cohort_pair_suppressions(pair_fingerprint, cohort_id, created_at)
                VALUES ($fingerprint, $cohort, $created);
                """;
            suppress.Parameters.AddWithValue("$fingerprint", pairFingerprint);
            suppress.Parameters.AddWithValue("$cohort", cohortId);
            suppress.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            suppress.ExecuteNonQuery();
        }
        if (innocentGoalId is not null &&
            (!suppressionAlreadyExists || ReadOvertakeCount(connection, transaction, innocentGoalId) == 0))
        {
            IncrementOvertake(connection, transaction, innocentGoalId);
        }
    }

    private static int ReadOvertakeCount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GoalId goalId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT overtake_count FROM cohort_fairness WHERE goal_id=$goal;";
        command.Parameters.AddWithValue("$goal", goalId.Value);
        return command.ExecuteScalar() is long count ? checked((int)count) : 0;
    }

    private static void IncrementOvertake(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GoalId goalId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cohort_fairness(goal_id, overtake_count, updated_at)
            VALUES ($goal, 1, $updated)
            ON CONFLICT(goal_id) DO UPDATE SET
                overtake_count=cohort_fairness.overtake_count+1,
                updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$goal", goalId.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static bool RestoreIntegrationAfterInterruptedLanding(
        string executionDirectory,
        (string CohortId, string ReceiptId, string Commit, string? PriorIntegration) intent)
    {
        const string integrationRef = "refs/heads/integration";
        var current = GitCli.Run(executionDirectory, "rev-parse", "--verify", "--quiet", integrationRef);
        if (current.ExitCode != 0 ||
            !current.Output.Trim().Equals(intent.Commit, StringComparison.Ordinal))
        {
            return true;
        }
        var rollback = intent.PriorIntegration is null
            ? GitCli.Run(executionDirectory, "update-ref", "-d", integrationRef, intent.Commit)
            : GitCli.Run(
                executionDirectory,
                "update-ref",
                integrationRef,
                intent.PriorIntegration,
                intent.Commit);
        return rollback.ExitCode == 0;
    }

    private void MarkLandingIntentState(
        string cohortId,
        string receiptId,
        string state,
        string? expectedState = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = expectedState is null
            ? "UPDATE cohort_landing_intents SET state=$state, updated_at=$updated WHERE cohort_id=$cohort AND receipt_id=$receipt;"
            : "UPDATE cohort_landing_intents SET state=$state, updated_at=$updated WHERE cohort_id=$cohort AND receipt_id=$receipt AND state=$expected;";
        command.Parameters.AddWithValue("$cohort", cohortId);
        command.Parameters.AddWithValue("$receipt", receiptId);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        if (expectedState is not null)
        {
            command.Parameters.AddWithValue("$expected", expectedState);
        }
        if (command.ExecuteNonQuery() == 1)
        {
            return;
        }

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT state FROM cohort_landing_intents WHERE cohort_id=$cohort AND receipt_id=$receipt;";
        read.Parameters.AddWithValue("$cohort", cohortId);
        read.Parameters.AddWithValue("$receipt", receiptId);
        if (!string.Equals(read.ExecuteScalar() as string, state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cohort landing intent could not transition to '{state}'.");
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private AcceptanceCohortReceipt CaptureGateEvidence(AcceptanceCohortReceipt receipt)
    {
        var attemptCustodyKey = string.Join(
            ':',
            receipt.ReceiptId,
            receipt.CompletedAt.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            receipt.GateElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var receiptFolder = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(attemptCustodyKey)));
        var custodyDirectory = Path.Combine(
            _databasePath + ".artifacts",
            receipt.Identity.Value,
            receiptFolder);
        Directory.CreateDirectory(custodyDirectory);

        var artifacts = new List<AcceptanceCohortEvidenceArtifact>();
        var custodyTrxPaths = new List<string>();
        for (var index = 0; index < receipt.GateTestResultPaths.Count; index++)
        {
            var source = Path.GetFullPath(receipt.GateTestResultPaths[index]);
            if (!File.Exists(source))
            {
                if (receipt.Outcome == AcceptanceCohortGateOutcome.Passed || receipt.ValidForLanding)
                {
                    throw new InvalidDataException($"Cohort TRX evidence disappeared before custody: {source}");
                }
                continue;
            }

            var target = Path.Combine(custodyDirectory, $"trx-{index:D3}.trx");
            artifacts.Add(CopyContentBoundArtifact(source, target, "trx"));
            custodyTrxPaths.Add(Path.GetFullPath(target));
        }

        var verdictBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            receipt.ReceiptId,
            CohortId = receipt.Identity.Value,
            Outcome = receipt.Outcome.ToString(),
            receipt.CompletedAt,
            receipt.GateElapsedMilliseconds,
            FailedChecks = receipt.FailedChecks.ToArray(),
            receipt.GateExitCode,
            GateTestResultSha256 = artifacts
                .Where(artifact => artifact.Kind.Equals("trx", StringComparison.Ordinal))
                .Select(artifact => artifact.Sha256)
                .ToArray()
        });
        artifacts.Add(WriteContentBoundArtifact(
            Path.Combine(custodyDirectory, "gate-verdict.json"),
            verdictBytes,
            "gate-verdict"));

        return receipt with
        {
            GateTestResultPaths = custodyTrxPaths,
            GateEvidenceArtifacts = artifacts
        };
    }

    private static AcceptanceCohortEvidenceArtifact CopyContentBoundArtifact(
        string source,
        string target,
        string kind)
    {
        using var sourceStream = File.OpenRead(source);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(sourceStream));
        sourceStream.Position = 0;
        try
        {
            using var targetStream = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            sourceStream.CopyTo(targetStream);
            targetStream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(target))
        {
            using var existing = File.OpenRead(target);
            var existingHash = Convert.ToHexStringLower(SHA256.HashData(existing));
            if (!existingHash.Equals(expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Immutable cohort evidence target already contains different content: {target}");
            }
        }

        return new AcceptanceCohortEvidenceArtifact(
            kind,
            Path.GetFullPath(target),
            expectedHash,
            new FileInfo(target).Length);
    }

    private static AcceptanceCohortEvidenceArtifact WriteContentBoundArtifact(
        string target,
        byte[] content,
        string kind)
    {
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(content));
        try
        {
            using var stream = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(target))
        {
            using var existing = File.OpenRead(target);
            var existingHash = Convert.ToHexStringLower(SHA256.HashData(existing));
            if (!existingHash.Equals(expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Immutable cohort evidence target already contains different content: {target}");
            }
        }

        return new AcceptanceCohortEvidenceArtifact(
            kind,
            Path.GetFullPath(target),
            expectedHash,
            content.LongLength);
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS cohort_receipts(
                cohort_id TEXT PRIMARY KEY,
                receipt_id TEXT NOT NULL UNIQUE,
                main_revision TEXT NOT NULL,
                combined_tree_revision TEXT NOT NULL,
                manifest_identity TEXT NOT NULL,
                outcome TEXT NOT NULL,
                attribution TEXT NOT NULL,
                valid_for_landing INTEGER NOT NULL,
                completed_at TEXT NOT NULL,
                gate_elapsed_ms INTEGER NOT NULL,
                failed_checks_json TEXT NOT NULL,
                infrastructure_reason_code TEXT NULL,
                infrastructure_detail TEXT NULL,
                gate_exit_code INTEGER NULL,
                gate_test_result_paths_json TEXT NOT NULL DEFAULT '[]',
                gate_evidence_artifacts_json TEXT NOT NULL DEFAULT '[]');
            CREATE TABLE IF NOT EXISTS cohort_members(
                cohort_id TEXT NOT NULL REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                member_ordinal INTEGER NOT NULL CHECK(member_ordinal IN (0,1)),
                goal_id TEXT NOT NULL,
                branch_revision TEXT NOT NULL,
                candidate_revision TEXT NOT NULL,
                landing_paths_json TEXT NOT NULL,
                resource_keys_json TEXT NOT NULL,
                risk_tier TEXT NOT NULL,
                promotion_disposition TEXT NOT NULL,
                merge_status TEXT NOT NULL,
                merge_reason TEXT NOT NULL,
                landed INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(cohort_id, member_ordinal),
                UNIQUE(cohort_id, goal_id));
            CREATE TABLE IF NOT EXISTS cohort_landing_intents(
                cohort_id TEXT PRIMARY KEY REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                receipt_id TEXT NOT NULL,
                combined_commit_revision TEXT NOT NULL,
                prior_integration_revision TEXT NULL,
                state TEXT NOT NULL CHECK(state IN ('prepared','finalized','invalidated')),
                updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cohort_partition_receipts(
                cohort_id TEXT NOT NULL REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                member_ordinal INTEGER NOT NULL CHECK(member_ordinal IN (0,1)),
                receipt_id TEXT NOT NULL,
                goal_id TEXT NOT NULL,
                candidate_revision TEXT NOT NULL,
                main_revision TEXT NOT NULL,
                tree_revision TEXT NULL,
                manifest_identity TEXT NOT NULL,
                outcome TEXT NOT NULL,
                elapsed_ms INTEGER NOT NULL,
                test_result_paths_json TEXT NOT NULL,
                PRIMARY KEY(cohort_id, member_ordinal));
            CREATE TABLE IF NOT EXISTS cohort_fairness(
                goal_id TEXT PRIMARY KEY,
                overtake_count INTEGER NOT NULL,
                updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cohort_pair_suppressions(
                pair_fingerprint TEXT PRIMARY KEY,
                cohort_id TEXT NOT NULL,
                created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cohort_attribution_effects(
                cohort_id TEXT PRIMARY KEY REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                attribution TEXT NOT NULL,
                pair_fingerprint TEXT NOT NULL,
                innocent_goal_id TEXT NULL,
                applied_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cohort_materialization_failures(
                attempt_id TEXT PRIMARY KEY,
                first_goal_id TEXT NOT NULL,
                first_branch_revision TEXT NOT NULL,
                first_candidate_revision TEXT NOT NULL,
                second_goal_id TEXT NOT NULL,
                second_branch_revision TEXT NOT NULL,
                second_candidate_revision TEXT NOT NULL,
                main_revision TEXT NOT NULL,
                outcome TEXT NOT NULL,
                detail TEXT NOT NULL,
                recorded_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cohort_invalidations(
                invalidation_id TEXT PRIMARY KEY,
                cohort_id TEXT NOT NULL REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                reason TEXT NOT NULL,
                detail TEXT NOT NULL,
                invalidated_at TEXT NOT NULL,
                observed_main_revision TEXT NOT NULL,
                combined_tree_revision TEXT NOT NULL,
                manifest_identity TEXT NOT NULL,
                ordered_member_bindings_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_cohort_invalidations_cohort
                ON cohort_invalidations(cohort_id, invalidated_at);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "cohort_members", "risk_tier", "TEXT NOT NULL DEFAULT 'DocsOnly'");
        EnsureColumn(connection, "cohort_members", "promotion_disposition", "TEXT NOT NULL DEFAULT 'Auto'");
        EnsureColumn(connection, "cohort_members", "merge_status", "TEXT NOT NULL DEFAULT 'Clean'");
        EnsureColumn(connection, "cohort_members", "merge_reason", "TEXT NOT NULL DEFAULT 'NoConflictsDetected'");
        EnsureColumn(connection, "cohort_receipts", "gate_exit_code", "INTEGER NULL");
        EnsureColumn(connection, "cohort_receipts", "infrastructure_reason_code", "TEXT NULL");
        EnsureColumn(connection, "cohort_receipts", "infrastructure_detail", "TEXT NULL");
        EnsureColumn(connection, "cohort_receipts", "gate_test_result_paths_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "cohort_receipts", "gate_evidence_artifacts_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "cohort_receipts", "attributed_members_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "cohort_receipts", "unrelated_failures_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "cohort_landing_intents", "prior_integration_revision", "TEXT NULL");
        EnsureReusablePartitionReceiptSchema(connection);
        EnsureColumn(connection, "cohort_partition_receipts", "failed_checks_json", "TEXT NULL");
        EnsureColumn(connection, "cohort_partition_receipts", "failing_test_identities_json", "TEXT NULL");
        using var invalidateLegacy = connection.CreateCommand();
        invalidateLegacy.CommandText = """
            UPDATE cohort_landing_intents
            SET state='invalidated', updated_at=$updated
            WHERE cohort_id NOT LIKE 'cohort-v2-%' AND state='prepared';
            """;
        invalidateLegacy.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        invalidateLegacy.ExecuteNonQuery();
    }

    private static void EnsureReusablePartitionReceiptSchema(SqliteConnection connection)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='cohort_partition_receipts';";
        var schema = inspect.ExecuteScalar() as string;
        if (schema?.Contains("receipt_id TEXT NOT NULL UNIQUE", StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        using var migrate = connection.CreateCommand();
        migrate.CommandText = """
            ALTER TABLE cohort_partition_receipts RENAME TO cohort_partition_receipts_legacy;
            CREATE TABLE cohort_partition_receipts(
                cohort_id TEXT NOT NULL REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                member_ordinal INTEGER NOT NULL CHECK(member_ordinal IN (0,1)),
                receipt_id TEXT NOT NULL,
                goal_id TEXT NOT NULL,
                candidate_revision TEXT NOT NULL,
                main_revision TEXT NOT NULL,
                tree_revision TEXT NULL,
                manifest_identity TEXT NOT NULL,
                outcome TEXT NOT NULL,
                elapsed_ms INTEGER NOT NULL,
                test_result_paths_json TEXT NOT NULL,
                PRIMARY KEY(cohort_id, member_ordinal));
            INSERT INTO cohort_partition_receipts(
                cohort_id, member_ordinal, receipt_id, goal_id, candidate_revision,
                main_revision, tree_revision, manifest_identity, outcome, elapsed_ms, test_result_paths_json)
            SELECT cohort_id, member_ordinal, receipt_id, goal_id, candidate_revision,
                main_revision, tree_revision, manifest_identity, outcome, elapsed_ms, test_result_paths_json
            FROM cohort_partition_receipts_legacy;
            DROP TABLE cohort_partition_receipts_legacy;
            """;
        migrate.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table});";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        }
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static AcceptanceCohortReceipt? ReadReceipt(
        SqliteConnection connection,
        string cohortId,
        SqliteTransaction? transaction)
    {
        if (!cohortId.StartsWith($"{AcceptanceCohortIdentity.Version}-", StringComparison.Ordinal))
        {
            return null;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT receipt_id, main_revision, combined_tree_revision, manifest_identity, outcome,
                   attribution, valid_for_landing, completed_at, gate_elapsed_ms, failed_checks_json,
                   infrastructure_reason_code, infrastructure_detail,
                   gate_exit_code, gate_test_result_paths_json, gate_evidence_artifacts_json,
                   attributed_members_json, unrelated_failures_json
            FROM cohort_receipts WHERE cohort_id=$cohort;
            """;
        command.Parameters.AddWithValue("$cohort", cohortId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var receiptId = reader.GetString(0);
        var main = reader.GetString(1);
        var tree = reader.GetString(2);
        var manifest = reader.GetString(3);
        var outcome = Enum.Parse<AcceptanceCohortGateOutcome>(reader.GetString(4));
        var attribution = Enum.Parse<AcceptanceCohortAttributionOutcome>(reader.GetString(5));
        var valid = reader.GetInt32(6) == 1;
        var completed = DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture);
        var elapsed = reader.GetInt64(8);
        var failed = JsonSerializer.Deserialize<string[]>(reader.GetString(9)) ?? [];
        var infrastructureReasonCode = reader.IsDBNull(10) ? null : reader.GetString(10);
        var infrastructureDetail = reader.IsDBNull(11) ? null : reader.GetString(11);
        if (outcome == AcceptanceCohortGateOutcome.InfrastructureFailure)
        {
            infrastructureReasonCode = string.IsNullOrWhiteSpace(infrastructureReasonCode)
                ? AcceptanceCohortInfrastructureReasonCodes.LegacyUnknown
                : infrastructureReasonCode;
        }
        else
        {
            infrastructureReasonCode = null;
            infrastructureDetail = null;
        }
        int? gateExitCode = reader.IsDBNull(12) ? null : reader.GetInt32(12);
        var gateTestResultPaths = JsonSerializer.Deserialize<string[]>(reader.GetString(13)) ?? [];
        var gateEvidenceArtifacts = JsonSerializer.Deserialize<AcceptanceCohortEvidenceArtifact[]>(reader.GetString(14)) ?? [];
        var attributedMembers = JsonSerializer.Deserialize<AcceptanceCohortAttributedMember[]>(reader.GetString(15)) ?? [];
        var unrelatedFailures = JsonSerializer.Deserialize<AcceptanceCohortUnrelatedFailure[]>(reader.GetString(16)) ?? [];
        reader.Close();

        using var membersCommand = connection.CreateCommand();
        membersCommand.Transaction = transaction;
        membersCommand.CommandText = """
            SELECT goal_id, branch_revision, candidate_revision, landing_paths_json, resource_keys_json,
                   risk_tier, promotion_disposition, merge_status, merge_reason
            FROM cohort_members WHERE cohort_id=$cohort ORDER BY member_ordinal;
            """;
        membersCommand.Parameters.AddWithValue("$cohort", cohortId);
        using var membersReader = membersCommand.ExecuteReader();
        var members = new List<AcceptanceCohortMemberBinding>(2);
        while (membersReader.Read())
        {
            members.Add(new AcceptanceCohortMemberBinding(
                new GoalId(membersReader.GetString(0)),
                membersReader.GetString(1),
                membersReader.GetString(2),
                JsonSerializer.Deserialize<string[]>(membersReader.GetString(3)) ?? [],
                JsonSerializer.Deserialize<string[]>(membersReader.GetString(4)) ?? [],
                Enum.Parse<Mcg.AgentOrchestrator.Core.Conductor.ChangeRiskTier>(membersReader.GetString(5)),
                Enum.Parse<Mcg.AgentOrchestrator.Core.Conductor.ConductorTransitionDecision>(membersReader.GetString(6)),
                membersReader.GetString(7),
                membersReader.GetString(8)));
        }
        membersReader.Close();
        if (members.Count != 2)
        {
            throw new InvalidDataException($"Cohort receipt {cohortId} does not have exactly two member rows.");
        }

        var identity = AcceptanceCohortIdentity.Create(members, main, tree, manifest);
        if (!identity.Value.Equals(cohortId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Stored cohort identity {cohortId} failed canonical reconstruction.");
        }
        AcceptanceCohortInvalidation? invalidation = null;
        using (var invalidationCommand = connection.CreateCommand())
        {
            invalidationCommand.Transaction = transaction;
            invalidationCommand.CommandText = """
                SELECT invalidation_id, reason, detail, invalidated_at, observed_main_revision,
                       combined_tree_revision, manifest_identity, ordered_member_bindings_json
                FROM cohort_invalidations
                WHERE cohort_id=$cohort
                ORDER BY invalidated_at DESC, invalidation_id DESC
                LIMIT 1;
                """;
            invalidationCommand.Parameters.AddWithValue("$cohort", cohortId);
            using var invalidationReader = invalidationCommand.ExecuteReader();
            if (invalidationReader.Read())
            {
                invalidation = new AcceptanceCohortInvalidation(
                    invalidationReader.GetString(0),
                    cohortId,
                    Enum.Parse<AcceptanceCohortInvalidationReason>(invalidationReader.GetString(1)),
                    invalidationReader.GetString(2),
                    DateTimeOffset.Parse(invalidationReader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                    invalidationReader.GetString(4),
                    invalidationReader.GetString(5),
                    invalidationReader.GetString(6),
                    JsonSerializer.Deserialize<AcceptanceCohortMemberBinding[]>(invalidationReader.GetString(7)) ?? []);
                var invalidationIdentity = AcceptanceCohortIdentity.Create(
                    invalidation.OrderedMembers,
                    invalidation.ObservedMainRevision,
                    invalidation.CombinedTreeRevision,
                    invalidation.ManifestIdentity);
                if (!invalidation.ObservedMainRevision.Equals(identity.ObservedMainRevision, StringComparison.Ordinal) ||
                    !invalidation.CombinedTreeRevision.Equals(identity.CombinedTreeRevision, StringComparison.Ordinal) ||
                    !invalidation.ManifestIdentity.Equals(identity.ManifestIdentity, StringComparison.Ordinal) ||
                    !invalidationIdentity.Value.Equals(identity.Value, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Stored cohort invalidation {invalidation.InvalidationId} does not bind its receipt identity.");
                }
            }
        }

        return new AcceptanceCohortReceipt(
            receiptId, identity, outcome, completed, elapsed, failed, gateExitCode,
            gateTestResultPaths, attribution, valid,
            infrastructureReasonCode, infrastructureDetail)
        {
            GateEvidenceArtifacts = gateEvidenceArtifacts,
            Invalidation = invalidation,
            AttributedMembers = attributedMembers,
            UnrelatedFailures = unrelatedFailures
        };
    }

    private static void ValidateInfrastructureClassification(AcceptanceCohortReceipt receipt)
    {
        if (receipt.Outcome == AcceptanceCohortGateOutcome.InfrastructureFailure)
        {
            if (!AcceptanceCohortInfrastructureReasonCodes.IsSingleToken(receipt.InfrastructureReasonCode))
            {
                throw new ArgumentException(
                    "An infrastructure-failure cohort receipt requires a nonblank single-token reason code.",
                    nameof(receipt));
            }
            if (receipt.InfrastructureDetail is { Length: > 256 } ||
                receipt.InfrastructureDetail?.IndexOfAny(['\r', '\n', '\t']) >= 0)
            {
                throw new ArgumentException(
                    "An infrastructure-failure cohort receipt detail must be single-line and at most 256 characters.",
                    nameof(receipt));
            }
            return;
        }

        if (receipt.InfrastructureReasonCode is not null || receipt.InfrastructureDetail is not null)
        {
            throw new ArgumentException(
                "Only infrastructure-failure cohort receipts may carry infrastructure reason evidence.",
                nameof(receipt));
        }
    }

    private static IReadOnlyList<AcceptanceCohortCoverage> ReadCoverage(
        SqliteConnection connection,
        string cohortId,
        string receiptId,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT goal_id, landed FROM cohort_members WHERE cohort_id=$cohort ORDER BY member_ordinal;";
        command.Parameters.AddWithValue("$cohort", cohortId);
        using var reader = command.ExecuteReader();
        var values = new List<AcceptanceCohortCoverage>(2);
        while (reader.Read())
        {
            values.Add(new AcceptanceCohortCoverage(
                new GoalId(reader.GetString(0)), cohortId, receiptId, reader.GetInt32(1) == 1));
        }
        return values;
    }
}
