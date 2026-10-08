using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum ExperimentInterventionKind { ConfigFlag = 1, Policy, BriefOrPromptChange, ModelSwap, EvidenceOnlyCodeSpike }
public enum ExperimentBaselineKind { BeforeAfterWindow = 1, AlternatingGates, TwinGoal }
public enum ExperimentStopUnit { Gates = 1, Goals, Ticks }
public enum ExperimentOutcomeState { Open = 1, Confirmed, Refuted, Inconclusive }

public sealed record ExperimentIntervention(ExperimentInterventionKind Kind, string Description);
public sealed record ExperimentBaseline(ExperimentBaselineKind Kind, DateTimeOffset? Since = null,
    DateTimeOffset? Until = null, string? TwinGoalId = null);
public sealed record ExperimentStopRule(int Count, ExperimentStopUnit Unit);
public sealed record ExperimentCondition(string Metric, string Op, [property: JsonRequired] double ChangePercent);
public sealed record ExperimentDecisionRule(IReadOnlyList<ExperimentCondition> KeepIf, IReadOnlyList<ExperimentCondition> RevertIf);
public sealed record ExperimentGuardrail(string Metric, ExperimentCondition BreachIf);
public sealed record ExperimentSpec(string Hypothesis, ExperimentIntervention Intervention, ExperimentBaseline Baseline,
    IReadOnlyList<string> Metrics, ExperimentGuardrail Guardrail, ExperimentStopRule StopRule,
    ExperimentDecisionRule DecisionRule, string? EpicId = null);
public sealed record ExperimentDecision(ExperimentOutcomeState Outcome, string Evidence, string Action, DateTimeOffset DecidedAt);
public sealed record ExperimentRecord(string Id, ExperimentSpec Spec, DateTimeOffset CreatedAt,
    ExperimentOutcomeState Outcome, ExperimentDecision? Decision);

public static class ExperimentMetrics
{
    public static IReadOnlyList<string> Menu { get; } = Array.AsReadOnly(new[]
    {
        "rounds-per-landing", "productive-rounds", "expected-overhead-rounds", "wasted-rounds", "landings-per-hour"
    });
}

/// <summary>A pre-registered experiment has one immutable spec and at most one recorded decision.</summary>
public sealed class ExperimentStore
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();
    private readonly string _connectionString;

    public ExperimentStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 30
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS experiments (
                id TEXT PRIMARY KEY, hypothesis TEXT NOT NULL, epic_id TEXT, created_at TEXT NOT NULL,
                intervention_json TEXT NOT NULL, baseline_json TEXT NOT NULL, metrics_json TEXT NOT NULL,
                guardrail_json TEXT NOT NULL, stop_rule_json TEXT NOT NULL, decision_rule_json TEXT NOT NULL,
                outcome TEXT NOT NULL CHECK(outcome IN ('open','confirmed','refuted','inconclusive')), decision_json TEXT);
            """;
        command.ExecuteNonQuery();
    }

    public async Task<ExperimentRecord> AddAsync(ExperimentSpec spec, CancellationToken cancellationToken = default)
    {
        var record = new ExperimentRecord(Guid.NewGuid().ToString("n"), spec, DateTimeOffset.UtcNow,
            ExperimentOutcomeState.Open, null);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO experiments VALUES ($id,$hypothesis,$epic,$created,$intervention,$baseline,$metrics,
                $guardrail,$stop,$rule,'open',NULL);
            """;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$hypothesis", spec.Hypothesis);
        command.Parameters.AddWithValue("$epic", (object?)spec.EpicId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", record.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$intervention", JsonSerializer.Serialize(spec.Intervention, JsonOptions));
        command.Parameters.AddWithValue("$baseline", JsonSerializer.Serialize(spec.Baseline, JsonOptions));
        command.Parameters.AddWithValue("$metrics", JsonSerializer.Serialize(spec.Metrics, JsonOptions));
        command.Parameters.AddWithValue("$guardrail", JsonSerializer.Serialize(spec.Guardrail, JsonOptions));
        command.Parameters.AddWithValue("$stop", JsonSerializer.Serialize(spec.StopRule, JsonOptions));
        command.Parameters.AddWithValue("$rule", JsonSerializer.Serialize(spec.DecisionRule, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return record;
    }

    public async Task<ExperimentRecord?> ResolveAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference)) throw new ArgumentException("experiment: reference is required.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        // substr is a literal prefix comparison: '%' and '_' never become wildcards.
        command.CommandText = "SELECT * FROM experiments WHERE id=$ref OR substr(id,1,length($ref))=$ref LIMIT 2";
        command.Parameters.AddWithValue("$ref", reference.ToLowerInvariant());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var record = Read(reader);
        if (await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException($"Experiment reference '{reference}' is ambiguous.");
        return record;
    }

    public async Task<IReadOnlyList<ExperimentRecord>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM experiments ORDER BY created_at, id";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<ExperimentRecord>();
        while (await reader.ReadAsync(cancellationToken)) records.Add(Read(reader));
        return records;
    }

    public async Task DecideAsync(string id, ExperimentOutcomeState outcome, string evidence, string action,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(outcome) || outcome == ExperimentOutcomeState.Open)
            throw new ArgumentException("outcome: expected confirmed, refuted or inconclusive.");
        if (string.IsNullOrWhiteSpace(evidence)) throw new ArgumentException("evidence: reference is required.");
        if (string.IsNullOrWhiteSpace(action)) throw new ArgumentException("action: text is required.");
        var decision = new ExperimentDecision(outcome, evidence, action, DateTimeOffset.UtcNow);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE experiments SET outcome=$outcome, decision_json=$decision WHERE id=$id AND outcome='open'";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$outcome", JsonNamingPolicy.KebabCaseLower.ConvertName(outcome.ToString()));
        command.Parameters.AddWithValue("$decision", JsonSerializer.Serialize(decision, JsonOptions));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"Experiment '{id}' is already decided or does not exist.");
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM experiments";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static ExperimentRecord Read(SqliteDataReader reader)
    {
        T Json<T>(int index) => JsonSerializer.Deserialize<T>(reader.GetString(index), JsonOptions)
            ?? throw new InvalidDataException($"Experiment '{reader.GetString(0)}' has missing stored data at column {index}.");
        var spec = new ExperimentSpec(reader.GetString(1), Json<ExperimentIntervention>(4), Json<ExperimentBaseline>(5),
            Json<string[]>(6), Json<ExperimentGuardrail>(7), Json<ExperimentStopRule>(8), Json<ExperimentDecisionRule>(9),
            reader.IsDBNull(2) ? null : reader.GetString(2));
        var outcome = JsonSerializer.Deserialize<ExperimentOutcomeState>($"\"{reader.GetString(10)}\"", JsonOptions);
        return new(reader.GetString(0), spec, DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
            outcome, reader.IsDBNull(11) ? null : Json<ExperimentDecision>(11));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }
}
