namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record GoalSnapshotArchiveRecompressionResult(int Rows, long SourceBytes, long StoredBytes);
