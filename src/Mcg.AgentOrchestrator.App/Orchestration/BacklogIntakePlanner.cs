using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record BacklogIntakeItem(
    string Id,
    string Heading,
    string Body,
    IReadOnlyList<string> TargetFiles,
    IReadOnlyList<AgentRole> Roles,
    IReadOnlyList<string> RiskLabels,
    IReadOnlyList<string> Verification,
    IReadOnlyList<string> Dependencies,
    string SuggestedObjective,
    string WorkspacePlan,
    string AcceptanceChecks,
    string FollowUpUpdates,
    RepositoryScopeConfidence ScopeConfidence,
    IReadOnlyList<string> ExcludedTargetFiles);

internal sealed record BacklogIntakePlan(string BacklogPath, IReadOnlyList<BacklogIntakeItem> Items);

internal static class BacklogIntakePlanner
{
    internal const string TargetScopeHeadingLine = "Target files/scopes:";
    internal const string PreciseScopeMarkerLine = "Scope confidence: precise";
    internal const string UnknownScopeMarkerLine = "Scope confidence: unknown";
    internal const string ScopeIncludesHeadingLine = "Includes:";
    internal const string ScopeExclusionsHeadingLine = "Exclusions:";

    public static BacklogIntakePlan Build(string backlogStorePath, string? headingFilter = null, int maxItems = 5)
    {
        if (string.IsNullOrWhiteSpace(backlogStorePath))
        {
            throw new ArgumentException("Value cannot be empty.", nameof(backlogStorePath));
        }

        if (!File.Exists(backlogStorePath))
        {
            throw new FileNotFoundException($"SQLite backlog store was not found: {backlogStorePath}", backlogStorePath);
        }

        var items = new BacklogStore(backlogStorePath).ListAsync().GetAwaiter().GetResult()
            .Where(item => !item.Title.StartsWith("Decision record", StringComparison.OrdinalIgnoreCase))
            .Where(item => MatchesFilter(item, headingFilter))
            .Take(maxItems)
            .Select(item => BuildItem(
                item.Id,
                item.Title,
                item.Body,
                ResolveRepositoryRoot(backlogStorePath)))
            .ToList();

        return new BacklogIntakePlan("backlog store", items);
    }

    private static bool MatchesFilter(BacklogItem item, string? headingFilter)
    {
        if (string.IsNullOrWhiteSpace(headingFilter))
        {
            return true;
        }

        return item.Id.Equals(headingFilter, StringComparison.OrdinalIgnoreCase) ||
            item.Title.Contains(headingFilter, StringComparison.OrdinalIgnoreCase) ||
            item.Body.Contains(headingFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static BacklogIntakeItem BuildItem(string id, string heading, string body, string repositoryRoot)
    {
        var text = $"{heading}\n{body}";
        var scope = GoalFileScopeInference.DeriveForIntake(body, repositoryRoot);
        if (GoalFileScopeInference.IsKnownBoilerplateScopeSet(scope.Includes))
        {
            throw new InvalidOperationException(
                "Newly refined target scope matched the known intake boilerplate set.");
        }

        var roles = InferRoles(text);
        var risks = InferRisks(text);
        var verification = InferVerification(text);
        var dependencies = InferDependencies(text);
        var objective = BuildObjective(heading, body, scope, verification);
        return new BacklogIntakeItem(
            id,
            heading,
            body,
            scope.Includes,
            roles,
            risks,
            verification,
            dependencies,
            objective,
            "Run `workspace create <goal-prefix>` before file-touching work; use the goal worktree for dispatch, tests, acceptance, and cleanup.",
            "Inspect goal-branch diff, run focused tests named in the plan, verify worker result/evidence records, then run acceptance before merge.",
            "Close or update the backlog item (`backlog-close`) and record goal-boundary evidence with `dogfood-log add <goal-prefix>`; durable entries live in `.orchestrator/dogfood-log.db`.",
            scope.Confidence,
            scope.Exclusions);
    }

    private static List<AgentRole> InferRoles(string text)
    {
        var roles = new List<AgentRole> { AgentRole.Planner };
        if (ContainsAny(text, "research", "primary external sources", "APIs"))
        {
            roles.Add(AgentRole.Researcher);
        }

        if (ContainsAny(text, "add", "implement", "command", "dashboard", "worker", "workflow", "broker", "supervisor", "rollback", "recovery"))
        {
            roles.Add(AgentRole.Developer);
        }

        roles.Add(AgentRole.Tester);
        roles.Add(AgentRole.Reviewer);
        return roles.Distinct().ToList();
    }

    private static List<string> InferRisks(string text)
    {
        var risks = new List<string>();
        if (ContainsAny(text, "paid", "subscription", "provider", "budget", "retry-after"))
        {
            risks.Add("subscription-cost");
        }

        if (ContainsAny(text, "rollback", "abandon", "delete", "remove", "cleanup", "worktree"))
        {
            risks.Add("state-and-worktree-mutation");
        }

        if (ContainsAny(text, "parallel", "concurrent", "build environment", "lock", "CS2012"))
        {
            risks.Add("concurrency");
        }

        if (ContainsAny(text, "dashboard", "operator", "inbox"))
        {
            risks.Add("operator-ux");
        }

        return risks.Count == 0 ? ["routine"] : risks;
    }

    private static List<string> InferVerification(string text)
    {
        var checks = new List<string> { "Focused unit tests for changed planner/command/mapper behavior." };
        if (ContainsAny(text, "dashboard", "operator inbox", "work-summary"))
        {
            checks.Add("Dashboard rendering/API DTO test for visible operator evidence.");
        }

        if (ContainsAny(text, "subscription", "worker", "dispatch", "preflight"))
        {
            checks.Add("Worker dispatch/preflight test with no paid worker start.");
        }

        if (ContainsAny(text, "workspace", "acceptance", "rollback", "abandon"))
        {
            checks.Add("Worktree or acceptance workflow test proving clean state transitions.");
        }

        if (ContainsAny(text, "build", "build/test", "dotnet test", "CS2012"))
        {
            checks.Add("Isolated dotnet/build-environment regression test.");
        }

        return checks;
    }

    private static List<string> InferDependencies(string text)
    {
        var dependencies = new List<string>();
        if (ContainsAny(text, "lifecycle", "unattended", "supervisor"))
        {
            dependencies.Add("parallel safety and deterministic recovery gates should be in place before unattended execution.");
        }

        if (ContainsAny(text, "parallel", "concurrent"))
        {
            dependencies.Add("backlog slicing should declare file scopes before cross-goal parallel starts.");
        }

        return dependencies.Count == 0 ? ["none"] : dependencies;
    }

    private static string BuildObjective(
        string heading,
        string body,
        GoalFileScopeDerivation scope,
        IReadOnlyList<string> verification)
    {
        return string.Join(Environment.NewLine, [
            $"Backlog slice: {heading}",
            string.Empty,
            body,
            string.Empty,
            TargetScopeHeadingLine,
            scope.Confidence == RepositoryScopeConfidence.Precise
                ? PreciseScopeMarkerLine
                : UnknownScopeMarkerLine,
            ScopeIncludesHeadingLine,
            .. (scope.Includes.Count == 0 ? ["- none"] : scope.Includes.Select(path => $"- {path}")),
            ScopeExclusionsHeadingLine,
            .. (scope.Exclusions.Count == 0 ? ["- none"] : scope.Exclusions.Select(path => $"- {path}")),
            string.Empty,
            "Verification:",
            .. verification.Select(check => $"- {check}")
        ]);
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveRepositoryRoot(string backlogStorePath)
    {
        var storeDirectory = Path.GetDirectoryName(Path.GetFullPath(backlogStorePath))
            ?? Environment.CurrentDirectory;
        if (Path.GetFileName(storeDirectory).Equals(".orchestrator", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.GetParent(storeDirectory)?.FullName ?? storeDirectory;
        }

        return storeDirectory;
    }
}
