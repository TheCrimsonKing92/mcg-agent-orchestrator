namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record MergeTrainReproductionProvenance(
    string MainRevision, string MemberRevision, string ExpectedTreeRevision, string TreeRevision,
    string PartitionBuildOutputRoot, IReadOnlyList<string> BuildOutputRoots,
    IReadOnlyList<string> AssemblyPaths, string? ValidationFailure = null);
