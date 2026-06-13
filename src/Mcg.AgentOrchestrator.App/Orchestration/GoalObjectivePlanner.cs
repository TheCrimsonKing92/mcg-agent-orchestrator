using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum GoalObjectiveDisposition
{
    Ready,
    NeedsClarification
}

internal sealed record GoalObjectiveTaskBoundary(
    int Index,
    AgentRole Role,
    string Purpose,
    string Capability,
    string Verification);

internal sealed record GoalObjectivePlan(
    string Objective,
    string Workflow,
    GoalObjectiveDisposition Disposition,
    TaskComplexity EstimatedComplexity,
    IReadOnlyList<string> RiskLabels,
    IReadOnlyList<string> FileScopes,
    IReadOnlyList<string> RequiredTools,
    IReadOnlyList<string> RequiredVerification,
    IReadOnlyList<GoalObjectiveTaskBoundary> TaskBoundaries,
    string Recommendation)
{
    public bool CanCreateGoal => Disposition == GoalObjectiveDisposition.Ready;
}

internal static class GoalObjectivePlanner
{
    private static readonly Regex FileScopeRegex = new(
        @"(?<![\w.-])(?:src|tests|scripts|docs|config|\.agents)[\\/][A-Za-z0-9_.\\/\-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly string[] HighRiskSignals =
    [
        "auth",
        "authentication",
        "authorization",
        "credential",
        "credentials",
        "delete",
        "destructive",
        "migration",
        "permission",
        "permissions",
        "production",
        "rollback",
        "secret",
        "secrets",
        "token"
    ];

    private static readonly string[] ExternalSignals =
    [
        "api",
        "browser",
        "database",
        "github",
        "network",
        "openai",
        "anthropic",
        "service",
        "webhook"
    ];

    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "add",
        "change",
        "do",
        "fix",
        "handle",
        "improve",
        "it",
        "make",
        "stuff",
        "things",
        "this",
        "update",
        "work"
    };

    public static GoalObjectivePlan Build(string objective, bool simple)
    {
        var normalized = objective.Trim();
        var tokens = BuildTokenSet(normalized);
        var fileScopes = InferFileScopes(normalized);
        var estimated = TaskComplexityEstimator.Estimate(normalized, normalized, AgentRole.Developer);
        var riskLabels = BuildRiskLabels(tokens, fileScopes, estimated);
        var workflow = simple ? "simple-goal" : "goal";
        var requiredTools = BuildRequiredTools(fileScopes, tokens);
        var requiredVerification = BuildRequiredVerification(fileScopes, tokens, estimated);
        var ambiguous = IsAmbiguous(normalized, tokens, fileScopes);

        return new GoalObjectivePlan(
            normalized,
            workflow,
            ambiguous ? GoalObjectiveDisposition.NeedsClarification : GoalObjectiveDisposition.Ready,
            estimated,
            riskLabels,
            fileScopes,
            requiredTools,
            requiredVerification,
            BuildTaskBoundaries(simple, estimated, requiredVerification),
            ambiguous
                ? "Add the target subsystem, expected behavior, and at least one file scope or concrete artifact before creating tasks."
                : BuildRecommendation(simple, estimated, riskLabels, fileScopes));
    }

    public static void ThrowIfBlocked(GoalObjectivePlan plan)
    {
        if (plan.CanCreateGoal)
        {
            return;
        }

        throw new InvalidOperationException($"Goal objective needs clarification before task creation. {plan.Recommendation}");
    }

    private static bool IsAmbiguous(string objective, HashSet<string> tokens, string[] fileScopes)
    {
        if (fileScopes.Length > 0)
        {
            return false;
        }

        if (objective.Length < 12)
        {
            return true;
        }

        var meaningful = tokens.Where(token => !GenericWords.Contains(token)).ToArray();
        return meaningful.Length == 0 || (tokens.Count <= 3 && meaningful.Length <= 1);
    }

    private static string[] InferFileScopes(string text)
    {
        return FileScopeRegex.Matches(text)
            .Select(match => match.Value.Replace('\\', '/').TrimEnd('.', ',', ';', ':', ')', ']'))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] BuildRiskLabels(
        HashSet<string> tokens,
        string[] fileScopes,
        TaskComplexity complexity)
    {
        var labels = new List<string>();
        if (complexity == TaskComplexity.Complex)
        {
            labels.Add("complex");
        }

        if (HighRiskSignals.Any(tokens.Contains))
        {
            labels.Add("high-risk");
        }

        if (ExternalSignals.Any(tokens.Contains))
        {
            labels.Add("external-dependency");
        }

        if (fileScopes.Length == 0)
        {
            labels.Add("scope-implicit");
        }
        else if (fileScopes.Length > 3)
        {
            labels.Add("multi-scope");
        }

        return labels.Count == 0 ? ["low-risk"] : labels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] BuildRequiredTools(string[] fileScopes, HashSet<string> tokens)
    {
        var tools = new List<string> { "rg" };
        if (fileScopes.Any(scope => scope.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
                                    scope.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)) ||
            tokens.Contains("build") ||
            tokens.Contains("test") ||
            tokens.Contains("dotnet"))
        {
            tools.Add("scripts/Invoke-IsolatedDotnet.ps1 or dotnet test with goal build lease");
        }

        if (fileScopes.Any(scope => scope.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)))
        {
            tools.Add("markdown/source review");
        }

        if (fileScopes.Any(scope => scope.StartsWith(".agents/", StringComparison.OrdinalIgnoreCase)))
        {
            tools.Add("skill-creator guidance");
        }

        return tools.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] BuildRequiredVerification(
        string[] fileScopes,
        HashSet<string> tokens,
        TaskComplexity complexity)
    {
        var verification = new List<string>();
        if (fileScopes.Any(scope => scope.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
                                    scope.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)) ||
            tokens.Contains("build") ||
            tokens.Contains("test") ||
            complexity == TaskComplexity.Complex)
        {
            verification.Add("Run focused automated tests or explain why a broader suite is required.");
        }

        if (fileScopes.Any(scope => scope.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)))
        {
            verification.Add("Review rendered or source documentation diff.");
        }

        if (verification.Count == 0)
        {
            verification.Add("Record manual verification evidence tied to the objective.");
        }

        return verification.ToArray();
    }

    private static GoalObjectiveTaskBoundary[] BuildTaskBoundaries(
        bool simple,
        TaskComplexity complexity,
        string[] verification)
    {
        if (simple)
        {
            return
            [
                new GoalObjectiveTaskBoundary(1, AgentRole.Developer, "Implement the requested change and record evidence.", "workspace-write", verification[0])
            ];
        }

        return
        [
            new GoalObjectiveTaskBoundary(1, AgentRole.Planner, "Clarify plan, scope, and acceptance criteria.", "read-only", "Plan must identify target files and risks."),
            new GoalObjectiveTaskBoundary(2, AgentRole.Researcher, "Inspect current source and prior evidence before implementation.", "read-only", "Research notes must cite exact files or state that no source change is needed."),
            new GoalObjectiveTaskBoundary(3, AgentRole.Developer, complexity == TaskComplexity.Complex ? "Implement the scoped slice and keep changes narrow." : "Implement the focused change.", "workspace-write", verification[0]),
            new GoalObjectiveTaskBoundary(4, AgentRole.Tester, "Run focused verification and capture failures as evidence.", "workspace-write", verification[0]),
            new GoalObjectiveTaskBoundary(5, AgentRole.Reviewer, "Review diff, tests, and worker evidence before acceptance.", "read-only", "Review must mention residual risk and acceptance readiness.")
        ];
    }

    private static string BuildRecommendation(
        bool simple,
        TaskComplexity complexity,
        string[] riskLabels,
        string[] fileScopes)
    {
        if (simple && complexity == TaskComplexity.Complex)
        {
            return "Prefer a five-role goal or split into smaller simple-goals before subscription dispatch.";
        }

        if (riskLabels.Contains("high-risk"))
        {
            return "Create an isolated workspace and require readiness confirmation before unattended dispatch.";
        }

        if (fileScopes.Length == 0)
        {
            return "Proceed only after Planner/Researcher confirm concrete file scopes.";
        }

        return "Proceed with the selected workflow and focused verification.";
    }

    private static HashSet<string> BuildTokenSet(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var start = -1;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index < text.Length && char.IsLetterOrDigit(text[index]))
            {
                if (start < 0)
                {
                    start = index;
                }

                continue;
            }

            if (start >= 0)
            {
                tokens.Add(text[start..index]);
                start = -1;
            }
        }

        return tokens;
    }
}
