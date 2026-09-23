using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public enum GoalReadinessSeverity
{
    Info,
    Warning,
    Blocker
}

public enum GoalReadinessRecommendation
{
    Proceed,
    UseFiveRoleGoal,
    SplitObjective,
    CreateWorkspace,
    RequireOperatorConfirmation,
    Blocked
}

public sealed record GoalReadinessFinding(
    GoalReadinessSeverity Severity,
    string Kind,
    string Message,
    bool CanOverride);

public sealed record GoalTaskReadiness(
    int TaskNumber,
    TaskId TaskId,
    AgentRole Role,
    WorkTaskStatus Status,
    TaskComplexity Complexity,
    bool HasAssignedAgent,
    IReadOnlyList<string> FileScopes);

public sealed record GoalReadinessPreflightReport(
    GoalId GoalId,
    string Objective,
    GoalStatus Status,
    GoalReadinessRecommendation Recommendation,
    bool AllowsUnattendedStart,
    bool RequiresOperatorConfirmation,
    bool HasHardBlockers,
    string FileScopeConfidence,
    bool RequiresWorkspace,
    bool HasWorkspace,
    IReadOnlyList<GoalReadinessFinding> Findings,
    IReadOnlyList<GoalTaskReadiness> Tasks)
{
    public bool AllowsStart(bool confirmed) =>
        !HasHardBlockers && (!RequiresOperatorConfirmation || confirmed);
}

public static class GoalReadinessPreflight
{
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

    private static readonly Regex FileScopeRegex = new(
        @"(?<![\w.-])(?:src|tests|scripts|docs|config|\.agents)[\\/][A-Za-z0-9_.\\/\-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static GoalReadinessPreflightReport Build(
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        string executionDirectory,
        WorkerProfileCatalog? profiles = null,
        Func<string, GoalId, string?>? resolveWorktree = null,
        IReadOnlyCollection<Goal>? providerHoldScope = null)
    {
        var findings = new List<GoalReadinessFinding>();
        var text = $"{goal.Objective}\n{string.Join('\n', goal.Tasks.Select(task => $"{task.Description}\n{task.VerificationPlan}"))}";
        var tokens = BuildTokenSet(text);
        var highRisk = HighRiskSignals.Where(tokens.Contains).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var external = ExternalSignals.Where(tokens.Contains).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var requiresWorkspace = goal.Tasks.Any(task => task.RequiredRole is AgentRole.Developer or AgentRole.Tester);
        var worktree = (resolveWorktree ?? GoalWorktrees.TryResolve)(executionDirectory, goal.Id);
        var hasWorkspace = !requiresWorkspace || worktree is not null;
        var taskReports = BuildTaskReports(goal, agents);
        var allScopes = taskReports.SelectMany(task => task.FileScopes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var fileScopeConfidence = allScopes.Length switch
        {
            0 when requiresWorkspace => "low",
            0 => "not-required",
            <= 3 => "medium",
            _ => "high"
        };

        if (requiresWorkspace && worktree is null)
        {
            findings.Add(new GoalReadinessFinding(
                GoalReadinessSeverity.Blocker,
                "workspace-missing",
                "File-touching roles require an isolated goal workspace before unattended dispatch.",
                CanOverride: false));
        }

        foreach (var task in taskReports.Where(task => !task.HasAssignedAgent))
        {
            findings.Add(new GoalReadinessFinding(
                GoalReadinessSeverity.Blocker,
                "agent-missing",
                $"Task {task.TaskNumber} {task.Role} has no assigned agent.",
                CanOverride: false));
        }

        if (highRisk.Length > 0)
        {
            findings.Add(new GoalReadinessFinding(
                GoalReadinessSeverity.Blocker,
                "high-risk-objective",
                $"High-risk terms require operator readiness confirmation before unattended start: {string.Join(", ", highRisk)}.",
                CanOverride: true));
        }

        if (external.Length > 0)
        {
            findings.Add(new GoalReadinessFinding(
                GoalReadinessSeverity.Warning,
                "external-dependency",
                $"Objective may depend on external services or secrets: {string.Join(", ", external)}.",
                CanOverride: true));
        }

        if (fileScopeConfidence == "low")
        {
            findings.Add(new GoalReadinessFinding(
                GoalReadinessSeverity.Warning,
                "file-scope-low-confidence",
                "No concrete repository file scopes were detected; prefer five-role planning or add explicit target paths before unattended execution.",
                CanOverride: true));
        }

        if (taskReports.Length == 1 &&
            taskReports[0].Complexity == TaskComplexity.Complex &&
            goal.Tasks[0].RequiredRole == AgentRole.Developer)
        {
            findings.Add(new GoalReadinessFinding(
                GoalReadinessSeverity.Warning,
                "simple-goal-complex",
                "Single Developer task is classified complex; five-role goal planning may reduce rework.",
                CanOverride: true));
        }

        // Always evaluate the canonical dispatch verdict when profiles are available. Existing
        // workspace/risk findings must not mask a held provider binding in ordinary status.
        var readinessVerdict = profiles is not null
            ? DispatchReadinessEvaluator.EvaluateDispatchReadiness(
                goal,
                SubscriptionPlanBuilder.Build(goal, agents, profiles, providerHoldScope: providerHoldScope),
                DateTimeOffset.UtcNow)
            : DispatchReadinessRules.HasAssignedDispatchCandidates(goal)
                ? (DispatchReadinessVerdict)new DispatchReadinessReady()
                : new DispatchReadinessBlocked("No assigned dispatch candidates");
        switch (readinessVerdict)
        {
            case DispatchReadinessDeferred deferred:
                findings.Add(new GoalReadinessFinding(
                    GoalReadinessSeverity.Info,
                    "deferred",
                    $"Readiness preflight found no blockers; assigned tasks are deferred by provider cooldown. {deferred.Reason}.",
                    CanOverride: true));
                break;
            case DispatchReadinessReady when findings.Count == 0:
                findings.Add(new GoalReadinessFinding(
                    GoalReadinessSeverity.Info,
                    "ready",
                    "Readiness preflight found no blockers; assigned tasks are in a dispatchable state.",
                    CanOverride: true));
                break;
            case DispatchReadinessReady:
                break;
            case DispatchReadinessBlocked { ProviderBudgetHold: not null } blocked:
                findings.Add(new GoalReadinessFinding(
                    GoalReadinessSeverity.Blocker,
                    "provider-budget-exhausted",
                    blocked.Reason,
                    CanOverride: false));
                break;
            case DispatchReadinessBlocked blocked:
                findings.Add(new GoalReadinessFinding(
                    GoalReadinessSeverity.Info,
                    "dispatch-not-ready",
                    blocked.Reason,
                    CanOverride: true));
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported dispatch-readiness verdict '{readinessVerdict.GetType().Name}'.");
        }

        var hardBlockers = findings.Any(finding => finding is { Severity: GoalReadinessSeverity.Blocker, CanOverride: false });
        var overrideBlockers = findings.Any(finding => finding is { Severity: GoalReadinessSeverity.Blocker, CanOverride: true });
        var recommendation = BuildRecommendation(hardBlockers, overrideBlockers, requiresWorkspace, hasWorkspace, taskReports, fileScopeConfidence);

        return new GoalReadinessPreflightReport(
            goal.Id,
            goal.Objective,
            goal.Status,
            recommendation,
            AllowsUnattendedStart: !hardBlockers && !overrideBlockers,
            RequiresOperatorConfirmation: overrideBlockers,
            HasHardBlockers: hardBlockers,
            fileScopeConfidence,
            requiresWorkspace,
            hasWorkspace,
            findings,
            taskReports);
    }

    public static void ThrowIfStartBlocked(GoalReadinessPreflightReport report, bool confirmed)
    {
        if (report.AllowsStart(confirmed))
        {
            return;
        }

        var blockerText = string.Join("; ", report.Findings
            .Where(finding => finding.Severity == GoalReadinessSeverity.Blocker)
            .Select(finding => $"{finding.Kind}: {finding.Message}"));
        var confirmation = report.RequiresOperatorConfirmation && !confirmed
            ? " Re-run with --confirm-readiness-risk or confirmReadinessRisk=true after reviewing readiness."
            : "";
        throw new InvalidOperationException($"Goal readiness preflight blocked unattended start. {blockerText}.{confirmation}");
    }

    private static GoalReadinessRecommendation BuildRecommendation(
        bool hardBlockers,
        bool overrideBlockers,
        bool requiresWorkspace,
        bool hasWorkspace,
        GoalTaskReadiness[] tasks,
        string fileScopeConfidence)
    {
        if (hardBlockers)
        {
            return requiresWorkspace && !hasWorkspace
                ? GoalReadinessRecommendation.CreateWorkspace
                : GoalReadinessRecommendation.Blocked;
        }

        if (overrideBlockers)
        {
            return GoalReadinessRecommendation.RequireOperatorConfirmation;
        }

        if (tasks.Length == 1 && tasks[0].Complexity == TaskComplexity.Complex)
        {
            return GoalReadinessRecommendation.UseFiveRoleGoal;
        }

        return fileScopeConfidence == "low"
            ? GoalReadinessRecommendation.UseFiveRoleGoal
            : GoalReadinessRecommendation.Proceed;
    }

    private static GoalTaskReadiness[] BuildTaskReports(Goal goal, IReadOnlyList<AgentDefinition> agents)
    {
        return goal.Tasks
            .Select((task, index) => new GoalTaskReadiness(
                index + 1,
                task.Id,
                task.RequiredRole,
                task.Status,
                TaskComplexityEstimator.Estimate(task.Description, goal.Objective, task.RequiredRole),
                task.AssignedAgentId is not null && agents.Any(agent => agent.Id == task.AssignedAgentId),
                InferFileScopes(goal, task)))
            .ToArray();
    }

    private static string[] InferFileScopes(Goal goal, TaskSpec task)
    {
        var text = $"{goal.Objective}\n{task.Description}\n{task.VerificationPlan}";
        return FileScopeRegex.Matches(text)
            .Select(match => match.Value.Replace('\\', '/').TrimEnd('.', ',', ';', ':', ')', ']'))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
