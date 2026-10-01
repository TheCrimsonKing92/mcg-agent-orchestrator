namespace Mcg.AgentOrchestrator.Core;

/// <summary>Answers whether a repository-relative path is present in the candidate being planned.</summary>
public interface ICandidateTreeProbe
{
    bool Exists(string path);
}

public static class CandidateTreeProbe
{
    public static ICandidateTreeProbe AssumeAllPresent { get; } = new PresentTree();

    public static ICandidateTreeProbe ForRepositoryRoot(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (!Path.IsPathFullyQualified(repositoryRoot))
            throw new ArgumentException("The repository root must be an absolute path.", nameof(repositoryRoot));

        // Partial fixture roots cannot establish that a file was removed from a candidate.
        return (File.Exists(Path.Combine(repositoryRoot, ".git")) ||
                Directory.Exists(Path.Combine(repositoryRoot, ".git"))) &&
            File.Exists(Path.Combine(repositoryRoot, "Mcg.AgentOrchestrator.sln"))
                ? new FileSystemTree(repositoryRoot)
                : AssumeAllPresent;
    }

    private sealed class PresentTree : ICandidateTreeProbe
    {
        public bool Exists(string path) => true;
    }

    private sealed class FileSystemTree(string root) : ICandidateTreeProbe
    {
        public bool Exists(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':'))
                return true;

            var segments = path.Replace('\\', '/').Split('/');
            if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
                return true;

            try
            {
                File.GetAttributes(Path.Combine(root, Path.Combine(segments)));
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            // Unreadable or malformed evidence does not prove absence.
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
            catch (ArgumentException) { return true; }
            catch (NotSupportedException) { return true; }
        }
    }
}
