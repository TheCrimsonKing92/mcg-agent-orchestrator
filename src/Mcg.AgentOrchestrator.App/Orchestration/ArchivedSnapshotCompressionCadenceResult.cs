namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ArchivedSnapshotCompressionCadenceResult(
    int Rows, long SourceBytes, long StoredBytes, int Batches, string Status, string? ExceptionType);
