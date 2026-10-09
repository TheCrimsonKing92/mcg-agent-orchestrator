using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    private const int CandidateGitTimeoutMilliseconds = 10_000;
    private static readonly HashSet<string> CandidateRootInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp", "Directory.Packages.props",
        "global.json", "NuGet.config"
    };

    public static bool TryComputeCandidateIdentity(
        string worktreePath,
        string integrationBranch,
        out CandidateIdentity? identity,
        out string failureReason,
        Func<string, IReadOnlyList<string>, string>? manifestIdentity = null)
    {
        identity = null;
        failureReason = string.Empty;
        if (!Directory.Exists(worktreePath))
            return Fail("missing-worktree", out failureReason);
        try
        {
            if (GitCli.IsWorktreeDirty(worktreePath))
                return Fail("dirty-worktree", out failureReason);
            if (!TryResolveCandidateMergeBase(worktreePath, integrationBranch, out var baseSha))
                return Fail("missing-merge-base", out failureReason);
            var changed = GitCli.Run(worktreePath, CandidateGitTimeoutMilliseconds,
                "diff", "--name-only", "--no-ext-diff", baseSha, "HEAD");
            if (!IsUsable(changed))
                return Fail("changed-files-unavailable", out failureReason);
            var changedFiles = changed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (changedFiles.Length == 0)
                return Fail("empty-change", out failureReason);
            var diff = GitCli.Run(worktreePath, CandidateGitTimeoutMilliseconds,
                "diff", "--binary", "--no-color", "--no-ext-diff", baseSha, "HEAD");
            if (!IsUsable(diff) || string.IsNullOrEmpty(diff.Output))
                return Fail("diff-unavailable", out failureReason);
            var patch = GitCli.RunWithStandardInput(worktreePath, CandidateGitTimeoutMilliseconds,
                diff.Output, "patch-id", "--stable");
            if (!IsUsable(patch))
                return Fail("patch-id-unavailable", out failureReason);
            var patchId = patch.Output.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (patchId is null || patchId.Length != 40 || !patchId.All(Uri.IsHexDigit))
                return Fail("patch-id-malformed", out failureReason);
            if (!TryHashBaseClosure(worktreePath, baseSha, changedFiles, out var closureHash, out failureReason))
                return false;
            var manifest = (manifestIdentity ?? ((path, files) =>
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(path, files)))(
                worktreePath, changedFiles);
            if (string.IsNullOrWhiteSpace(manifest))
                return Fail("manifest-unavailable", out failureReason);
            identity = new CandidateIdentity(patchId, closureHash, manifest);
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"candidate-computation-failed:{ex.GetType().Name}:{ex.Message}", out failureReason);
        }
    }

    internal static bool TryResolveCandidateMergeBase(string worktreePath, string integrationBranch, out string mergeBase)
    {
        var result = GitCli.Run(worktreePath, CandidateGitTimeoutMilliseconds,
            "merge-base", $"refs/heads/{integrationBranch}", "HEAD");
        mergeBase = result.Output.Trim();
        return IsUsable(result) && !string.IsNullOrWhiteSpace(mergeBase);
    }

    private static bool TryHashBaseClosure(
        string worktreePath, string baseSha, IReadOnlyList<string> changedFiles,
        out string hash, out string failureReason)
    {
        hash = string.Empty;
        failureReason = string.Empty;
        var tree = GitCli.Run(worktreePath, CandidateGitTimeoutMilliseconds, "ls-tree", "-r", "--full-tree", baseSha);
        if (!IsUsable(tree))
            return Fail("base-tree-unavailable", out failureReason);
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in tree.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var metadata = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length == 3 && metadata[1] == "blob")
                blobs[line[(tab + 1)..].TrimEnd('\r')] = metadata[2];
        }
        var projects = blobs.Keys.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        var individualFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var changed in changedFiles)
        {
            var owner = projects.Where(project => changed.Equals(project, StringComparison.OrdinalIgnoreCase) ||
                    changed.StartsWith(project[..(project.LastIndexOf('/') + 1)], StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(project => project.Length).FirstOrDefault();
            if (owner is null) individualFiles.Add(changed);
            else pending.Enqueue(owner);
        }
        while (pending.TryDequeue(out var project))
        {
            if (!visited.Add(project)) continue;
            var source = GitCli.Run(worktreePath, CandidateGitTimeoutMilliseconds, "show", $"{baseSha}:{project}");
            if (!IsUsable(source))
                return Fail("project-reference-unavailable", out failureReason);
            var document = XDocument.Parse(source.Output);
            foreach (var reference in document.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)) continue;
                if (include.Contains("$(", StringComparison.Ordinal) || include.Contains('*'))
                    return Fail("project-reference-unresolved", out failureReason);
                var absolute = Path.GetFullPath(Path.Combine(worktreePath,
                    project[..project.LastIndexOf('/')].Replace('/', Path.DirectorySeparatorChar),
                    include.Replace('\\', Path.DirectorySeparatorChar)));
                var relative = Path.GetRelativePath(worktreePath, absolute).Replace('\\', '/');
                if (!projects.Contains(relative))
                    return Fail("project-reference-missing", out failureReason);
                pending.Enqueue(relative);
            }
        }
        var lines = blobs.Where(pair => CandidateRootInputs.Contains(pair.Key) ||
                pair.Key.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                individualFiles.Contains(pair.Key) ||
                visited.Any(project => pair.Key.StartsWith(project[..(project.LastIndexOf('/') + 1)], StringComparison.OrdinalIgnoreCase)))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}\0{pair.Value}\n");
        hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines)))).ToLowerInvariant();
        return true;
    }

    private static bool IsUsable(GitCli.GitResult result) =>
        result.Succeeded && !result.DrainTimedOut && result.ProcessStarted;

    private static bool Fail(string reason, out string failureReason)
    {
        failureReason = reason;
        return false;
    }
}
