using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private string? _ownerPolicyDecisionStoreDirectory;

    private bool HasOwnerPolicyApproval(string worktreePath, GoalId? goalId, IReadOnlyList<string>? changedFiles)
    {
        var candidateSha = ResolveGitScalar(worktreePath, "rev-parse", "HEAD");
        if (candidateSha is null) return false;
        if (HasOwnerPolicyApprovalForCandidate(goalId, candidateSha)) return true;
        if (goalId is null || _ownerPolicyDecisionStoreDirectory is null ||
            !Directory.Exists(_ownerPolicyDecisionStoreDirectory)) return false;
        var fingerprint = ComputeOwnerProtectedChangeFingerprint(worktreePath, changedFiles,
            AcceptanceGitTextResolver.Resolve, "HEAD", readCommittedCandidate: false, IntegrationBranch);
        return fingerprint is not null && AcceptancePolicyChangeDecision.IsApprovedForFingerprintAsync(
            CollaborationItemStore.ForDirectory(_ownerPolicyDecisionStoreDirectory), goalId.Value, fingerprint)
            .GetAwaiter().GetResult();
    }

    internal bool HasOwnerPolicyApprovalForCandidateTests(GoalId goalId, string candidateSha) =>
        HasOwnerPolicyApprovalForCandidate(goalId, candidateSha);

    private bool HasOwnerPolicyApprovalForCandidate(GoalId? goalId, string? candidateSha)
    {
        if (goalId is null) return false;
        if (candidateSha is null || _ownerPolicyDecisionStoreDirectory is null ||
            !Directory.Exists(_ownerPolicyDecisionStoreDirectory)) return false;
        var store = CollaborationItemStore.ForDirectory(_ownerPolicyDecisionStoreDirectory);
        return AcceptancePolicyChangeDecision.IsApprovedAsync(store, goalId.Value, candidateSha)
            .GetAwaiter().GetResult();
    }

    internal static bool HasOwnerPolicyApprovalForTests(
        ICollaborationItemStore store, GoalId goalId, string candidateSha) =>
        AcceptancePolicyChangeDecision.IsApprovedAsync(store, goalId.Value, candidateSha)
            .GetAwaiter().GetResult();

    private AcceptanceCheckResult? TryClassifyManifestTrust(
        string worktreePath,
        IReadOnlyList<string>? changedFiles) =>
        TryClassifyManifestTrustCore(worktreePath, changedFiles, AcceptanceGitTextResolver.Resolve, IntegrationBranch);

    internal static AcceptanceCheckResult? TryClassifyManifestTrustWithGitForTests(
        string worktreePath,
        IReadOnlyList<string>? changedFiles,
        Func<string, string[], string?> resolveGitText) =>
        TryClassifyManifestTrustCore(worktreePath, changedFiles, resolveGitText, TrunkBranchName.Default);

    internal static AcceptanceCheckResult? TryClassifyManifestTrustWithGitForTests(
        string worktreePath,
        IReadOnlyList<string>? changedFiles,
        Func<string, string[], string?> resolveGitText,
        ICollaborationItemStore decisions,
        GoalId goalId,
        string candidateSha)
    {
        var failure = TryClassifyManifestTrustCore(worktreePath, changedFiles, resolveGitText, TrunkBranchName.Default);
        if (failure is null || HasOwnerPolicyApprovalForTests(decisions, goalId, candidateSha))
            return null;
        var fingerprint = ComputeOwnerProtectedChangeFingerprint(
            worktreePath, changedFiles, resolveGitText, "HEAD", readCommittedCandidate: false, TrunkBranchName.Default);
        return fingerprint is not null && AcceptancePolicyChangeDecision.IsApprovedForFingerprintAsync(
            decisions, goalId.Value, fingerprint).GetAwaiter().GetResult() ? null : failure;
    }

    private static AcceptanceCheckResult? TryClassifyManifestTrustCore(
        string worktreePath,
        IReadOnlyList<string>? changedFiles,
        Func<string, string[], string?> resolveGitText, string integrationBranch)
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

        foreach (var rawPath in SelectOwnerProtectedPolicyPaths(worktreePath, changedFiles, resolveGitText, "HEAD", integrationBranch))
        {
            var path = NormalizePath(rawPath);
            var fullPath = Path.GetFullPath(Path.Combine(worktreePath, path.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Owner-protected path escapes worktree: {path}");
            var candidate = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
            var trusted = resolveGitText(worktreePath, ["show", $"{integrationBranch}:{path}"]);
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
            var trusted = resolveGitText(worktreePath, ["show", $"{integrationBranch}:config/acceptance-manifest.json"]);
            if (string.IsNullOrWhiteSpace(trusted))
            {
                if (manifestChanged)
                {
                    if (details.Count == 0 && !File.Exists(candidateManifestPath))
                        return new AcceptanceCheckResult(
                            "acceptance manifest trusted dimensions", false, 1,
                            "Trusted main acceptance manifest could not be compared; refusing candidate engine settings.",
                            ResultSummary: "trusted manifest comparison unavailable");
                    details.Add($"{manifestPath}: changed field(s) $ (trusted main unavailable)");
                }
            }
            else if (manifestMissing)
            {
                details.Add($"{manifestPath}: changed field(s) $ (removed or renamed)");
            }
            else
            {
                var candidate = File.ReadAllText(candidateManifestPath);
                try
                {
                    var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);
                    var fields = RepositoryChangeClassifier.DescribeJsonChanges(trusted, candidate);
                    if (decision.RequiresTrustedReview && details.Count == 0 &&
                        fields.All(IsExistingGuardedEnginePath))
                        return new AcceptanceCheckResult(
                            "acceptance manifest trusted dimensions", false, 1, decision.Evidence,
                            ResultSummary: "operator review required");
                    if (fields.Count > 0 || decision.RequiresTrustedReview)
                        details.Add($"{manifestPath}: changed field(s) {string.Join(", ", fields.Count == 0 ? decision.SecurityCriticalChanges : fields)}");
                }
                catch (JsonException)
                {
                    details.Add($"{manifestPath}: changed field(s) $ (unparseable JSON)");
                }
            }
        }

        return details.Count == 0 ? null : new AcceptanceCheckResult(
            "owner-protected configuration", false, 1,
            $"{string.Join("; ", details)}; owner decision required for this candidate",
            ResultSummary: "operator review required");
    }

    private static bool IsExistingGuardedEnginePath(string path) =>
        path is "engine.enforceStructuralCoverage" or "engine.partitionVerdictFullRerunEveryN" ||
        path.StartsWith("engine.mtpInvocations", StringComparison.Ordinal);
}
