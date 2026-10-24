using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Snapshots tracked paths at a work tree top level, otherwise retaining filesystem discovery.</summary>
internal sealed class DiscoveryTrackedPaths
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly string root;
    private readonly HashSet<string>? files;
    private readonly HashSet<string>? directories;
    private readonly string? failureReason;

    private DiscoveryTrackedPaths(string root, HashSet<string>? files = null,
        HashSet<string>? directories = null, string? failureReason = null)
    {
        this.root = root;
        this.files = files;
        this.directories = directories;
        this.failureReason = failureReason;
    }

    public static DiscoveryTrackedPaths Read(string root) =>
        Read(root, (directory, arguments) => GitCli.Run(directory, arguments));

    internal static DiscoveryTrackedPaths Read(string root, Func<string, string[], GitCli.GitResult> git)
    {
        var normalizedRoot = NormalizeRoot(root);
        var topLevel = git(root, ["rev-parse", "--show-toplevel"]);
        if (!topLevel.ProcessStarted || !topLevel.Succeeded || topLevel.DrainTimedOut)
            return new(normalizedRoot);
        var output = topLevel.Output.TrimEnd('\r', '\n');
        if (output.Length == 0)
            return new(normalizedRoot);
        try
        {
            if (!PathComparer.Equals(normalizedRoot, NormalizeRoot(output)))
                return new(normalizedRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(normalizedRoot);
        }

        var listing = git(root, ["ls-files", "-z", "--cached", "--recurse-submodules"]);
        if (!listing.ProcessStarted || !listing.Succeeded || listing.DrainTimedOut)
        {
            var firstError = listing.Error.Split(['\r', '\n'], 2)[0];
            if (firstError.Length == 0)
                firstError = "stderr was empty";
            var startFailure = listing.ProcessStarted ? "" : ", process did not start";
            var drainFailure = listing.DrainTimedOut ? ", output drain timed out" : "";
            return new(normalizedRoot, failureReason:
                $"Tracked files could not be listed (git ls-files exit {listing.ExitCode}{startFailure}{drainFailure}: {firstError}); untracked directories were walked.");
        }

        var files = new HashSet<string>(PathComparer);
        var directories = new HashSet<string>(PathComparer) { "." };
        foreach (var entry in listing.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = entry.Replace('\\', '/');
            files.Add(path);
            for (var separator = path.LastIndexOf('/'); separator > 0;
                 separator = path.LastIndexOf('/', separator - 1))
                directories.Add(path[..separator]);
        }
        return new(normalizedRoot, files, directories);
    }

    public bool IncludesFile(string fullPath) => files is null || files.Contains(Relative(fullPath));

    public bool IncludesDirectory(string fullPath) =>
        directories is null || directories.Contains(Relative(fullPath));

    public void AskIfDegraded(List<ProjectOwnerQuestion> questions)
    {
        if (failureReason is not null)
            ProjectFactHelpers.Ask("discovery/tracked-files", failureReason, new FactSource(".git", 1), questions);
    }

    private string Relative(string fullPath) => Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private static string NormalizeRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootLength = Path.GetPathRoot(fullPath)!.Length;
        while (fullPath.Length > rootLength && Path.EndsInDirectorySeparator(fullPath))
            fullPath = Path.TrimEndingDirectorySeparator(fullPath);
        return fullPath;
    }
}
