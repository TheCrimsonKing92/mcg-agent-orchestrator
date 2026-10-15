namespace Mcg.AgentOrchestrator.Core;

public sealed record DirectiveRecordOperatorIntentPayload(
    string Kind, string Text, string? ExpiresAt = null, bool UntilHandoff = false);

public sealed record DirectiveRetireOperatorIntentPayload(string DirectiveId, string Reason);

public sealed record DirectiveHandoffOperatorIntentPayload(string? Note = null);
