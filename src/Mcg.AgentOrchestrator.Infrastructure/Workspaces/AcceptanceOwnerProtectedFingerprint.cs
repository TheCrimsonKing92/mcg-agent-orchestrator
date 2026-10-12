using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceOwnerProtectedFingerprint
{
    internal static IReadOnlyList<string> SelectPolicyPaths(
        string worktreePath, IReadOnlyList<string> changedFiles,
        Func<string, string[], string?> resolveGitText, string candidateRef, string integrationBranch)
    {
        const string manifestPath = "config/acceptance-manifest.json";
        var noRenamePaths = changedFiles.Any(path =>
            !RepositoryChangeClassifier.IsOwnerProtectedPolicyPath(path) &&
            !NormalizePath(path).Equals(manifestPath, StringComparison.OrdinalIgnoreCase))
            ? resolveGitText(worktreePath, ["diff", "--name-only", "--no-renames", $"{integrationBranch}...{candidateRef}", "--"])?
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []
            : [];
        return changedFiles.Concat(noRenamePaths)
            .Where(RepositoryChangeClassifier.IsOwnerProtectedPolicyPath)
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string? Compute(
        string worktreePath, IReadOnlyList<string>? changedFiles,
        Func<string, string[], string?> resolveGitText, string candidateRef,
        bool readCommittedCandidate, string integrationBranch)
    {
        if (changedFiles is null) return null;
        const string manifestPath = "config/acceptance-manifest.json";
        var inputs = new List<(string File, string? Trusted, string? Candidate)>();
        var paths = SelectPolicyPaths(worktreePath, changedFiles, resolveGitText, candidateRef, integrationBranch).ToList();
        var manifestChanged = changedFiles.Any(path =>
            NormalizePath(path).Equals(manifestPath, StringComparison.OrdinalIgnoreCase));
        var candidateManifest = readCommittedCandidate
            ? resolveGitText(worktreePath, ["show", $"{candidateRef}:{manifestPath}"])
            : File.Exists(Path.Combine(worktreePath, "config", "acceptance-manifest.json")) ? "present" : null;
        var manifestMissing = changedFiles.Count > 0 &&
            changedFiles.Any(path => !RepositoryChangeClassifier.IsOwnerProtectedPolicyPath(path)) &&
            candidateManifest is null;
        if (manifestChanged || manifestMissing)
            paths.Add(manifestPath);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(Path.Combine(worktreePath, path.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Owner-protected path escapes worktree: {path}");
            var trusted = resolveGitText(worktreePath, ["show", $"{integrationBranch}:{path}"]);
            if (path == manifestPath && string.IsNullOrWhiteSpace(trusted)) return null;
            var candidate = readCommittedCandidate
                ? resolveGitText(worktreePath, ["show", $"{candidateRef}:{path}"])
                : File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
            if (readCommittedCandidate && candidate is null &&
                resolveGitText(worktreePath, ["cat-file", "-e", $"{candidateRef}:{path}"]) is not null)
                return null;
            inputs.Add((path, trusted, candidate));
        }
        return RepositoryChangeClassifier.ComputeOwnerProtectedChangeFingerprint(inputs);
    }

    internal static string? ComputeForCandidate(string worktreePath, string candidateSha, string integrationBranch)
    {
        if (!AcceptancePolicyChangeDecision.IsFullSha(candidateSha)) return null;
        var changed = AcceptanceGitTextResolver.Resolve(worktreePath,
            ["diff", "--name-only", "--no-renames", $"{integrationBranch}...{candidateSha}", "--"]);
        if (changed is null) return null;
        var changedFiles = changed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Compute(worktreePath, changedFiles,
            AcceptanceGitTextResolver.Resolve, candidateSha, readCommittedCandidate: true, integrationBranch);
    }
}
