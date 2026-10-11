using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Impact is evidence only when every fatal class selects the same unique train member.
internal static class MergeTrainImpactAttribution
{
    internal static MergeTrainMemberBinding? TryAttribute(
        IReadOnlyList<string> resultPaths,
        string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members,
        out IReadOnlyList<string> failingClasses)
    {
        failingClasses = [];
        var results = resultPaths.Select(AcceptanceTrxFailureReader.Read).ToArray();
        if (results.Any(result => result.Status != AcceptanceTrxReadStatus.Readable))
            return null;
        // Use the ownership path's reader and fatal/apparatus taxonomy on this same
        // complete snapshot; a second read could discard a newly unreadable artifact.
        var failures = results.SelectMany(result => result.Failures)
            .Where(failure => AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome) &&
                ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is null).ToArray();
        if (failures.Length == 0 || failures.Any(MergeTrainRedAttribution.IsMessageSubjectGuard)) return null;
        var classes = failures.Select(failure => AcceptanceTestSourceResolver.ExtractClassName(failure.TestName ?? string.Empty))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (classes.Any(string.IsNullOrWhiteSpace)) return null;

        try
        {
            var plans = members.Select(member => RepositoryTestImpactPlanner.Plan(member.LandingPaths, workspacePath)).ToArray();
            if (plans.Any(plan => plan.RequiresBroadVerification || plan.ReverseDependencyDegradation is not null)) return null;

            // Operator Option A: a plain whole-project check is usable only if no other
            // member has ANY check against that project, including a filtered check.
            for (var index = 0; index < plans.Length; index++)
            foreach (var check in plans[index].Checks.Where(check => check.TestProject is not null && check.TestClassSelections is null))
            {
                if (!IsPlainProjectCheck(check) || plans.Where((_, other) => other != index)
                    .Any(plan => plan.Checks.Any(other => other.TestProject == check.TestProject))) return null;
            }

            MergeTrainMemberBinding? attributed = null;
            foreach (var className in classes)
            {
                var sources = AcceptanceTestSourceResolver.ResolveSourcePaths(workspacePath, null, className + ".Subject");
                var impacted = plans.Select((plan, index) => (plan, index)).Where(item => item.plan.Checks.Any(check =>
                    check.TestClassSelections is { } selections
                        ? selections.Any(selection => AcceptanceTestSourceResolver.ExtractClassName(selection + ".Subject") == className)
                        : check.TestProject is not null && sources.Count > 0 &&
                            sources.All(source => BelongsToProject(workspacePath, source, check.Command[3]))))
                    .Select(item => members[item.index]).ToArray();
                if (impacted.Length != 1 || (attributed is not null && attributed != impacted[0])) return null;
                attributed = impacted[0];
            }
            failingClasses = classes.Select(name => name!).Order(StringComparer.Ordinal).ToArray();
            return attributed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsPlainProjectCheck(RepositoryTestImpactCheck check) =>
        // Abandoned declaration inspection is a fail-safe expansion, not a plain selection.
        !check.Reason.StartsWith("Focused test selection was abandoned", StringComparison.Ordinal) &&
        check.Command.Count == 6 && check.Command[0] == "dotnet" && check.Command[1] == "test" &&
        check.Command[2] == "--project" && check.Command[3].EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
        check.Command[4] == "--verbosity" && check.Command[5] == "minimal";

    private static bool BelongsToProject(string workspacePath, string source, string project)
    {
        var projectPath = Path.GetFullPath(Path.Combine(workspacePath, project));
        if (!File.Exists(projectPath)) return false;
        var directory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(workspacePath, source)));
        var root = Path.GetFullPath(workspacePath);
        while (directory is not null && directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var projects = Directory.GetFiles(directory, "*.csproj");
            if (projects.Length != 0)
                return projects.Length == 1 && projects[0].Equals(projectPath, StringComparison.OrdinalIgnoreCase);
            directory = Path.GetDirectoryName(directory);
        }
        return false;
    }
}
