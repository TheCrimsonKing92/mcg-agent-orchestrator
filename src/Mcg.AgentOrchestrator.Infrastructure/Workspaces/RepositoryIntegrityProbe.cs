namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class RepositoryIntegrityProbe(string repositoryRoot, IWorkerIntegrityLabeler labeler)
    : IRepositoryIntegrityProbe
{
    public RepositoryIntegrityReading Read()
    {
        try
        {
            var paths = new List<string> { "." };
            if (Directory.Exists(Path.Combine(repositoryRoot, ".git")))
            {
                paths.Add(".git/hooks");
                paths.Add(".git/refs");
            }

            var lowPaths = new List<string>();
            foreach (var relativePath in paths)
            {
                var reading = labeler.Query(relativePath == "." ? repositoryRoot : Path.Combine(repositoryRoot, relativePath));
                if (reading.NativeQueryError is { } error)
                    return RepositoryIntegrityReading.Unavailable($"integrity-query-error:{error}");
                if (reading.Low) lowPaths.Add(relativePath);
            }
            lowPaths.Sort(StringComparer.Ordinal);
            return RepositoryIntegrityReading.Available(lowPaths);
        }
        catch (Exception exception)
        {
            return RepositoryIntegrityReading.Unavailable($"repository-integrity-error:{exception.GetType().Name}");
        }
    }
}
