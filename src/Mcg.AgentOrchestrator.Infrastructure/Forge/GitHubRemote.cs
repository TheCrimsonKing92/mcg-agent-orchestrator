using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GitHubRemote
{
    internal static bool TryParse(string remote, out string repository)
    {
        repository = "";
        string path;
        if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            path = remote["git@github.com:".Length..];
        else if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) &&
                 uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                 uri.Scheme is "https" or "ssh" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment))
            path = uri.AbsolutePath.TrimStart('/');
        else return false;

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        if (!Regex.IsMatch(path, @"\A[A-Za-z0-9-]+/[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant) ||
            path.Split('/').Any(segment => segment is "." or "..")) return false;
        repository = path;
        return true;
    }
}
