using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IStewardTriageReceiptStore
{
    Task AppendAsync(StewardTriageReceipt receipt, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StewardTriageReceipt>> ListAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryStewardTriageReceiptStore : IStewardTriageReceiptStore
{
    private readonly List<StewardTriageReceipt> _receipts = [];

    public IReadOnlyList<StewardTriageReceipt> Receipts => _receipts;

    public Task AppendAsync(StewardTriageReceipt receipt, CancellationToken cancellationToken = default)
    {
        _receipts.Add(receipt);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StewardTriageReceipt>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StewardTriageReceipt>>(_receipts.ToList());
}

public sealed class SqliteStewardTriageReceiptStore : IStewardTriageReceiptStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly string _dbPath;

    public SqliteStewardTriageReceiptStore(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchema();
    }

    public static SqliteStewardTriageReceiptStore ForDirectory(string orchestratorDirectory) =>
        new(Path.Combine(orchestratorDirectory, "steward-triage-receipts.db"));

    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False;";

    public async Task AppendAsync(StewardTriageReceipt receipt, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO steward_triage_receipts (
                id, output_kind, inputs_hash, created_at, input_ids_json, dispositions_json,
                summary, measurement_json
            )
            VALUES (
                $id, $output_kind, $inputs_hash, $created_at, $input_ids_json, $dispositions_json,
                $summary, $measurement_json
            )
            """;
        BindReceipt(cmd, receipt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StewardTriageReceipt>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, output_kind, inputs_hash, created_at, input_ids_json, dispositions_json, summary, measurement_json
            FROM steward_triage_receipts
            ORDER BY id
            """;
        var receipts = new List<StewardTriageReceipt>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            receipts.Add(ReadReceipt(reader));

        return receipts;
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=30000";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private void EnsureSchema()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS steward_triage_receipts (
                id                 TEXT PRIMARY KEY,
                output_kind        TEXT NOT NULL,
                inputs_hash        TEXT NOT NULL,
                created_at         TEXT NOT NULL,
                input_ids_json     TEXT NOT NULL,
                dispositions_json  TEXT NOT NULL,
                summary            TEXT NOT NULL,
                measurement_json   TEXT
            )
            """;
        cmd.ExecuteNonQuery();
    }

    private static void BindReceipt(SqliteCommand cmd, StewardTriageReceipt receipt)
    {
        cmd.Parameters.AddWithValue("$id", receipt.Id);
        cmd.Parameters.AddWithValue("$output_kind", receipt.OutputKind.ToString());
        cmd.Parameters.AddWithValue("$inputs_hash", receipt.InputsHash);
        cmd.Parameters.AddWithValue("$created_at", receipt.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$input_ids_json", JsonSerializer.Serialize(receipt.InputIds, JsonOptions));
        cmd.Parameters.AddWithValue("$dispositions_json", JsonSerializer.Serialize(
            receipt.Dispositions.Select(StoredDisposition.From).ToList(),
            JsonOptions));
        cmd.Parameters.AddWithValue("$summary", receipt.Summary);
        cmd.Parameters.AddWithValue("$measurement_json", receipt.CardLoadMeasurement is null
            ? DBNull.Value
            : JsonSerializer.Serialize(receipt.CardLoadMeasurement, JsonOptions));
    }

    private static StewardTriageReceipt ReadReceipt(SqliteDataReader reader)
    {
        var measurementJson = reader.IsDBNull(7) ? null : reader.GetString(7);
        return new StewardTriageReceipt(
            reader.GetString(0),
            Enum.Parse<StewardOutputKind>(reader.GetString(1), ignoreCase: true),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(4), JsonOptions) ?? [],
            (JsonSerializer.Deserialize<IReadOnlyList<StoredDisposition>>(reader.GetString(5), JsonOptions) ?? [])
                .Select(item => item.ToDomain())
                .ToList(),
            reader.GetString(6),
            string.IsNullOrWhiteSpace(measurementJson)
                ? null
                : JsonSerializer.Deserialize<StewardCardLoadMeasurement>(measurementJson, JsonOptions));
    }

    private sealed record StoredDisposition(
        StewardDispositionKind Kind,
        string InboxItemId,
        string? LinkedCardDedupKey,
        string? LinkedActionReceiptId)
    {
        public static StoredDisposition From(StewardInboxDisposition disposition) =>
            new(
                disposition.Kind,
                disposition.InboxItemId,
                disposition.LinkedCardDedupKey,
                disposition.LinkedActionReceiptId);

        public StewardInboxDisposition ToDomain() => Kind switch
        {
            StewardDispositionKind.RaisedRaw => StewardInboxDisposition.RaisedRaw(InboxItemId, LinkedActionReceiptId ?? string.Empty),
            StewardDispositionKind.CardCreated => StewardInboxDisposition.CardCreated(InboxItemId, LinkedCardDedupKey ?? string.Empty),
            StewardDispositionKind.ReceiptLinkedAction => StewardInboxDisposition.ReceiptLinkedAction(InboxItemId, LinkedActionReceiptId ?? string.Empty),
            _ => throw new InvalidOperationException($"Unknown Steward disposition kind '{Kind}'.")
        };
    }
}
