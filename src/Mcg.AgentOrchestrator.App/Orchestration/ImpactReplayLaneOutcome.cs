namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ImpactReplayLaneOutcome(string Lane, string Outcome, IReadOnlyList<string> TrxPaths);
