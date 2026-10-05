using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class LegacySourceBacklogOwnerAmbiguousException(string backlogItemId, IReadOnlyList<string> linkedGoalIds)
    : InvalidOperationException($"SOURCE_BACKLOG_CLAIM_CONFLICT reason=legacy-owner-ambiguous backlogItem={backlogItemId} linkedGoals={string.Join(',', linkedGoalIds)}")
{
    public string BacklogItemId { get; } = backlogItemId;
    public IReadOnlyList<string> LinkedGoalIds { get; } = linkedGoalIds;
}

public sealed class SourceBacklogClaimConflictException(
    string backlogItemId,
    string attemptedPredecessorGoalId,
    string? observedOwnerGoalId,
    long? observedVersion)
    : InvalidOperationException(
        $"GOAL_REPLACE_CURRENT_OWNER_CONFLICT backlogItem={backlogItemId} predecessor={attemptedPredecessorGoalId} currentOwner={observedOwnerGoalId ?? "none"} claimVersion={observedVersion?.ToString(CultureInfo.InvariantCulture) ?? "none"}")
{
    public string BacklogItemId { get; } = backlogItemId;
    public string AttemptedPredecessorGoalId { get; } = attemptedPredecessorGoalId;
    public string? ObservedOwnerGoalId { get; } = observedOwnerGoalId;
    public long? ObservedVersion { get; } = observedVersion;
}

public sealed class GoalReplacementIdempotencyConflictException(Guid requestId)
    : InvalidOperationException($"GOAL_REPLACE_IDEMPOTENCY_CONFLICT requestId={requestId:D} reason=fingerprint-mismatch");

public sealed class SourceBacklogClaimStore(string databasePath)
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);

    public SourceBacklogClaimSnapshot? ResolveClaim(AgentOrchestratorKernel kernel, string backlogItemId)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        if (!File.Exists(_databasePath))
            return ResolveLegacyClaim(kernel, backlogItemId, materialize: false);

        var persisted = WithReadConnection(connection => ReadClaim(connection, backlogItemId));
        if (persisted is not null)
            return persisted;

        return ResolveLegacyClaim(kernel, backlogItemId, materialize: false);
    }

    public SourceBacklogClaimSnapshot EnsureClaimForNewGoal(
        AgentOrchestratorKernel kernel,
        string backlogItemId,
        string newOwnerGoalId,
        SourceBacklogCoverage coverage)
    {
        SourceBacklogClaimSnapshot? result = null;
        WithWriteConnection(connection =>
        {
            result = ReadClaim(connection, backlogItemId);
            if (result is not null)
                return;

            var legacy = ResolveLegacyClaim(kernel, backlogItemId, materialize: true, connection);
            if (legacy is not null)
            {
                result = legacy;
                return;
            }

            result = InsertClaim(connection, backlogItemId, newOwnerGoalId, coverage, version: 1);
        });
        return result!;
    }

    public SourceBacklogClaimSnapshot ResolveOrMaterializeClaim(AgentOrchestratorKernel kernel, string backlogItemId)
    {
        SourceBacklogClaimSnapshot? result = null;
        WithWriteConnection(connection =>
        {
            result = ReadClaim(connection, backlogItemId) ??
                ResolveLegacyClaim(kernel, backlogItemId, materialize: true, connection);
        });
        return result ?? throw new InvalidOperationException(
            $"SOURCE_BACKLOG_CLAIM_MISSING backlogItem={backlogItemId}");
    }

    public GoalReplacementAuditSnapshot? FindAudit(Guid requestId) =>
        WithReadConnection(connection => ReadAudit(connection, requestId));

    public GoalReplacementAuditSnapshot? FindAuditInCurrentTransaction(Guid requestId)
    {
        GoalReplacementAuditSnapshot? result = null;
        WithWriteConnection(connection => result = ReadAudit(connection, requestId));
        return result;
    }

    public SourceBacklogClaimSnapshot CommitReplacement(
        GoalReplacementAuditSnapshot audit,
        IGoalReplacementTransferAuthority authority,
        GoalReplacementEligibilityFacts validatedFacts,
        string replacementLeaseOwner)
    {
        SourceBacklogClaimSnapshot? result = null;
        WithWriteConnection(connection =>
        {
            var existingAudit = ReadAudit(connection, audit.RequestId);
            if (existingAudit is not null)
            {
                if (!string.Equals(existingAudit.Fingerprint, audit.Fingerprint, StringComparison.Ordinal))
                    throw new GoalReplacementIdempotencyConflictException(audit.RequestId);

                result = ReadClaim(connection, audit.BacklogItemId);
                return;
            }

            var authoritySnapshot = authority.Snapshot;
            if (authoritySnapshot.AuthorityId == Guid.Empty ||
                authoritySnapshot.ExpiresAt <= authoritySnapshot.IssuedAt ||
                string.IsNullOrWhiteSpace(replacementLeaseOwner) ||
                !string.Equals(authoritySnapshot.BacklogItemId, audit.BacklogItemId, StringComparison.Ordinal) ||
                !string.Equals(authoritySnapshot.PredecessorGoalId, audit.PredecessorGoalId, StringComparison.Ordinal) ||
                !string.Equals(authoritySnapshot.PredecessorGoalId, audit.ExpectedOwnerGoalId, StringComparison.Ordinal) ||
                authoritySnapshot.ExpectedClaimVersion != audit.ExpectedClaimVersion ||
                !string.Equals(authoritySnapshot.EvidenceToken, audit.EligibilityFacts.EvidenceToken, StringComparison.Ordinal))
            {
                throw new GoalReplacementTransferAuthorityException("eligibility-authority-binding-mismatch");
            }

            using (var lease = connection.CreateCommand())
            {
                lease.CommandText = "SELECT 1 FROM reconcile_acceptance_leases WHERE goal_id = $goal AND owner = $owner";
                lease.Parameters.AddWithValue("$goal", audit.PredecessorGoalId);
                lease.Parameters.AddWithValue("$owner", replacementLeaseOwner);
                if (lease.ExecuteScalar() is null)
                    throw new GoalReplacementTransferAuthorityException("eligibility-authority-lease-missing");
            }

            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE source_backlog_claims
                SET owner_goal_id = $successor,
                    version = version + 1,
                    updated_at = $updated_at
                WHERE backlog_item_id = $backlog_item_id
                  AND owner_goal_id = $predecessor
                  AND version = $version
                """;
            update.Parameters.AddWithValue("$successor", audit.SuccessorGoalId!);
            update.Parameters.AddWithValue("$updated_at", audit.AttemptedAt.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$backlog_item_id", audit.BacklogItemId);
            update.Parameters.AddWithValue("$predecessor", audit.ExpectedOwnerGoalId);
            update.Parameters.AddWithValue("$version", audit.ExpectedClaimVersion);
            if (DateTimeOffset.UtcNow >= authoritySnapshot.ExpiresAt)
                throw new GoalReplacementTransferAuthorityException("eligibility-authority-expired", validatedFacts);
            if (!validatedFacts.IsGitEvidenceAvailable)
                throw new GoalReplacementTransferAuthorityException("eligibility-evidence-indeterminate", validatedFacts);
            if (string.IsNullOrWhiteSpace(validatedFacts.EvidenceToken))
                throw new GoalReplacementTransferAuthorityException("eligibility-evidence-missing", validatedFacts);
            if (!string.Equals(validatedFacts.EvidenceToken, authoritySnapshot.EvidenceToken, StringComparison.Ordinal))
                throw new GoalReplacementTransferAuthorityException("eligibility-evidence-changed", validatedFacts);
            if (SourceBacklogClaimEligibility.Evaluate(audit.Disposition, validatedFacts) != GoalReplacementOutcome.Succeeded)
            {
                throw new GoalReplacementTransferAuthorityException(
                    "eligibility-no-longer-authorized",
                    validatedFacts);
            }
            if (update.ExecuteNonQuery() != 1)
            {
                var observed = ReadClaim(connection, audit.BacklogItemId);
                throw new SourceBacklogClaimConflictException(
                    audit.BacklogItemId,
                    audit.PredecessorGoalId,
                    observed?.OwnerGoalId,
                    observed?.Version);
            }

            using var lineage = connection.CreateCommand();
            lineage.CommandText = """
                INSERT INTO goal_replacement_lineage
                    (predecessor_goal_id, successor_goal_id, backlog_item_id, request_id, replaced_at)
                VALUES ($predecessor, $successor, $backlog_item_id, $request_id, $replaced_at)
                """;
            lineage.Parameters.AddWithValue("$predecessor", audit.PredecessorGoalId);
            lineage.Parameters.AddWithValue("$successor", audit.SuccessorGoalId!);
            lineage.Parameters.AddWithValue("$backlog_item_id", audit.BacklogItemId);
            lineage.Parameters.AddWithValue("$request_id", audit.RequestId.ToString("D"));
            lineage.Parameters.AddWithValue("$replaced_at", audit.AttemptedAt.ToString("O", CultureInfo.InvariantCulture));
            lineage.ExecuteNonQuery();

            InsertAudit(connection, audit);
            result = ReadClaim(connection, audit.BacklogItemId);
        });
        return result!;
    }

    public void CommitRejectedAttempt(GoalReplacementAuditSnapshot audit)
    {
        WithWriteConnection(connection =>
        {
            var existing = ReadAudit(connection, audit.RequestId);
            if (existing is not null)
            {
                if (!string.Equals(existing.Fingerprint, audit.Fingerprint, StringComparison.Ordinal))
                    throw new GoalReplacementIdempotencyConflictException(audit.RequestId);
                return;
            }
            InsertAudit(connection, audit);
        });
    }

    public void RecordFailedAttempt(GoalReplacementAuditSnapshot audit)
    {
        WithStandaloneTransaction(connection =>
        {
            var existing = ReadAudit(connection, audit.RequestId);
            if (existing is not null)
            {
                if (!string.Equals(existing.Fingerprint, audit.Fingerprint, StringComparison.Ordinal))
                    throw new GoalReplacementIdempotencyConflictException(audit.RequestId);
                return;
            }

            InsertAudit(connection, audit);
        });
    }

    public IReadOnlyList<GoalReplacementLineageSnapshot> ListLineage(string backlogItemId) =>
        WithReadConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT backlog_item_id, predecessor_goal_id, successor_goal_id, request_id, replaced_at
                FROM goal_replacement_lineage
                WHERE backlog_item_id = $backlog_item_id
                ORDER BY replaced_at, request_id
                """;
            command.Parameters.AddWithValue("$backlog_item_id", backlogItemId);
            using var reader = command.ExecuteReader();
            var records = new List<GoalReplacementLineageSnapshot>();
            while (reader.Read())
            {
                records.Add(new GoalReplacementLineageSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    Guid.Parse(reader.GetString(3)),
                    ParseDate(reader.GetString(4))));
            }
            return records;
        });

    private SourceBacklogClaimSnapshot? ResolveLegacyClaim(
        AgentOrchestratorKernel kernel,
        string backlogItemId,
        bool materialize,
        SqliteConnection? connection = null)
    {
        var linked = kernel.Goals
            .Where(goal => string.Equals(goal.SourceBacklogItemId, backlogItemId, StringComparison.Ordinal))
            .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal)
            .ToArray();
        if (linked.Length == 0)
            return null;
        if (linked.Length > 1)
            throw new LegacySourceBacklogOwnerAmbiguousException(backlogItemId, linked.Select(goal => goal.Id.Value).ToArray());

        var owner = linked[0];
        var coverage = owner.SourceBacklogCoverage ?? SourceBacklogCoverage.Full;
        return materialize
            ? InsertClaim(connection!, backlogItemId, owner.Id.Value, coverage, version: 1)
            : new SourceBacklogClaimSnapshot(backlogItemId, owner.Id.Value, coverage, 1, DateTimeOffset.MinValue);
    }

    private static SourceBacklogClaimSnapshot InsertClaim(
        SqliteConnection connection,
        string backlogItemId,
        string ownerGoalId,
        SourceBacklogCoverage coverage,
        long version)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO source_backlog_claims (backlog_item_id, owner_goal_id, coverage, version, updated_at)
            VALUES ($backlog_item_id, $owner_goal_id, $coverage, $version, $updated_at)
            """;
        command.Parameters.AddWithValue("$backlog_item_id", backlogItemId);
        command.Parameters.AddWithValue("$owner_goal_id", ownerGoalId);
        command.Parameters.AddWithValue("$coverage", coverage.ToString());
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        return new SourceBacklogClaimSnapshot(backlogItemId, ownerGoalId, coverage, version, updatedAt);
    }

    private static SourceBacklogClaimSnapshot? ReadClaim(SqliteConnection connection, string backlogItemId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT backlog_item_id, owner_goal_id, coverage, version, updated_at
            FROM source_backlog_claims
            WHERE backlog_item_id = $backlog_item_id
            """;
        command.Parameters.AddWithValue("$backlog_item_id", backlogItemId);
        using var reader = command.ExecuteReader();
        return !reader.Read()
            ? null
            : new SourceBacklogClaimSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<SourceBacklogCoverage>(reader.GetString(2)),
                reader.GetInt64(3),
                ParseDate(reader.GetString(4)));
    }

    private static GoalReplacementAuditSnapshot? ReadAudit(SqliteConnection connection, Guid requestId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request_id, fingerprint, outcome, backlog_item_id, predecessor_goal_id,
                   successor_goal_id, disposition, reason, old_status, new_status, coverage,
                   actor, channel, authentication_assurance, attempted_at,
                   expected_owner_goal_id, expected_claim_version, observed_owner_goal_id,
                   observed_claim_version, eligibility_facts_json, failure_code,
                   objective_hash, ordered_roles, assigned_agents
            FROM goal_replacement_audit
            WHERE request_id = $request_id
            """;
        command.Parameters.AddWithValue("$request_id", requestId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new GoalReplacementAuditSnapshot(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            Enum.Parse<GoalReplacementOutcome>(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            Enum.Parse<GoalReplacementDisposition>(reader.GetString(6)),
            reader.GetString(7),
            Enum.Parse<GoalStatus>(reader.GetString(8)),
            reader.IsDBNull(9) ? null : Enum.Parse<GoalStatus>(reader.GetString(9)),
            Enum.Parse<SourceBacklogCoverage>(reader.GetString(10)),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            ParseDate(reader.GetString(14)),
            reader.GetString(15),
            reader.GetInt64(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetInt64(18),
            JsonSerializer.Deserialize<GoalReplacementEligibilityFacts>(reader.GetString(19))!,
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.GetString(21),
            reader.GetString(22),
            reader.GetString(23));
    }

    private static void InsertAudit(SqliteConnection connection, GoalReplacementAuditSnapshot audit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO goal_replacement_audit
                (request_id, fingerprint, outcome, backlog_item_id, predecessor_goal_id,
                 successor_goal_id, disposition, reason, old_status, new_status, coverage,
                 actor, channel, authentication_assurance, attempted_at,
                 expected_owner_goal_id, expected_claim_version, observed_owner_goal_id,
                 observed_claim_version, eligibility_facts_json, failure_code,
                 objective_hash, ordered_roles, assigned_agents)
            VALUES
                ($request_id, $fingerprint, $outcome, $backlog_item_id, $predecessor_goal_id,
                 $successor_goal_id, $disposition, $reason, $old_status, $new_status, $coverage,
                 $actor, $channel, $authentication_assurance, $attempted_at,
                 $expected_owner_goal_id, $expected_claim_version, $observed_owner_goal_id,
                  $observed_claim_version, $eligibility_facts_json, $failure_code,
                   $objective_hash, $ordered_roles, $assigned_agents)
            """;
        command.Parameters.AddWithValue("$request_id", audit.RequestId.ToString("D"));
        command.Parameters.AddWithValue("$fingerprint", audit.Fingerprint);
        command.Parameters.AddWithValue("$outcome", audit.Outcome.ToString());
        command.Parameters.AddWithValue("$backlog_item_id", audit.BacklogItemId);
        command.Parameters.AddWithValue("$predecessor_goal_id", audit.PredecessorGoalId);
        command.Parameters.AddWithValue("$successor_goal_id", (object?)audit.SuccessorGoalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$disposition", audit.Disposition.ToString());
        command.Parameters.AddWithValue("$reason", audit.Reason);
        command.Parameters.AddWithValue("$old_status", audit.OldStatus.ToString());
        command.Parameters.AddWithValue("$new_status", audit.NewStatus is null ? DBNull.Value : audit.NewStatus.Value.ToString());
        command.Parameters.AddWithValue("$coverage", audit.Coverage.ToString());
        command.Parameters.AddWithValue("$actor", audit.Actor);
        command.Parameters.AddWithValue("$channel", audit.Channel);
        command.Parameters.AddWithValue("$authentication_assurance", audit.AuthenticationAssurance);
        command.Parameters.AddWithValue("$attempted_at", audit.AttemptedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expected_owner_goal_id", audit.ExpectedOwnerGoalId);
        command.Parameters.AddWithValue("$expected_claim_version", audit.ExpectedClaimVersion);
        command.Parameters.AddWithValue("$observed_owner_goal_id", (object?)audit.ObservedOwnerGoalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$observed_claim_version", (object?)audit.ObservedClaimVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$eligibility_facts_json", JsonSerializer.Serialize(audit.EligibilityFacts));
        command.Parameters.AddWithValue("$failure_code", (object?)audit.FailureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$objective_hash", audit.ObjectiveHash);
        command.Parameters.AddWithValue("$ordered_roles", audit.OrderedRoles);
        command.Parameters.AddWithValue("$assigned_agents", audit.AssignedAgents);
        command.ExecuteNonQuery();
    }

    private T WithReadConnection<T>(Func<SqliteConnection, T> action)
    {
        T? result = default;
        if (StateDbWriteSession.TryExecute(_databasePath, connection => result = action(connection)))
            return result!;

        using var connection = StateDbConnectionFactory.Open(_databasePath, StateDbConnectionProfile.QueryOnlyRead);
        return action(connection);
    }

    private void WithWriteConnection(Action<SqliteConnection> action)
    {
        if (TryWithWriteConnection(action))
            return;

        throw new InvalidOperationException(
            "Source backlog claim writes must join the active state transaction.");
    }

    private bool TryWithWriteConnection(Action<SqliteConnection> action) =>
        StateDbWriteSession.TryExecute(_databasePath, action);

    private void WithStandaloneTransaction(Action<SqliteConnection> action)
    {
        using var connection = StateDbConnectionFactory.Open(_databasePath, StateDbConnectionProfile.ReadWrite);
        using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE";
        begin.ExecuteNonQuery();
        try
        {
            action(connection);
            using var commit = connection.CreateCommand();
            commit.CommandText = "COMMIT";
            commit.ExecuteNonQuery();
        }
        catch
        {
            try
            {
                using var rollback = connection.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                rollback.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
            }
            throw;
        }
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
