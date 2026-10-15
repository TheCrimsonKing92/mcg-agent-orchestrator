using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

// Portfolio policy and observations are independent of goal delivery and source-scope completion.
public sealed record EpicOutcomeRequirement(string Id, string Statement, string SourceKind, string Authority);
public sealed record EpicOutcomeDefinition(int SchemaVersion, long ExpectedVersion, string DisplayText,
    IReadOnlyList<EpicOutcomeRequirement> Requirements);
public sealed record EpicOutcomeSource(string Kind, string Locator, string Authority, string Sha256);
public sealed record EpicOutcomeEvidence(int SchemaVersion, long BarVersion, string RequirementId,
    long ExpectedObservationSequence, bool Met, DateTimeOffset ObservedAt, EpicOutcomeSource Source,
    IReadOnlyList<string>? SupersedesReceiptIds = null);
public sealed record EpicOutcomeAttribution(string Actor, OperatorActorKind ActorKind, string Channel,
    AuthorizationTier Authorization);
public sealed record EpicOutcomeRevision(long Version, string? DisplayText,
    IReadOnlyList<EpicOutcomeRequirement> Requirements, string Reason, string Actor, string Channel,
    DateTimeOffset AppliedAt);
public sealed record EpicOutcomeObservation(string ReceiptId, long BarVersion, string RequirementId,
    long Sequence, bool Met, DateTimeOffset ObservedAt, EpicOutcomeSource Source,
    IReadOnlyList<string> SupersedesReceiptIds, string SnapshotBase64, string Actor,
    OperatorActorKind ActorKind, string Channel, DateTimeOffset AppliedAt);
public sealed record EpicOutcomeHistory(IReadOnlyList<EpicOutcomeRevision> Revisions,
    IReadOnlyList<EpicOutcomeObservation> Observations)
{
    public long Version => Revisions.Count == 0 ? 0 : Revisions.Max(revision => revision.Version);
    public EpicOutcomeRevision? Current => Revisions.FirstOrDefault(revision => revision.Version == Version);
    public static EpicOutcomeHistory Empty { get; } = new([], []);
}

public static class EpicOutcomeContract
{
    public const string FileSourceKind = "file-sha256";
    public const int MaximumReceiptBytes = 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static void Validate(EpicOutcomeDefinition definition)
    {
        if (definition is null || definition.SchemaVersion != 1 || definition.ExpectedVersion < 0 ||
            string.IsNullOrWhiteSpace(definition.DisplayText) || definition.Requirements is not { Count: > 0 })
            throw new ArgumentException("outcome-definition-invalid");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in definition.Requirements)
            if (requirement is null || !Identifier(requirement.Id) || !ids.Add(requirement.Id) ||
                string.IsNullOrWhiteSpace(requirement.Statement) || requirement.SourceKind != FileSourceKind ||
                string.IsNullOrWhiteSpace(requirement.Authority))
                throw new ArgumentException("outcome-requirement-invalid");
    }

    public static void Validate(EpicOutcomeEvidence evidence)
    {
        if (evidence is null || evidence.SchemaVersion != 1 || evidence.BarVersion <= 0 || !Identifier(evidence.RequirementId) ||
            evidence.ExpectedObservationSequence < 0 || evidence.ObservedAt == default || evidence.Source is null ||
            evidence.Source.Kind != FileSourceKind || string.IsNullOrWhiteSpace(evidence.Source.Locator) ||
            string.IsNullOrWhiteSpace(evidence.Source.Authority) || !Hash(evidence.Source.Sha256) ||
            (evidence.SupersedesReceiptIds ?? []).Any(id => !Identifier(id)) ||
            (evidence.SupersedesReceiptIds ?? []).Distinct(StringComparer.Ordinal).Count() != (evidence.SupersedesReceiptIds?.Count ?? 0))
            throw new ArgumentException("outcome-evidence-invalid");
    }

    public static void RequireMutation(EpicOutcomeAttribution attribution)
    {
        if (!DecisionAuthorization.Meets(attribution.Authorization, AuthorizationTier.Mutate))
            throw new InvalidOperationException("tier-below-mutate");
        if (string.IsNullOrWhiteSpace(attribution.Actor) || string.IsNullOrWhiteSpace(attribution.Channel) ||
            !Enum.IsDefined(attribution.ActorKind)) throw new ArgumentException("outcome-attribution-invalid");
    }

    private static bool Identifier(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 128 &&
        text.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private static bool Hash(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);
}
