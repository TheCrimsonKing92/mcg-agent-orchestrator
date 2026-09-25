using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private static bool HasOwnerPolicyApproval(string worktreePath, GoalId? goalId)
    {
        if (goalId is null) return false;
        var candidateSha = ResolveGitScalar(worktreePath, "rev-parse", "HEAD");
        var commonGitDirectory = AcceptanceGitTextResolver.Resolve(worktreePath, "rev-parse", "--git-common-dir")?.Trim();
        if (candidateSha is null || commonGitDirectory is null) return false;
        var gitPath = Path.GetFullPath(Path.IsPathRooted(commonGitDirectory)
            ? commonGitDirectory : Path.Combine(worktreePath, commonGitDirectory));
        var repositoryRoot = Directory.GetParent(gitPath)?.FullName;
        if (repositoryRoot is null) return false;
        var storeDirectory = Path.Combine(repositoryRoot, ".orchestrator");
        if (!Directory.Exists(storeDirectory)) return false;
        var store = CollaborationItemStore.ForDirectory(storeDirectory);
        return AcceptancePolicyChangeDecision.IsApprovedAsync(store, goalId.Value, candidateSha)
            .GetAwaiter().GetResult();
    }

    internal static bool HasOwnerPolicyApprovalForTests(
        ICollaborationItemStore store, GoalId goalId, string candidateSha) =>
        AcceptancePolicyChangeDecision.IsApprovedAsync(store, goalId.Value, candidateSha)
            .GetAwaiter().GetResult();

    private static AcceptanceCheckResult? TryClassifyManifestTrust(
        string worktreePath,
        IReadOnlyList<string>? changedFiles) =>
        TryClassifyManifestTrustCore(worktreePath, changedFiles, AcceptanceGitTextResolver.Resolve);

    internal static AcceptanceCheckResult? TryClassifyManifestTrustWithGitForTests(
        string worktreePath,
        IReadOnlyList<string>? changedFiles,
        Func<string, string[], string?> resolveGitText) =>
        TryClassifyManifestTrustCore(worktreePath, changedFiles, resolveGitText);

    internal static AcceptanceCheckResult? TryClassifyManifestTrustWithGitForTests(
        string worktreePath,
        IReadOnlyList<string>? changedFiles,
        Func<string, string[], string?> resolveGitText,
        ICollaborationItemStore decisions,
        GoalId goalId,
        string candidateSha)
    {
        var failure = TryClassifyManifestTrustCore(worktreePath, changedFiles, resolveGitText);
        return failure is not null && HasOwnerPolicyApprovalForTests(decisions, goalId, candidateSha)
            ? null : failure;
    }

    private static AcceptanceCheckResult? TryClassifyManifestTrustCore(
        string worktreePath,
        IReadOnlyList<string>? changedFiles,
        Func<string, string[], string?> resolveGitText)
    {
        if (changedFiles is null)
            return null;

        const string manifestPath = "config/acceptance-manifest.json";
        var manifestChanged = changedFiles.Any(path =>
            NormalizePath(path).Equals(manifestPath, StringComparison.OrdinalIgnoreCase));
        var candidateManifestPath = Path.Combine(worktreePath, "config", "acceptance-manifest.json");
        var manifestMissing = changedFiles.Count > 0 &&
            changedFiles.Any(path => !RepositoryChangeClassifier.IsOwnerProtectedPolicyPath(path)) &&
            !File.Exists(candidateManifestPath);
        var details = new List<string>();

        foreach (var rawPath in changedFiles.Where(RepositoryChangeClassifier.IsOwnerProtectedPolicyPath))
        {
            var path = NormalizePath(rawPath);
            var fullPath = Path.GetFullPath(Path.Combine(worktreePath, path.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Owner-protected path escapes worktree: {path}");
            var candidate = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
            var trusted = resolveGitText(worktreePath, ["show", $"main:{path}"]);
            IReadOnlyList<string> fields;
            try
            {
                fields = RepositoryChangeClassifier.DescribeJsonChanges(trusted, candidate);
            }
            catch (JsonException)
            {
                fields = ["(unparseable JSON)"];
            }
            details.Add($"{path}: changed field(s) {string.Join(", ", fields.Count == 0 ? ["$"] : fields)}");
        }

        if (manifestChanged || manifestMissing)
        {
            var trusted = resolveGitText(worktreePath, ["show", "main:config/acceptance-manifest.json"]);
            if (string.IsNullOrWhiteSpace(trusted))
            {
                if (manifestChanged)
                    return new AcceptanceCheckResult(
                        "acceptance manifest trusted dimensions", false, 1,
                        "Trusted main acceptance manifest could not be compared; refusing candidate engine settings.",
                        ResultSummary: "trusted manifest comparison unavailable");
            }
            else if (manifestMissing)
            {
                details.Add($"{manifestPath}: changed field(s) $ (removed or renamed)");
            }
            else
            {
                var candidate = File.ReadAllText(candidateManifestPath);
                var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);
                if (decision.RequiresTrustedReview)
                    return new AcceptanceCheckResult(
                        "acceptance manifest trusted dimensions", false, 1, decision.Evidence,
                        ResultSummary: "operator review required");
                var fields = RepositoryChangeClassifier.DescribeJsonChanges(trusted, candidate);
                if (fields.Count > 0)
                    details.Add($"{manifestPath}: changed field(s) {string.Join(", ", fields)}");
            }
        }

        return details.Count == 0 ? null : new AcceptanceCheckResult(
            "owner-protected configuration", false, 1,
            $"{string.Join("; ", details)}; owner decision required for this candidate",
            ResultSummary: "operator review required");
    }
}
