using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record OperatorDirective(
    string Id, string Kind, string Text, string Actor, OperatorActorKind ActorKind,
    string Channel, DateTimeOffset RecordedAt, DateTimeOffset? ExpiresAt,
    bool UntilHandoff, string? RetireReason, string? RetiredBy,
    DateTimeOffset? RetiredAt, bool Expired);
