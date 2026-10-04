namespace Mcg.AgentOrchestrator.Core;

public sealed record DispatchShadowDecision(
    DispatchTaskClass TaskClass,
    string? ShadowProviderName,
    string? ShadowModelName,
    string? ShadowReasoningEffort,
    string PolicyVersion,
    bool Differs);
