namespace Mcg.AgentOrchestrator.Core;

/// <summary>Conductor-captured immutable scope inputs for one tracked worker dispatch, never compile evidence.</summary>
public sealed record WorkerBuildScopeIdentity(string GoalBaseline, string MetadataDigest);
