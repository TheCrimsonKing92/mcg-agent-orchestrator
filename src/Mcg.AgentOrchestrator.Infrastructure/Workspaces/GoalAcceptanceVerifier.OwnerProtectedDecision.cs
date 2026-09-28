using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    internal sealed record OwnerProtectedDecision(AcceptanceCheckResult? Failure, AcceptanceCheckResult? Pass);

    private OwnerProtectedDecision EvaluateOwnerProtectedConfiguration(string worktreePath, GoalId? goalId,
        IReadOnlyList<string>? changedFiles, IReadOnlyList<AcceptanceOwnerProtectedCohortMember>? cohortMembers)
    {
        var store = _ownerPolicyDecisionStoreDirectory is { } directory && Directory.Exists(directory)
            ? CollaborationItemStore.ForDirectory(directory) : null;
        return EvaluateOwnerProtectedConfigurationCore(worktreePath, goalId, changedFiles, cohortMembers,
            AcceptanceGitTextResolver.Resolve, store,
            () => AcceptanceTestInventorySource.Read(worktreePath, AcceptanceGitTextResolver.Resolve),
            () => ResolveGitScalar(worktreePath, "rev-parse", "HEAD"));
    }

    internal static OwnerProtectedDecision EvaluateOwnerProtectedConfigurationForTests(string worktreePath,
        GoalId? goalId, IReadOnlyList<string>? changedFiles,
        IReadOnlyList<AcceptanceOwnerProtectedCohortMember>? cohortMembers,
        Func<string, string[], string?> gitText, ICollaborationItemStore? decisions,
        Func<AcceptanceTestInventory> inventory, string? candidateSha) =>
        EvaluateOwnerProtectedConfigurationCore(worktreePath, goalId, changedFiles, cohortMembers,
            gitText, decisions, inventory, () => candidateSha);

    private static OwnerProtectedDecision EvaluateOwnerProtectedConfigurationCore(string worktreePath,
        GoalId? goalId, IReadOnlyList<string>? changedFiles,
        IReadOnlyList<AcceptanceOwnerProtectedCohortMember>? cohortMembers,
        Func<string, string[], string?> gitText, ICollaborationItemStore? decisions,
        Func<AcceptanceTestInventory> inventory, Func<string?> candidateSha)
    {
        var failure = TryClassifyManifestTrustCore(worktreePath, changedFiles, gitText);
        if (failure is null) return new(null, null);
        if (goalId is { } singleGoal && decisions is not null &&
            IsApproved(worktreePath, changedFiles, gitText, decisions, singleGoal, candidateSha(),
                "HEAD", readCommittedCandidate: false))
            return new(null, null);
        var equivalent = IsEquivalentManifestChange(worktreePath, changedFiles, gitText, inventory, "HEAD",
                gitText(worktreePath, ["show", "main:config/acceptance-manifest.json"]),
                File.Exists(Path.Combine(worktreePath, "config", "acceptance-manifest.json"))
                    ? File.ReadAllText(Path.Combine(worktreePath, "config", "acceptance-manifest.json")) : null);
        if (cohortMembers is { Count: > 0 })
        {
            if (!CohortChangesAreApproved(worktreePath, cohortMembers, gitText, decisions, inventory))
                return new(failure, null);
            return equivalent
                ? new(null, new AcceptanceCheckResult("owner-protected configuration", true, 0,
                    "partition-equivalent", ResultSummary: "partition-equivalent"))
                : new(null, null);
        }
        if (equivalent)
            return new(null, new AcceptanceCheckResult("owner-protected configuration", true, 0,
                "partition-equivalent", ResultSummary: "partition-equivalent"));
        return new(failure, null);
    }

    private static bool IsEquivalentManifestChange(string worktreePath, IReadOnlyList<string>? changedFiles,
        Func<string, string[], string?> gitText, Func<AcceptanceTestInventory> inventory,
        string candidateRef, string? trusted, string? candidate)
    {
        if (changedFiles is null || !changedFiles.Any(path => NormalizePath(path).Equals(
                "config/acceptance-manifest.json", StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(trusted) || string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            var noRename = gitText(worktreePath,
                ["diff", "--name-only", "--no-renames", $"main...{candidateRef}", "--"]);
            if (noRename is null || changedFiles.Concat(SplitPaths(noRename))
                    .Any(RepositoryChangeClassifier.IsOwnerProtectedPolicyPath))
                return false;
            return AcceptanceManifestPartitionEquivalence.IsEquivalent(trusted, candidate, inventory);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
                                      ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsApproved(string worktreePath, IReadOnlyList<string>? changedFiles,
        Func<string, string[], string?> gitText, ICollaborationItemStore decisions,
        GoalId goalId, string? candidateSha, string candidateRef, bool readCommittedCandidate)
    {
        if (candidateSha is null) return false;
        if (AcceptancePolicyChangeDecision.IsApprovedAsync(decisions, goalId.Value, candidateSha)
            .GetAwaiter().GetResult()) return true;
        var fingerprint = ComputeOwnerProtectedChangeFingerprint(worktreePath, changedFiles, gitText,
            candidateRef, readCommittedCandidate);
        return fingerprint is not null && AcceptancePolicyChangeDecision.IsApprovedForFingerprintAsync(
            decisions, goalId.Value, fingerprint).GetAwaiter().GetResult();
    }

    private static bool CohortChangesAreApproved(string worktreePath,
        IReadOnlyList<AcceptanceOwnerProtectedCohortMember> members,
        Func<string, string[], string?> gitText, ICollaborationItemStore? decisions,
        Func<AcceptanceTestInventory> inventory)
    {
        const string manifest = "config/acceptance-manifest.json";
        try
        {
            var combinedDiff = gitText(worktreePath, ["diff", "--name-only", "--no-renames", "main...HEAD", "--"]);
            if (combinedDiff is null) return false;
            var combined = ProtectedPaths(SplitPaths(combinedDiff));
            var changes = new List<(AcceptanceOwnerProtectedCohortMember Member, string[] Files, HashSet<string> Protected)>();
            foreach (var member in members)
            {
                if (!AcceptancePolicyChangeDecision.IsFullSha(member.CandidateSha)) return false;
                var diff = gitText(worktreePath,
                    ["diff", "--name-only", "--no-renames", $"main...{member.CandidateSha}", "--"]);
                if (diff is null) return false;
                var files = SplitPaths(diff);
                changes.Add((member, files, ProtectedPaths(files)));
            }
            foreach (var path in combined)
            {
                var owners = changes.Where(change => change.Protected.Contains(path)).ToArray();
                if (owners.Length != 1) return false;
                var memberText = gitText(worktreePath, ["show", $"{owners[0].Member.CandidateSha}:{path}"]);
                var combinedText = gitText(worktreePath, ["show", $"HEAD:{path}"]);
                if (!string.Equals(memberText, combinedText, StringComparison.Ordinal)) return false;
            }
            foreach (var change in changes.Where(change => change.Protected.Count > 0))
            {
                if (change.Protected.Any(path => !combined.Contains(path))) return false;
                if (decisions is not null && IsApproved(worktreePath, change.Files, gitText, decisions, change.Member.GoalId,
                        change.Member.CandidateSha, change.Member.CandidateSha, readCommittedCandidate: true)) continue;
                if (change.Protected.Count == 1 && change.Protected.Contains(manifest) &&
                    IsEquivalentManifestChange(worktreePath, change.Files, gitText, inventory, change.Member.CandidateSha,
                        gitText(worktreePath, ["show", $"main:{manifest}"]),
                        gitText(worktreePath, ["show", $"{change.Member.CandidateSha}:{manifest}"]))) continue;
                return false;
            }
            return combined.Count > 0;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
                                      ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string[] SplitPaths(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static HashSet<string> ProtectedPaths(IReadOnlyList<string> changedFiles)
    {
        var paths = changedFiles.Where(RepositoryChangeClassifier.IsOwnerProtectedPolicyPath)
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (changedFiles.Any(path => NormalizePath(path).Equals("config/acceptance-manifest.json",
                StringComparison.OrdinalIgnoreCase))) paths.Add("config/acceptance-manifest.json");
        return paths;
    }
}
