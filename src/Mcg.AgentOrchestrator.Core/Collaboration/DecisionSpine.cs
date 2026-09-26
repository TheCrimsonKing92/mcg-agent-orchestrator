using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Core;

public enum DecisionRequestKind
{
    General = 1,
    RiskApproval = 2
}

public enum DecisionDefaultDisposition
{
    Deny = 1,
    Park = 2,
    NoAction = 3,
    Approve = 4
}

public enum DecisionReuseScope
{
    ThisOccurrence = 1,
    ThisGoal = 2,
    ThisForkClass = 3,
    ProposePermanentPolicy = 4
}

public enum DecisionRequestPhase
{
    Requested = 1,
    DecisionRecorded = 2,
    EffectApplied = 3
}

public enum AuthorizationTier
{
    Read = 0,
    Answer = 1,
    Mutate = 2,
    AttestLand = 3
}

public enum DecisionActionKind
{
    Clarify = 1,
    Deny = 2,
    Park = 3,
    Retry = 4,
    Recover = 5,
    Unpark = 6,
    Intake = 7,
    VerifyManual = 8,
    RiskyLanding = 9,
    DenylistChange = 10,
    AcceptancePolicyChangeApproved = 11
}

public enum EffectReceiptStatus
{
    Applied = 1,
    Rejected = 2
}

public enum DecisionReversibility
{
    Reversible = 1,
    ReversibleWithCost = 2,
    Irreversible = 3
}

public sealed record DecisionActionRef(string Value);

public sealed record EvidenceManifestEntry(string ReceiptId, string ContentHash);

public sealed record EvidenceManifest(
    IReadOnlyList<EvidenceManifestEntry> Entries,
    string ManifestHash)
{
    public static EvidenceManifest Create(IReadOnlyList<EvidenceManifestEntry> entries) =>
        new(entries, ComputeHash(entries));

    public bool HasExpectedHash() =>
        string.Equals(ManifestHash, ComputeHash(Entries), StringComparison.OrdinalIgnoreCase);

    public static string ComputeHash(IReadOnlyList<EvidenceManifestEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries.OrderBy(entry => entry.ReceiptId, StringComparer.Ordinal))
        {
            builder
                .Append(entry.ReceiptId.Trim())
                .Append('\n')
                .Append(entry.ContentHash.Trim())
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}

public sealed record DecisionBlockingImpact(
    string Description,
    IReadOnlyList<string> BlockedQueues);

public sealed record DecisionAllowedAction(
    DecisionActionRef ActionRef,
    string Label,
    DecisionActionKind Kind,
    AuthorizationTier RequiredTier,
    DateTimeOffset ExpiresAt,
    long? ExpectedGoalStateVersion);

public sealed record DecisionRequest(
    string Id,
    DecisionRequestKind Kind,
    string? GoalId,
    string Subject,
    string RenderedText,
    string TemplateVersion,
    EvidenceManifest EvidenceManifest,
    DateTimeOffset ExpiresAt,
    DecisionDefaultDisposition DefaultDisposition,
    DecisionBlockingImpact BlockingImpact,
    IReadOnlyList<DecisionReuseScope> ReuseScopeOptions,
    IReadOnlyList<DecisionAllowedAction> AllowedActions,
    DateTimeOffset CreatedAt);

public sealed record DecisionResponse(
    DecisionActionRef ActionRef,
    string Value,
    DecisionReuseScope SelectedReuseScope,
    bool PermanentPolicyProposed);

public sealed record DecisionReceipt(
    string Id,
    string RequestId,
    string RenderedText,
    string TemplateVersion,
    IReadOnlyList<EvidenceManifestEntry> EvidenceHashes,
    string EvidenceManifestHash,
    string ActorId,
    string Channel,
    AuthorizationTier AuthenticationAssurance,
    long? ExpectedGoalStateVersion,
    DecisionResponse Response,
    DateTimeOffset RecordedAt,
    EffectReceipt? EffectResult,
    DecisionReversibility? Reversibility = null,
    string? PrecedentRef = null);

public static class OperatorActorIdentity
{
    public static string Format(string actor, OperatorActorKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown operator actor kind.");
        return $"{kind.ToString().ToLowerInvariant()}:{actor.Trim()}";
    }

    public static bool TryParse(string actorId, out string actor, out OperatorActorKind kind)
    {
        actor = string.Empty;
        kind = OperatorActorKind.Human;
        if (string.IsNullOrWhiteSpace(actorId))
            return false;
        var separator = actorId.IndexOf(':');
        if (separator <= 0 || separator == actorId.Length - 1 ||
            !Enum.TryParse(actorId[..separator], ignoreCase: true, out kind) || !Enum.IsDefined(kind))
            return false;
        actor = actorId[(separator + 1)..];
        return !string.IsNullOrWhiteSpace(actor);
    }
}

public sealed record NotificationDelivery(
    string Id,
    string RequestId,
    string Channel,
    string Target,
    string ContentHash,
    DateTimeOffset DeliveredAt);

public sealed record EffectReceipt(
    string Id,
    string RequestId,
    string DecisionReceiptId,
    DecisionActionRef ActionRef,
    EffectReceiptStatus Status,
    long? ExpectedGoalStateVersion,
    long? ActualGoalStateVersion,
    string Result,
    DateTimeOffset RecordedAt,
    string? Outcome = null);

public sealed record DecisionState(
    DecisionRequest Request,
    DecisionReceipt? Receipt,
    EffectReceipt? Effect,
    DecisionRequestPhase Phase)
{
    public static DecisionState Create(DecisionRequest request, DecisionReceipt? receipt, EffectReceipt? effect)
    {
        var phase = effect?.Status == EffectReceiptStatus.Applied
            ? DecisionRequestPhase.EffectApplied
            : receipt is null
                ? DecisionRequestPhase.Requested
                : DecisionRequestPhase.DecisionRecorded;
        return new DecisionState(request, receipt, effect, phase);
    }
}

public static class DecisionAuthorization
{
    public static AuthorizationTier RequiredTierFor(DecisionActionKind kind) => kind switch
    {
        DecisionActionKind.Clarify => AuthorizationTier.Answer,
        DecisionActionKind.Deny => AuthorizationTier.Answer,
        DecisionActionKind.Park => AuthorizationTier.Answer,
        DecisionActionKind.Retry => AuthorizationTier.Mutate,
        DecisionActionKind.Recover => AuthorizationTier.Mutate,
        DecisionActionKind.Unpark => AuthorizationTier.Mutate,
        DecisionActionKind.Intake => AuthorizationTier.Mutate,
        DecisionActionKind.VerifyManual => AuthorizationTier.AttestLand,
        DecisionActionKind.RiskyLanding => AuthorizationTier.AttestLand,
        DecisionActionKind.DenylistChange => AuthorizationTier.AttestLand,
        DecisionActionKind.AcceptancePolicyChangeApproved => AuthorizationTier.AttestLand,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown decision action kind.")
    };

    public static bool Meets(AuthorizationTier actual, AuthorizationTier required) =>
        actual >= required;
}
