namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record ExperimentStopExtension(int FromCount, int NewCount, string Reason, DateTimeOffset ExtendedAt);
