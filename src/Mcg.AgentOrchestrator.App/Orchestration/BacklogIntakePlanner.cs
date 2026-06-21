using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record BacklogIntakeItem(
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
    string FollowUpUpdates);

internal sealed record BacklogIntakePlan(string BacklogPath, IReadOnlyList<BacklogIntakeItem> Items);

internal static class BacklogIntakePlanner
{
    private static readonly Regex PathRegex = new(
        @"(?<![\w.-])(?:src|tests|scripts|docs|config|\.agents|\.github)[\\/][A-Za-z0-9_.\\/\-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static BacklogIntakePlan Build(string backlogStorePath, string? headingFilter = null, int maxItems = 5)
    {
        if (string.IsNullOrWhiteSpace(backlogStorePath))
        {
            throw new ArgumentException("Value cannot be empty.", nameof(backlogStorePath));
        }

        var items = new BacklogStore(backlogStorePath).ListAsync().GetAwaiter().GetResult()
            .Where(item => !item.Title.StartsWith("Decision record", StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(headingFilter) ||
                item.Title.Contains(headingFilter, StringComparison.OrdinalIgnoreCase))
            .Take(maxItems)
            .Select(item => BuildItem(item.Title, item.Body))
            .ToList();

        return new BacklogIntakePlan("backlog store", items);
    }

    private static BacklogIntakeItem BuildItem(string heading, string body)
    {
        var text = $"{heading}\n{body}";
        var targetFiles = InferTargetFiles(text);
        var roles = InferRoles(text);
        var risks = InferRisks(text);
        var verification = InferVerification(text);
        var dependencies = InferDependencies(text);
        var objective = BuildObjective(heading, body, targetFiles, verification);
        return new BacklogIntakeItem(
            heading,
            body,
            targetFiles,
            roles,
            risks,
            verification,
            dependencies,
            objective,
            "Run `workspace create <goal-prefix>` before file-touching work; use the goal worktree for dispatch, tests, acceptance, and cleanup.",
            "Inspect goal-branch diff, run focused tests named in the plan, verify worker result/evidence records, then run acceptance before merge.",
            "Close or update the backlog item (`backlog-close`) and add a DOGFOOD_LOG entry with commands, tests, blockers, and Model fit.");
    }

    private static List<string> InferTargetFiles(string text)
    {
        var paths = PathRegex.Matches(text)
            .Select(match => match.Value.Replace('\\', '/').TrimEnd('.', ',', ';', ':', ')', ']'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ContainsAny(text, "dashboard", "operator inbox", "work-summary"))
        {
            Add(paths, "src/Mcg.AgentOrchestrator.App/Dashboard");
            Add(paths, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DashboardRenderingTests.cs");
        }

        if (ContainsAny(text, "lifecycle", "run-goal", "acceptance", "workspace"))
        {
            Add(paths, "src/Mcg.AgentOrchestrator.App/Cli");
            Add(paths, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTests.cs");
        }

        if (ContainsAny(text, "subscription", "worker", "dispatch", "preflight"))
        {
            Add(paths, "src/Mcg.AgentOrchestrator.Infrastructure/Workers");
            Add(paths, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs");
        }

        if (ContainsAny(text, "build", "build/test", "dotnet test", "CS2012", "MSBuild", "VBCSCompiler"))
        {
            Add(paths, "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces");
            Add(paths, "scripts/Invoke-IsolatedDotnet.ps1");
        }

        return paths;
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

    private static string BuildObjective(string heading, string body, IReadOnlyList<string> targetFiles, IReadOnlyList<string> verification)
    {
        return string.Join(Environment.NewLine, [
            $"Backlog slice: {heading}",
            string.Empty,
            body,
            string.Empty,
            "Target files/scopes:",
            .. targetFiles.Select(path => $"- {path}"),
            string.Empty,
            "Verification:",
            .. verification.Select(check => $"- {check}")
        ]);
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static void Add(List<string> paths, string path)
    {
        if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(path);
        }
    }
}
