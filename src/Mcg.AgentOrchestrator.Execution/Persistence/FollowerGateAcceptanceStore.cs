using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class FollowerGateAcceptanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _databasePath;
    private readonly bool _readOnly;

    public FollowerGateAcceptanceStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        FollowerGateAcceptanceStoreSetup.Setup(_databasePath);
    }

    private FollowerGateAcceptanceStore(string dbPath, bool readOnly)
    {
        _databasePath = dbPath;
        _readOnly = readOnly;
    }

    public static FollowerGateAcceptanceStore OpenReadOnly(string dbPath)
    {
        if (!File.Exists(dbPath))
            throw SchemaSetupRequired(dbPath, StoreSchemaState.Missing);

        var store = new FollowerGateAcceptanceStore(dbPath, readOnly: true);
        using var connection = store.Open();
        var state = StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.FollowerGateAcceptance);
        if (state != StoreSchemaState.Current)
            throw SchemaSetupRequired(dbPath, state);
        return store;
    }

    private static InvalidOperationException SchemaSetupRequired(string dbPath, StoreSchemaState state) =>
        new($"Follower gate acceptance store '{dbPath}' schema is {state} (expected version {StoreSchemaRegistry.FollowerGateAcceptance.CurrentVersion}); run setup.");

    public FollowerGateRunReceipt SaveGateReceipt(FollowerGateRunReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (_readOnly)
            throw new InvalidOperationException("SaveGateReceipt is a writer operation unavailable on a read-only follower gate acceptance store.");
        var existing = TryReadReceipt(receipt.IdentityValue);
        if (existing is not null) return existing;
        if (receipt.Outcome == FollowerGateRunOutcome.Passed &&
            (receipt.GateExitCode != 0 || !AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(receipt.GateTestResultPaths)))
            throw new ArgumentException("A passing follower gate receipt requires exit code zero and coherent TRX evidence.", nameof(receipt));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO follower_gate_receipts
                (identity_value, receipt_id, leader_goal_id, follower_goal_id, payload_json)
            VALUES($identity, $receipt, $leader, $follower, $payload);
            """;
        command.Parameters.AddWithValue("$identity", receipt.IdentityValue);
        command.Parameters.AddWithValue("$receipt", receipt.ReceiptId);
        command.Parameters.AddWithValue("$leader", receipt.Binding.LeaderGoalId.Value);
        command.Parameters.AddWithValue("$follower", receipt.Binding.FollowerGoalId.Value);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(receipt, JsonOptions));
        command.ExecuteNonQuery();
        return TryReadReceipt(receipt.IdentityValue) ??
            throw new InvalidOperationException("Follower gate receipt write did not become readable.");
    }

    public FollowerGateRunReceipt? TryReadReceipt(string identityValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityValue);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM follower_gate_receipts WHERE identity_value=$identity;";
        command.Parameters.AddWithValue("$identity", identityValue);
        var payload = command.ExecuteScalar() as string;
        return payload is null ? null : Deserialize(payload);
    }

    public IReadOnlyList<FollowerGateRunReceipt> ReadReceiptsForFollower(GoalId follower)
    {
        ArgumentNullException.ThrowIfNull(follower);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM follower_gate_receipts WHERE follower_goal_id=$follower;";
        command.Parameters.AddWithValue("$follower", follower.Value);
        using var reader = command.ExecuteReader();
        var receipts = new List<FollowerGateRunReceipt>();
        while (reader.Read()) receipts.Add(Deserialize(reader.GetString(0)));
        return receipts.OrderByDescending(receipt => receipt.CompletedAt)
            .ThenByDescending(receipt => receipt.ReceiptId, StringComparer.Ordinal).ToArray();
    }

    private static FollowerGateRunReceipt Deserialize(string payload) =>
        JsonSerializer.Deserialize<FollowerGateRunReceipt>(payload, JsonOptions) ??
        throw new InvalidDataException("Follower gate receipt payload is empty.");

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_readOnly ? new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ConnectionString : new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared, Pooling = false
        }.ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }
}
