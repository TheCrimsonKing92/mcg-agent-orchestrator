using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static partial class AcceptancePolicyShardPlanner
{
    internal static PolicyShardPlan BuildPolicyShardPlan(
        IReadOnlyList<string>? changedFiles,
        AcceptanceShardPolicySwitches? switches,
        ICandidateTreeProbe candidateTree,
        IEnumerable<string>? testProjects = null)
    {
        ArgumentNullException.ThrowIfNull(candidateTree);
        var removedPaths = (changedFiles ?? []).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizePath(path)!)
            .Where(path => !candidateTree.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal).ToArray();
        var presentPaths = changedFiles is null ? null : changedFiles
            .Where(path => !removedPaths.Contains(NormalizePath(path), StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var plan = BuildPresentPolicyShardPlan(presentPaths, switches);
        // Include manifest projects even outside the dependency closure: force-full and
        // focused manifest checks must obey the same absence rule as scoped selections.
        var projects = ReferencingProjectsByProject.Keys.Concat(
            ReferencingProjectsByProject.Values.SelectMany(projects => projects))
            .Concat(testProjects ?? [])
            .Select(project => NormalizePath(project)!)
            .Where(project => project.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) &&
                project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var absentProjects = projects.Where(project => !candidateTree.Exists(project))
            .Order(StringComparer.Ordinal).ToArray();
        var clauses = removedPaths.Select(path => $"skipped removed path: {path} (absent from candidate tree)")
            .Concat(absentProjects.Select(project => $"skipped absent test project: {project} (absent from candidate tree)"))
            .ToArray();
        return plan with
        {
            Evidence = clauses.Length == 0 ? plan.Evidence : $"{plan.Evidence}; {string.Join("; ", clauses)}",
            DependencyClosure = plan.DependencyClosure.Except(absentProjects, StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            AbsentProjects = absentProjects.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
    }
}
