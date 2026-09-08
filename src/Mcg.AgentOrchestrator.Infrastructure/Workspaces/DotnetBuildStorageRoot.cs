namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// A fixed, absolute storage namespace for one build or cleanup operation.
/// Constructing this value does not create directories or acquire execution capacity.
/// </summary>
public sealed record DotnetBuildStorageRoot
{
    public DotnetBuildStorageRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("Build storage root must be an absolute path.", nameof(rootPath));
        }

        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    public string RootPath { get; }
}
