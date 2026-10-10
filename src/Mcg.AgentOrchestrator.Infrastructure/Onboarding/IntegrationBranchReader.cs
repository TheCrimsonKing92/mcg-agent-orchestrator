using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Reads loose symbolic references without invoking version-control tools.</summary>
public static class IntegrationBranchReader
{
    public static LearnedIntegrationBranch Read(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var metadata = Path.Combine(repositoryRoot, ".git");
        try
        {
            if ((File.GetAttributes(metadata) & FileAttributes.Directory) == 0)
                return LearnedIntegrationBranch.Unlearned("The metadata entry is a pointer file rather than a directory.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return LearnedIntegrationBranch.Unlearned("The metadata directory is missing.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return LearnedIntegrationBranch.Unlearned("The metadata directory is unreadable.");
        }

        const string remoteSource = ".git/refs/remotes/origin/HEAD";
        try
        {
            var line = ReadFirstLine(Path.Combine(repositoryRoot, remoteSource));
            return ParseBranch(line, "ref: refs/remotes/origin/", remoteSource, FactConfidence.High)
                ?? LearnedIntegrationBranch.Unlearned("The remote default reference is malformed.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Only an absent remote reference permits the weaker checked-out branch hint.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return LearnedIntegrationBranch.Unlearned("The remote default reference is unreadable.");
        }

        const string headSource = ".git/HEAD";
        try
        {
            var line = ReadFirstLine(Path.Combine(repositoryRoot, headSource));
            var learned = ParseBranch(line, "ref: refs/heads/", headSource, FactConfidence.Medium);
            if (learned is not null) return learned;
            return LearnedIntegrationBranch.Unlearned(
                line.Length is 40 or 64 && line.All(Uri.IsHexDigit)
                    ? "HEAD is detached and holds a commit id."
                    : "The metadata HEAD reference is malformed.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return LearnedIntegrationBranch.Unlearned("The metadata HEAD reference is missing.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return LearnedIntegrationBranch.Unlearned("The metadata HEAD reference is unreadable.");
        }
    }

    private static string ReadFirstLine(string path)
    {
        using var reader = new StreamReader(path);
        return (reader.ReadLine() ?? "").TrimEnd();
    }

    private static LearnedIntegrationBranch? ParseBranch(string line, string prefix, string source,
        FactConfidence confidence)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var branch = line[prefix.Length..];
        if (branch.Length == 0 || branch == "@" ||
            branch.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || "~^:?*[\\".Contains(character)) ||
            branch.EndsWith('.') || branch.Contains("..", StringComparison.Ordinal) ||
            branch.Contains("@{", StringComparison.Ordinal) ||
            branch.Split('/').Any(segment => segment.Length == 0 || segment.StartsWith('.') || segment.EndsWith(".lock", StringComparison.Ordinal)))
            return null;
        return LearnedIntegrationBranch.Learned(new ProjectFact<string>(branch, new FactSource(source, 1), confidence),
            confidence == FactConfidence.High
                ? "Learned from the remote default reference."
                : "Learned from the checked-out branch hint.");
    }
}
