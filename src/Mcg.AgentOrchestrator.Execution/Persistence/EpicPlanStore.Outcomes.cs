using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class EpicPlanStore
{
    // True denotes recovery of an exact previously applied request, not a new mutation.
    public async Task<bool> DefineOutcomeAsync(string epicId, EpicOutcomeDefinition definition,
        string requestId, EpicOutcomeAttribution attribution)
    {
        EpicOutcomeContract.Validate(definition);
        EpicOutcomeContract.RequireMutation(attribution);
        var hash = RequestHash(epicId, "define", definition, attribution, requestId);
        var replayed = false;
        await WriteAsync(epicId, connection =>
        {
            if (WasApplied(connection, epicId, requestId, hash)) { replayed = true; return; }
            var history = ReadOutcome(connection, epicId);
            if (history.Version != definition.ExpectedVersion) throw new InvalidOperationException("outcome-stale-version");
            AppendRevision(connection, epicId, new(checked(history.Version + 1), definition.DisplayText,
                definition.Requirements, "defined", attribution.Actor, attribution.Channel, DateTimeOffset.UtcNow));
            Remember(connection, epicId, requestId, hash);
        }).ConfigureAwait(false);
        return replayed;
    }

    public async Task<bool> RecordOutcomeAsync(string epicId, EpicOutcomeEvidence evidence,
        string requestId, EpicOutcomeAttribution attribution)
    {
        EpicOutcomeContract.Validate(evidence);
        EpicOutcomeContract.RequireMutation(attribution);
        var hash = RequestHash(epicId, "record", evidence, attribution, requestId);
        var replayed = false;
        await WriteAsync(epicId, connection =>
        {
            if (WasApplied(connection, epicId, requestId, hash)) { replayed = true; return; }
            var history = ReadOutcome(connection, epicId);
            if (history.Version != evidence.BarVersion) throw new InvalidOperationException("outcome-stale-version");
            var requirement = history.Current?.Requirements.SingleOrDefault(requirement => requirement.Id == evidence.RequirementId)
                ?? throw new InvalidOperationException("outcome-requirement-not-found");
            if (requirement.SourceKind != evidence.Source.Kind || requirement.Authority != evidence.Source.Authority)
                throw new InvalidOperationException("outcome-ambiguous-provenance");
            var previous = history.Observations.Where(observation => observation.BarVersion == evidence.BarVersion &&
                observation.RequirementId == evidence.RequirementId).ToArray();
            var sequence = previous.Length == 0 ? 0 : previous.Max(observation => observation.Sequence);
            if (evidence.ExpectedObservationSequence != sequence) throw new InvalidOperationException("outcome-stale-observation-sequence");
            var superseded = previous.SelectMany(observation => observation.SupersedesReceiptIds).ToHashSet(StringComparer.Ordinal);
            var active = previous.Where(observation => !superseded.Contains(observation.ReceiptId))
                .Select(observation => observation.ReceiptId).ToHashSet(StringComparer.Ordinal);
            if ((evidence.SupersedesReceiptIds ?? []).Any(id => !active.Contains(id)))
                throw new InvalidOperationException("outcome-supersession-invalid");
            // File accessibility and identity are revalidated under the portfolio write transaction.
            var snapshot = EpicOutcomeReceipts.Read(evidence.Source);
            var observation = new EpicOutcomeObservation(requestId, evidence.BarVersion, evidence.RequirementId,
                checked(sequence + 1), evidence.Met, evidence.ObservedAt, evidence.Source,
                evidence.SupersedesReceiptIds ?? [], Convert.ToBase64String(snapshot), attribution.Actor,
                attribution.ActorKind, attribution.Channel, DateTimeOffset.UtcNow);
            Execute(connection, """
                INSERT INTO epic_outcome_observations
                (epic_id, bar_version, requirement_id, sequence, receipt_id, payload_json)
                VALUES ($epic, $version, $requirement, $sequence, $receipt, $payload)
                """, epicId, ("$version", observation.BarVersion), ("$requirement", observation.RequirementId),
                ("$sequence", observation.Sequence), ("$receipt", observation.ReceiptId),
                ("$payload", JsonSerializer.Serialize(observation, EpicOutcomeContract.JsonOptions)));
            Remember(connection, epicId, requestId, hash);
        }).ConfigureAwait(false);
        return replayed;
    }

    private static EpicOutcomeHistory ReadOutcome(SqliteConnection connection, string epicId) => new(
        ReadOutcomeRows<EpicOutcomeRevision>(connection, epicId,
            "SELECT payload_json FROM epic_outcome_revisions WHERE epic_id = $epic ORDER BY version"),
        ReadOutcomeRows<EpicOutcomeObservation>(connection, epicId,
            "SELECT payload_json FROM epic_outcome_observations WHERE epic_id = $epic ORDER BY bar_version, requirement_id, sequence"));

    private static IReadOnlyList<T> ReadOutcomeRows<T>(SqliteConnection connection, string epicId, string sql)
    {
        using var command = Command(connection, sql, epicId);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), EpicOutcomeContract.JsonOptions)
            ?? throw new InvalidOperationException("outcome-history-invalid"));
        return rows;
    }

    private static void AppendRevision(SqliteConnection connection, string epicId, EpicOutcomeRevision revision) =>
        Execute(connection, "INSERT INTO epic_outcome_revisions (epic_id, version, payload_json) VALUES ($epic, $version, $payload)",
            epicId, ("$version", revision.Version), ("$payload", JsonSerializer.Serialize(revision, EpicOutcomeContract.JsonOptions)));

    private static string RequestHash<T>(string epicId, string verb, T payload, EpicOutcomeAttribution attribution, string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128 ||
            !requestId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            throw new ArgumentException("outcome-request-id-invalid");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { epicId, verb, payload, attribution }, EpicOutcomeContract.JsonOptions))));
    }

    private static bool WasApplied(SqliteConnection connection, string epicId, string requestId, string hash)
    {
        using var command = Command(connection,
            "SELECT epic_id, request_hash FROM epic_outcome_applied_requests WHERE request_id = $request", epicId, ("$request", requestId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return false;
        if (reader.GetString(0) != epicId || reader.GetString(1) != hash)
            throw new InvalidOperationException("outcome-request-payload-mismatch");
        return true;
    }

    private static void Remember(SqliteConnection connection, string epicId, string requestId, string hash) =>
        Execute(connection, "INSERT INTO epic_outcome_applied_requests (request_id, epic_id, request_hash) VALUES ($request, $epic, $hash)",
            epicId, ("$request", requestId), ("$hash", hash));
}
