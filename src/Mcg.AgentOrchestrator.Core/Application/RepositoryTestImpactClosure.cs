namespace Mcg.AgentOrchestrator.Core;

public static class RepositoryTestImpactClosure
{
    public static int DefaultIndexedSourceFileCap => ReverseDependencyTestImpactReader.MaximumIndexedSourceFiles;

    public static RepositoryTestImpactHeadroom? MeasureIndexedSources(
        string repositoryRoot, int? maximumIndexedSourceFiles = null) =>
        ReverseDependencyTestImpactReader.MeasureIndexedSources(
            repositoryRoot, maximumIndexedSourceFiles ?? DefaultIndexedSourceFileCap);
}
