namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record IntegrationBranchComparison(
    IntegrationBranchResultKind Kind, string? LearnedBranch, string? RegistryBranch, string Reason);
