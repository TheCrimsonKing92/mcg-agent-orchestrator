namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record BuildPermitClaimant(string Key, BuildPermitClaimantKind Kind, int OwnerPid, bool OwnerBound);
