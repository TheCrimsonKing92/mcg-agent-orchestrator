using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record MergeTrainCoverage(GoalId GoalId, string TrainId, string ReceiptId, bool Landed);
public sealed record MergeTrainLandingRecovery(
    MergeTrainReceipt Receipt,
    string CommitRevision,
    IReadOnlyList<MergeTrainCoverage> Coverage);

public sealed class MergeTrainAcceptanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _databasePath;

    public MergeTrainAcceptanceStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS merge_train_receipts(
                train_id TEXT PRIMARY KEY,
                receipt_id TEXT NOT NULL UNIQUE,
                payload_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS merge_train_landings(
                train_id TEXT PRIMARY KEY REFERENCES merge_train_receipts(train_id),
                receipt_id TEXT NOT NULL,
                commit_revision TEXT NOT NULL,
                prior_integration_revision TEXT NULL,
                state TEXT NOT NULL CHECK(state IN ('prepared','finalized')),
                updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS merge_train_ejections(
                ejection_id INTEGER PRIMARY KEY AUTOINCREMENT,
                train_attempt_id TEXT NOT NULL,
                goal_id TEXT NOT NULL,
                reason TEXT NOT NULL,
                conflict_paths_json TEXT NOT NULL,
                detail TEXT NOT NULL,
                recorded_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS merge_train_implicated_candidates(
                goal_id TEXT NOT NULL,
                candidate_revision TEXT NOT NULL,
                train_id TEXT NOT NULL,
                observed_main_revision TEXT NOT NULL,
                recorded_at TEXT NOT NULL,
                PRIMARY KEY(goal_id, candidate_revision));
            CREATE TABLE IF NOT EXISTS merge_train_pair_suppressions(
                pair_fingerprint TEXT PRIMARY KEY,
                train_id TEXT NOT NULL,
                created_at TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public MergeTrainReceipt SaveGateReceipt(MergeTrainReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var existing = TryReadReceipt(receipt.Identity.Value);
        if (existing is not null)
        {
            return existing;
        }
        if ((receipt.Outcome == MergeTrainGateOutcome.Passed || receipt.ValidForLanding) &&
            (receipt.GateExitCode != 0 || !AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(receipt.GateTestResultPaths)))
        {
            throw new ArgumentException(
                "A passing merge train receipt requires exit code zero and coherent TRX evidence.",
                nameof(receipt));
        }
        receipt = CaptureEvidence(receipt);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO merge_train_receipts(train_id, receipt_id, payload_json)
            VALUES($train, $receipt, $payload);
            """;
        command.Parameters.AddWithValue("$train", receipt.Identity.Value);
        command.Parameters.AddWithValue("$receipt", receipt.ReceiptId);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(ToDto(receipt), JsonOptions));
        command.ExecuteNonQuery();
        return TryReadReceipt(receipt.Identity.Value) ??
            throw new InvalidOperationException("Merge train receipt write did not become readable.");
    }

    public MergeTrainReceipt? TryReadReceipt(string trainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trainId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM merge_train_receipts WHERE train_id=$train;";
        command.Parameters.AddWithValue("$train", trainId);
        var payload = command.ExecuteScalar() as string;
        return payload is null ? null : FromDto(JsonSerializer.Deserialize<ReceiptDto>(payload, JsonOptions)!);
    }

    public IReadOnlyList<MergeTrainReceipt> ReadPassedReceiptsForGoal(GoalId goalId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM merge_train_receipts ORDER BY rowid DESC;";
        using var reader = command.ExecuteReader();
        var receipts = new List<MergeTrainReceipt>();
        while (reader.Read())
        {
            var receipt = FromDto(JsonSerializer.Deserialize<ReceiptDto>(reader.GetString(0), JsonOptions)!);
            if (receipt.HasAuthoritativeLandingEvidence && receipt.Identity.Members.Any(member => member.GoalId == goalId))
            {
                receipts.Add(receipt);
            }
        }

        return receipts
            .OrderByDescending(receipt => receipt.CompletedAt)
            .ThenByDescending(receipt => receipt.ReceiptId, StringComparer.Ordinal)
            .ToArray();
    }

    public void RecordTrainImplicatedCandidate(
        GoalId goalId, string candidateRevision, string trainId, string observedMainRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(trainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedMainRevision);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO merge_train_implicated_candidates
                (goal_id, candidate_revision, train_id, observed_main_revision, recorded_at)
            VALUES($goal, $candidate, $train, $main, $at);
            """;
        command.Parameters.AddWithValue("$goal", goalId.Value);
        command.Parameters.AddWithValue("$candidate", candidateRevision);
        command.Parameters.AddWithValue("$train", trainId);
        command.Parameters.AddWithValue("$main", observedMainRevision);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlySet<string> ReadTrainImplicatedCandidateKeys()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT goal_id, candidate_revision FROM merge_train_implicated_candidates;";
        using var reader = command.ExecuteReader();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) keys.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
        return keys;
    }

    public void SuppressPair(string pairFingerprint, string trainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(trainId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO merge_train_pair_suppressions(pair_fingerprint, train_id, created_at)
            VALUES($pair, $train, $at);
            """;
        command.Parameters.AddWithValue("$pair", pairFingerprint);
        command.Parameters.AddWithValue("$train", trainId);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlySet<string> ReadSuppressedPairs()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pair_fingerprint FROM merge_train_pair_suppressions;";
        using var reader = command.ExecuteReader();
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) pairs.Add(reader.GetString(0));
        return pairs;
    }

    public void RecordEjections(string trainAttemptId, IReadOnlyList<MergeTrainEjection> ejections)
    {
        if (ejections.Count == 0) return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var ejection in ejections)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO merge_train_ejections(
                    train_attempt_id, goal_id, reason, conflict_paths_json, detail, recorded_at)
                VALUES($attempt, $goal, $reason, $paths, $detail, $recorded);
                """;
            command.Parameters.AddWithValue("$attempt", trainAttemptId);
            command.Parameters.AddWithValue("$goal", ejection.GoalId.Value);
            command.Parameters.AddWithValue("$reason", ejection.Reason.ToString());
            command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(ejection.ConflictPaths, JsonOptions));
            command.Parameters.AddWithValue("$detail", ejection.Detail);
            command.Parameters.AddWithValue("$recorded", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void PrepareLanding(MergeTrainReceipt receipt, string commitRevision, string? priorIntegrationRevision)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.HasAuthoritativeLandingEvidence)
        {
            throw new InvalidOperationException("Merge train landing cannot be prepared without authoritative gate evidence.");
        }
        var commit = MergeTrainMemberBinding.NormalizeRevision(commitRevision, nameof(commitRevision));
        var prior = string.IsNullOrWhiteSpace(priorIntegrationRevision)
            ? null
            : MergeTrainMemberBinding.NormalizeRevision(priorIntegrationRevision, nameof(priorIntegrationRevision));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO merge_train_landings(
                train_id, receipt_id, commit_revision, prior_integration_revision, state, updated_at)
            VALUES($train, $receipt, $commit, $prior, 'prepared', $updated)
            ON CONFLICT(train_id) DO UPDATE SET
                receipt_id=excluded.receipt_id,
                commit_revision=excluded.commit_revision,
                prior_integration_revision=excluded.prior_integration_revision,
                state='prepared',
                updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$train", receipt.Identity.Value);
        command.Parameters.AddWithValue("$receipt", receipt.ReceiptId);
        command.Parameters.AddWithValue("$commit", commit);
        command.Parameters.AddWithValue("$prior", (object?)prior ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<MergeTrainCoverage> FinalizeLanding(string trainId, string receiptId)
    {
        var receipt = TryReadReceipt(trainId) ?? throw new InvalidOperationException("Merge train receipt is missing.");
        if (!receipt.ReceiptId.Equals(receiptId, StringComparison.Ordinal) || !receipt.HasAuthoritativeLandingEvidence)
        {
            throw new InvalidOperationException("Merge train landing finalization requires its exact authoritative receipt.");
        }
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM merge_train_landings
            WHERE train_id=$train AND receipt_id=$receipt AND state='prepared';
            """;
        command.Parameters.AddWithValue("$train", trainId);
        command.Parameters.AddWithValue("$receipt", receiptId);
        if (Convert.ToInt32(command.ExecuteScalar()) != 1)
        {
            throw new InvalidOperationException("Merge train prepared landing was not present exactly once.");
        }
        return receipt.Identity.Members
            .Select(member => new MergeTrainCoverage(member.GoalId, trainId, receiptId, Landed: true))
            .ToArray();
    }

    public IReadOnlyList<MergeTrainLandingRecovery> RecoverPreparedLandings(string executionDirectory, string integrationBranch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionDirectory);
        var prepared = new List<(string TrainId, string ReceiptId, string Commit)>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT train_id, receipt_id, commit_revision
                FROM merge_train_landings WHERE state='prepared' ORDER BY train_id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                prepared.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var recovered = new List<MergeTrainLandingRecovery>();
        foreach (var intent in prepared)
        {
            var receipt = TryReadReceipt(intent.TrainId);
            if (receipt is null ||
                !receipt.ReceiptId.Equals(intent.ReceiptId, StringComparison.Ordinal) ||
                !receipt.HasAuthoritativeLandingEvidence ||
                GitCli.Run(
                    executionDirectory,
                    "merge-base", "--is-ancestor", intent.Commit, $"refs/heads/{integrationBranch}").ExitCode != 0)
            {
                continue;
            }

            recovered.Add(new MergeTrainLandingRecovery(
                receipt,
                intent.Commit,
                FinalizeLanding(intent.TrainId, intent.ReceiptId)));
        }
        return recovered;
    }

    public void CompleteLandingEffects(string trainId, string receiptId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE merge_train_landings SET state='finalized', updated_at=$updated
            WHERE train_id=$train AND receipt_id=$receipt AND state='prepared';
            """;
        command.Parameters.AddWithValue("$train", trainId);
        command.Parameters.AddWithValue("$receipt", receiptId);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Merge train landing effects were not finalized exactly once.");
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

    private MergeTrainReceipt CaptureEvidence(MergeTrainReceipt receipt)
    {
        var evidenceDirectory = Path.Combine(
            Path.GetDirectoryName(_databasePath)!,
            "merge-train-evidence",
            receipt.Identity.Value);
        Directory.CreateDirectory(evidenceDirectory);
        var artifacts = new List<AcceptanceCohortEvidenceArtifact>();
        var trxPaths = new List<string>();
        foreach (var source in receipt.GateTestResultPaths.Order(StringComparer.OrdinalIgnoreCase))
        {
            var target = Path.Combine(evidenceDirectory, $"{trxPaths.Count:D2}-{Path.GetFileName(source)}");
            File.Copy(source, target, overwrite: false);
            trxPaths.Add(Path.GetFullPath(target));
            artifacts.Add(Artifact("trx", target));
        }
        var verdictPath = Path.Combine(evidenceDirectory, "gate-verdict.json");
        File.WriteAllText(verdictPath, JsonSerializer.Serialize(new
        {
            receipt.Identity.Value,
            receipt.Outcome,
            receipt.GateExitCode,
            receipt.CompletedAt
        }, JsonOptions));
        artifacts.Add(Artifact("gate-verdict", verdictPath));
        return receipt with { GateTestResultPaths = trxPaths.ToArray(), GateEvidenceArtifacts = artifacts.ToArray() };
    }

    private static AcceptanceCohortEvidenceArtifact Artifact(string kind, string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var stream = File.OpenRead(fullPath);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        return new AcceptanceCohortEvidenceArtifact(kind, fullPath, hash, new FileInfo(fullPath).Length);
    }

    private static ReceiptDto ToDto(MergeTrainReceipt receipt) => new(
        receipt.ReceiptId,
        receipt.Identity.Members.ToArray(),
        receipt.Identity.ObservedMainRevision,
        receipt.Identity.TrainTreeRevision,
        receipt.Identity.ManifestIdentity,
        receipt.Outcome,
        receipt.CompletedAt,
        receipt.GateElapsedMilliseconds,
        receipt.FailedChecks.ToArray(),
        receipt.GateExitCode,
        receipt.GateTestResultPaths.ToArray(),
        receipt.GateEvidenceArtifacts.ToArray(),
        receipt.ValidForLanding);

    private static MergeTrainReceipt FromDto(ReceiptDto dto) => new MergeTrainReceipt(
        dto.ReceiptId,
        MergeTrainIdentity.Create(dto.Members, dto.ObservedMainRevision, dto.TrainTreeRevision, dto.ManifestIdentity),
        dto.Outcome,
        dto.CompletedAt,
        dto.GateElapsedMilliseconds,
        dto.FailedChecks,
        dto.GateExitCode,
        dto.GateTestResultPaths,
        dto.ValidForLanding)
        { GateEvidenceArtifacts = dto.GateEvidenceArtifacts };

    private sealed record ReceiptDto(
        string ReceiptId,
        IReadOnlyList<MergeTrainMemberBinding> Members,
        string ObservedMainRevision,
        string TrainTreeRevision,
        string ManifestIdentity,
        MergeTrainGateOutcome Outcome,
        DateTimeOffset CompletedAt,
        long GateElapsedMilliseconds,
        IReadOnlyList<string> FailedChecks,
        int? GateExitCode,
        IReadOnlyList<string> GateTestResultPaths,
        IReadOnlyList<AcceptanceCohortEvidenceArtifact> GateEvidenceArtifacts,
        bool ValidForLanding);
}
