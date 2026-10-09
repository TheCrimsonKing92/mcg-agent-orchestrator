using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IAuthorBriefDraftRepository
{
    string ResolveMainHead();
    (string? MainHead, AuthorDraftTrackedEdits? TrackedEdits) ResolveMainHeadOrTrackedEdits() => (ResolveMainHead(), null);
    int? TrackedLineCount(string sha, string path);
    bool IsTrackedDirectory(string sha, string path) => false;
}

internal sealed class GitAuthorBriefDraftRepository(string repositoryRoot, string integrationBranch) : IAuthorBriefDraftRepository
{
    public string ResolveMainHead()
    {
        var result = ResolveMainHeadOrTrackedEdits();
        if (result.TrackedEdits is not null)
            throw new InvalidOperationException("Author drafting requires no tracked edits against main HEAD.");
        return result.MainHead!;
    }

    public (string? MainHead, AuthorDraftTrackedEdits? TrackedEdits) ResolveMainHeadOrTrackedEdits()
    {
        var main = Require(GitCli.Run(repositoryRoot, "rev-parse", "--verify", $"{integrationBranch}^{{commit}}")).Trim();
        var head = Require(GitCli.Run(repositoryRoot, "rev-parse", "--verify", "HEAD^{commit}")).Trim();
        var root = Require(GitCli.Run(repositoryRoot, "rev-parse", "--show-toplevel")).Trim();
        var wrongRoot = !Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)), StringComparison.OrdinalIgnoreCase);
        if (wrongRoot || head != main)
            throw new AuthorDraftRepositoryNotAtMainException(
                wrongRoot ? head != main ? "head-not-main, toplevel-not-root" : "toplevel-not-root" : "head-not-main",
                head, main, root, repositoryRoot);
        var tracked = GitTrackedEditsProbe.Probe(repositoryRoot);
        if (tracked.Edits is not null) return (main, tracked.Edits);
        if (!tracked.Clean) throw new InvalidOperationException("Author repository inspection failed: tracked edits unresolved.");
        return (main, null);
    }

    public int? TrackedLineCount(string sha, string path)
    {
        // A citation is a repository-relative file, never a git revision expression or directory.
        if (!IsRepositoryPath(path)) return null;
        var type = GitCli.Run(repositoryRoot, "cat-file", "-t", $"{sha}:{path}");
        if (!type.ProcessStarted || type.DrainTimedOut)
            throw new InvalidOperationException($"Cannot inspect citation {path}: {type.Error}");
        if (!type.Succeeded || type.Output.Trim() != "blob") return null;
        var content = Require(GitCli.Run(repositoryRoot, "show", $"{sha}:{path}"));
        using var reader = new StringReader(content);
        var lines = 0;
        while (reader.ReadLine() is not null) lines++;
        return lines;
    }

    public bool IsTrackedDirectory(string sha, string path)
    {
        if (!IsRepositoryPath(path)) return false;
        var type = GitCli.Run(repositoryRoot, "cat-file", "-t", $"{sha}:{path}");
        if (!type.ProcessStarted || type.DrainTimedOut)
            throw new InvalidOperationException($"Cannot inspect citation {path}: {type.Error}");
        return type.Succeeded && type.Output.Trim() == "tree";
    }

    private static bool IsRepositoryPath(string path) =>
        !Path.IsPathRooted(path) && !path.Contains(':') && !path.Contains('\\') &&
        !path.Split('/').Any(segment => segment is "" or "." or "..");

    private static string Require(GitCli.GitResult result)
    {
        if (!result.Succeeded || result.DrainTimedOut)
            throw new InvalidOperationException($"Author repository inspection failed: {result.Error}");
        return result.Output;
    }
}
