namespace Mcg.AgentOrchestrator.Core;

public static class ForeignTargetTestImpactPlanner
{
    public static RepositoryTestImpactPlan Plan(IEnumerable<string> paths, string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (!Path.IsPathFullyQualified(repositoryRoot))
        {
            throw new ArgumentException(
                "The repository root must be an absolute path.",
                nameof(repositoryRoot));
        }

        var summary = RepositoryChangeClassifier.Classify(paths);
        ITestClassDeclarationReader declarationReader = ReverseDependencyTestImpactReader
            .IsDeliberatelyPartialRepositoryRoot(repositoryRoot)
                ? UnavailableTestClassDeclarationReader.Instance
                : new FileSystemTestClassDeclarationReader(repositoryRoot);
        return RepositoryTestImpactPlanner.Plan(
            summary,
            declarationReader,
            new ForeignTree(CandidateTreeProbe.ForRepositoryRoot(repositoryRoot)));
    }

    private sealed class ForeignTree(ICandidateTreeProbe inner) : ICandidateTreeProbe
    {
        public bool Exists(string path) =>
            !RepositoryTestImpactPlanner.BuiltInTestProjectPaths.Contains(path, StringComparer.OrdinalIgnoreCase) &&
            inner.Exists(path);
    }
}
