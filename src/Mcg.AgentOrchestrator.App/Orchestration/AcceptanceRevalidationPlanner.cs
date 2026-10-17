using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceRevalidationPlanner
{
    internal static AcceptanceRevalidationPlan Plan(string repositoryPath, string verifiedMainSha,
        string currentMainSha, IReadOnlyList<string> candidatePaths)
    {
        if (string.Equals(verifiedMainSha, currentMainSha, StringComparison.OrdinalIgnoreCase))
            return new AcceptanceRevalidationPlan.Unchanged();
        try
        {
            var candidates = Normalize(candidatePaths);
            if (candidates.Length == 0 ||
                !AcceptanceMainAdvanceGitEvidence.IsAncestor(repositoryPath, verifiedMainSha, currentMainSha) ||
                AcceptanceMainAdvanceGitEvidence.ReadChangedPaths(repositoryPath, verifiedMainSha, currentMainSha) is not { } changed)
                return new AcceptanceRevalidationPlan.FullRegate("unknown");
            var landed = Normalize(changed);
            if (landed.Length == 0)
                return new AcceptanceRevalidationPlan.FullRegate("unknown");
            foreach (var path in landed)
                if (candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                    return new AcceptanceRevalidationPlan.FullRegate("overlap", path);
            foreach (var path in landed.Concat(candidates))
            {
                var area = RepositoryOwnershipMap.Classify(path).Area;
                if (area is not (RepositoryOwnershipArea.Source or RepositoryOwnershipArea.Test or
                    RepositoryOwnershipArea.Documentation or RepositoryOwnershipArea.SharedInfrastructure or RepositoryOwnershipArea.Script))
                    return new AcceptanceRevalidationPlan.FullRegate("unsafe-area", path, area.ToString());
            }

            var checks = ReadChecks(repositoryPath);
            if (checks is null)
                return new AcceptanceRevalidationPlan.FullRegate("manifest-unreadable");
            var affected = new List<string>();
            var unaffected = new List<string>();
            foreach (var check in checks)
            {
                var members = AcceptanceLaneClosureHasher.TryComputeMembers(repositoryPath, check);
                var isAffected = members is null || landed.Any(path => members.Any(member =>
                    member == "." || path.Equals(RepositoryPathOverlap.Normalize(member), StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(RepositoryPathOverlap.Normalize(member) + "/", StringComparison.OrdinalIgnoreCase)));
                (isAffected ? affected : unaffected).Add(check.Name);
            }
            return new AcceptanceRevalidationPlan.Revalidate(landed, candidates, affected, unaffected);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return new AcceptanceRevalidationPlan.FullRegate("unknown");
        }
    }

    private static IReadOnlyList<AcceptanceManifestCheck>? ReadChecks(string repositoryPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryPath, "config", "acceptance-manifest.json")));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("checks", out var elements) || elements.ValueKind != JsonValueKind.Array)
                return null;
            // CheckDeclaredTestProjects filters focused-evidence projects by filename. Revalidation
            // must include every dotnet-test lane, including projects outside that narrower inventory.
            var checks = elements.Deserialize<AcceptanceManifestCheck[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var eligible = checks?.Where(check => check is not null &&
                string.Equals(check.Type, "dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(check.Project)).ToArray();
            return eligible is { Length: > 0 } ? eligible : null;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return null;
        }
    }

    private static string[] Normalize(IEnumerable<string> paths) => paths
        .Where(path => !string.IsNullOrWhiteSpace(path)).Select(RepositoryPathOverlap.Normalize)
        .Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase).ToArray();
}
