namespace Mcg.AgentOrchestrator.Core;

public static partial class RepositoryTestImpactPlanner
{
    internal static RepositoryTestImpactPlan Plan(
        RepositoryChangeSummary summary,
        ITestClassDeclarationReader declarationReader,
        ICandidateTreeProbe candidateTree)
    {
        ArgumentNullException.ThrowIfNull(candidateTree);
        var removedPaths = summary.Files.Select(file => file.Path)
            .Where(path => !candidateTree.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal).ToArray();
        var presentSummary = removedPaths.Length == 0 ? summary : RepositoryChangeClassifier.Classify(
            summary.Files.Select(file => file.Path).Except(removedPaths, StringComparer.OrdinalIgnoreCase));
        // Escalation belongs to the whole change; absence only narrows test selection.
        var escalates = summary.HasBuildSystemChanges || summary.HasSecuritySensitiveChanges;
        var plan = PlanPresentPaths(escalates ? summary : presentSummary, declarationReader);
        var absentProjects = plan.Checks.Select(TestProjectPath).OfType<string>()
            .Where(project => !candidateTree.Exists(project))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal).ToArray();
        var clauses = removedPaths.Select(path => $"skipped removed path: {path} (absent from candidate tree)")
            .Concat(absentProjects.Select(project => $"skipped absent test project: {project} (absent from candidate tree)"))
            .ToArray();
        if (clauses.Length == 0)
            return plan;

        var reason = $"{plan.Summary}; {string.Join("; ", clauses)}";
        var checks = plan.Checks.Where(check =>
                TestProjectPath(check) is not { } project ||
                !absentProjects.Contains(project, StringComparer.OrdinalIgnoreCase))
            .Select(check => check with { Reason = $"{check.Reason}; {string.Join("; ", clauses)}" })
            .ToArray();
        return checks.Length == 0
            ? NoBuild(reason)
            : plan with { Summary = reason, Checks = checks };
    }

    private static string? TestProjectPath(RepositoryTestImpactCheck check)
    {
        for (var index = 0; index + 1 < check.Command.Count; index++)
        {
            if (check.Command[index].Equals("--project", StringComparison.OrdinalIgnoreCase) &&
                check.Command[index + 1].EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                return check.Command[index + 1];
        }
        return null;
    }
}
