namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Sections requested by one attention projection read; existing callers include gate evidence.</summary>
public sealed record AttentionProjectionReadOptions(bool IncludeGateEvidence = true);
