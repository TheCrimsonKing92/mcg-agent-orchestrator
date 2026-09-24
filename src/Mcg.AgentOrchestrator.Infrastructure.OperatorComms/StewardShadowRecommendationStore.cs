using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IStewardShadowRecommendationStore
{
    Task AppendRecommendationAsync(StewardShadowRecommendation receipt, CancellationToken cancellationToken = default);
    Task AppendAgreementAsync(StewardShadowAgreementRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StewardShadowRecommendation>> ListRecommendationsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StewardShadowAgreementRecord>> ListAgreementsAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryStewardShadowRecommendationStore : IStewardShadowRecommendationStore
{
    private readonly List<StewardShadowRecommendation> _recommendations = [];
    private readonly List<StewardShadowAgreementRecord> _agreements = [];
    public IReadOnlyList<StewardShadowRecommendation> Recommendations => _recommendations;
    public IReadOnlyList<StewardShadowAgreementRecord> Agreements => _agreements;

    public Task AppendRecommendationAsync(StewardShadowRecommendation receipt, CancellationToken cancellationToken = default)
    {
        if (_recommendations.All(x => x.Id != receipt.Id)) _recommendations.Add(receipt);
        return Task.CompletedTask;
    }

    public Task AppendAgreementAsync(StewardShadowAgreementRecord record, CancellationToken cancellationToken = default)
    {
        if (_agreements.All(x => x.RecommendationId != record.RecommendationId)) _agreements.Add(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StewardShadowRecommendation>> ListRecommendationsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StewardShadowRecommendation>>(_recommendations.ToList());

    public Task<IReadOnlyList<StewardShadowAgreementRecord>> ListAgreementsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StewardShadowAgreementRecord>>(_agreements.ToList());
}

public sealed class SqliteStewardShadowRecommendationStore : IStewardShadowRecommendationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dbPath;

    public SqliteStewardShadowRecommendationStore(string dbPath)
    {
        _dbPath = dbPath;
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS shadow_recommendations (
                id TEXT PRIMARY KEY, inputs_hash TEXT NOT NULL, receipt_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS shadow_agreements (
                recommendation_id TEXT PRIMARY KEY, recommendation_inputs_hash TEXT NOT NULL,
                record_json TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public static SqliteStewardShadowRecommendationStore ForDirectory(string orchestratorDirectory) =>
        new(Path.Combine(orchestratorDirectory, "steward-shadow-recommendations.db"));

    public Task AppendRecommendationAsync(StewardShadowRecommendation receipt, CancellationToken cancellationToken = default) =>
        InsertAsync("INSERT OR IGNORE INTO shadow_recommendations (id, inputs_hash, receipt_json) VALUES ($id, $hash, $json)",
            receipt.Id, receipt.InputsHash, JsonSerializer.Serialize(receipt, JsonOptions), cancellationToken);

    public Task AppendAgreementAsync(StewardShadowAgreementRecord record, CancellationToken cancellationToken = default) =>
        InsertAsync("INSERT OR IGNORE INTO shadow_agreements (recommendation_id, recommendation_inputs_hash, record_json) VALUES ($id, $hash, $json)",
            record.RecommendationId, record.RecommendationInputsHash, JsonSerializer.Serialize(record, JsonOptions), cancellationToken);

    public Task<IReadOnlyList<StewardShadowRecommendation>> ListRecommendationsAsync(CancellationToken cancellationToken = default) =>
        ListAsync<StewardShadowRecommendation>("SELECT receipt_json FROM shadow_recommendations ORDER BY id", cancellationToken);

    public Task<IReadOnlyList<StewardShadowAgreementRecord>> ListAgreementsAsync(CancellationToken cancellationToken = default) =>
        ListAsync<StewardShadowAgreementRecord>("SELECT record_json FROM shadow_agreements ORDER BY recommendation_id", cancellationToken);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=30000";
        command.ExecuteNonQuery();
        return connection;
    }

    private async Task InsertAsync(string sql, string id, string hash, string json, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$json", json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<T>> ListAsync<T>(string sql, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), JsonOptions) ??
                throw new InvalidDataException("Invalid Steward shadow record."));
        return results;
    }
}
