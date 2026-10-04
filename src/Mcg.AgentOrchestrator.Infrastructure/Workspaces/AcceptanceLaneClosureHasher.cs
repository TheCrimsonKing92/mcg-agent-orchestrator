using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceLaneClosureHasher
{
    internal static string? TryCompute(string worktreePath, AcceptanceManifestCheck check)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(check.Project))
                return null;

            var root = Path.GetFullPath(worktreePath);
            var project = Path.GetFullPath(Path.Combine(root, check.Project));
            if (!IsUnderRoot(root, project) || !File.Exists(project))
                return null;

            var members = new SortedSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Push(project);
            while (pending.TryPop(out var currentProject))
            {
                if (!File.Exists(currentProject))
                    return null;
                if (!visited.Add(currentProject))
                    continue;

                var directory = Path.GetDirectoryName(currentProject)!;
                members.Add(ToGitPath(root, directory));
                var document = XDocument.Load(currentProject, LoadOptions.None);
                foreach (var reference in document.Descendants().Where(element =>
                             element.Name.LocalName.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase)))
                {
                    var include = reference.Attribute("Include")?.Value;
                    if (string.IsNullOrWhiteSpace(include))
                        return null;
                    var referenced = Path.GetFullPath(Path.Combine(directory, include));
                    if (!IsUnderRoot(root, referenced))
                        return null;
                    pending.Push(referenced);
                }

                foreach (var item in document.Descendants().Where(element =>
                             element.Name.LocalName is "Content" or "None" or "EmbeddedResource" or "Compile"))
                {
                    var include = item.Attribute("Include")?.Value;
                    if (string.IsNullOrWhiteSpace(include))
                        continue;
                    if (include.Contains("$(", StringComparison.Ordinal) ||
                        include.Contains('*') && include.StartsWith("..", StringComparison.Ordinal))
                        return null;
                    if (include.Contains('*'))
                        continue;
                    var included = Path.GetFullPath(Path.Combine(directory, include));
                    if (!IsUnderRoot(root, included))
                        return null;
                    if (!IsUnderRoot(directory, included))
                        members.Add(ToGitPath(root, included));
                }
            }

            foreach (var rootBuildInput in new[] { "Directory.Build.props", "Directory.Build.rsp", "Directory.Packages.props", "global.json" })
            {
                if (File.Exists(Path.Combine(root, rootBuildInput)))
                    members.Add(rootBuildInput);
            }
            foreach (var repositoryDirectory in new[] { "scripts", "docs", "config" })
            {
                if (Directory.Exists(Path.Combine(root, repositoryDirectory)))
                    members.Add(repositoryDirectory);
            }
            foreach (var solution in Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly))
                members.Add(ToGitPath(root, solution));
            if (members.Count == 0 || RunGit(root, ["status", "--porcelain", "--", .. members]) is not { } status || status.Length != 0)
                return null;

            var identities = new List<string>(members.Count);
            foreach (var member in members)
            {
                var identity = RunGit(root, ["rev-parse", $"HEAD:{member}"]);
                if (string.IsNullOrWhiteSpace(identity))
                    return null;
                identities.Add($"{member}:{identity.Trim().ToLowerInvariant()}");
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', identities))))
                .ToLowerInvariant();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return null;
        }
    }

    private static string? RunGit(string root, IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            return null;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(GitCli.DefaultTimeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        Task.WaitAll([output, error]);
        return process.ExitCode == 0 ? output.Result.Trim() : null;
    }

    private static bool IsUnderRoot(string root, string path) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string ToGitPath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
}
