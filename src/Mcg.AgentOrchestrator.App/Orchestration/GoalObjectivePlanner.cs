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

internal enum GoalIntakePipeline
{
    DeveloperOnly,
    DeveloperReviewer,
    FiveRole
}

internal enum GoalIntakePipelineRequest
{
    Auto,
    FiveRole
}

internal static class GoalIntakePipelineRequestParser
{
    public const string AllowedValues = "auto, five-role";

    public static GoalIntakePipelineRequest Parse(string value)
    {
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return GoalIntakePipelineRequest.Auto;
        }

        if (value.Equals("five-role", StringComparison.OrdinalIgnoreCase))
        {
            return GoalIntakePipelineRequest.FiveRole;
        }

        throw new ArgumentException($"--pipeline must be one of: {AllowedValues}.");
    }

    public static GoalIntakePipeline? ToPipelineOverride(this GoalIntakePipelineRequest request) =>
        request == GoalIntakePipelineRequest.FiveRole
            ? GoalIntakePipeline.FiveRole
            : null;
}

internal sealed record GoalIntakePipelineDecision(
    GoalIntakePipeline Pipeline,
    bool IsOverride,
    IReadOnlyList<string> Reasons)
{
    public string Workflow => Pipeline switch
    {
        GoalIntakePipeline.DeveloperOnly => "developer-only",
        GoalIntakePipeline.DeveloperReviewer => "developer-reviewer",
        GoalIntakePipeline.FiveRole => "five-role",
        _ => Pipeline.ToString()
    };

    public string SelectionSource => IsOverride ? "explicitly-required" : "automatic";
}

internal sealed record GoalHistoricalOutcomeRates(
    double AggregateFailureRate,
    double RealFailureRate,
    double EnvironmentalFailureRate,
    double ManufacturedFixedFailureRate,
    double UnknownEraFailureRate);

internal sealed record GoalObjectivePlan(
    string Objective,
    string Workflow,
    GoalObjectiveDisposition Disposition,
    TaskComplexity EstimatedComplexity,
    string? HistoricalTimeEstimate,
    GoalHistoricalOutcomeRates? HistoricalOutcomeRates,
    IReadOnlyList<string> RiskLabels,
    IReadOnlyList<string> CapabilityWarnings,
    GoalIntakePipelineDecision PipelineDecision,
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

    private static readonly string[] SecurityRiskSignals =
    [
        "auth",
        "authentication",
        "authorization",
        "credential",
        "credentials",
        "permission",
        "permissions",
        "secret",
        "secrets",
        "security",
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

    private static readonly (Regex Pattern, string Label)[] CapabilityWarningSignals =
    [
        (new Regex(@"\bgit\s+fetch\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "git fetch"),
        (new Regex(@"\bgit\s+pull\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "git pull"),
        (new Regex(@"\bgit\s+push\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "git push"),
        (new Regex(@"\bgit\s+rebase\b|\brebase\s+(?:onto\s+)?(?:origin|upstream|remote|[A-Za-z0-9_.-]+/[A-Za-z0-9_.\-/]+)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "git rebase"),
        (new Regex(@"(?<![\w.-])gh\s+[A-Za-z0-9][A-Za-z0-9-]*\b|\bGitHub CLI\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "gh CLI"),
        (new Regex(@"(?<![\w.-])(?:https://|http://)(?:github\.com|gitlab\.com|bitbucket\.org)/[^\s]+|(?<![\w.-])git@(?:github\.com|gitlab\.com|bitbucket\.org):[^\s]+|(?<![\w.-])ssh://git@(?:github\.com|gitlab\.com|bitbucket\.org)/[^\s]+", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "remote repository URL"),
        (new Regex(@"\b(?:log\s+in|login|sign\s+in|authenticate)\s+(?:to|with)\s+(?:git|github|gitlab|bitbucket)\b|\b(?:use|provide)\s+(?:my|your|operator)\s+(?:git|github|gitlab|bitbucket)?\s*(?:credential|credentials|token|pat)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "credential-required action")
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

    public static GoalObjectivePlan Build(
        string objective,
        GoalIntakePipeline? pipelineOverride = null,
        IEnumerable<TaskDurationStatsRecord>? durationStats = null)
    {
        var normalized = objective.Trim();
        var classificationText = BuildActionableClassificationText(normalized);
        var fileScopeText = BuildActionableFileScopeText(normalized);
        var tokens = BuildTokenSet(classificationText);
        var fileScopes = InferFileScopes(fileScopeText);
        var estimated = TaskComplexityEstimator.Estimate(classificationText, classificationText, AgentRole.Developer);
        var historicalRecord = FindHistoricalEstimate(durationStats, AgentRole.Developer, estimated);
        var historicalEstimate = BuildHistoricalEstimate(historicalRecord);
        var historicalOutcomeRates = BuildHistoricalOutcomeRates(historicalRecord);
        var riskLabels = BuildRiskLabels(tokens, fileScopes, estimated);
        var capabilityWarnings = BuildCapabilityWarnings(normalized);
        var pipelineDecision = SelectPipeline(riskLabels, fileScopes, pipelineOverride);
        var workflow = pipelineDecision.Workflow;
        var requiredTools = BuildRequiredTools(fileScopes, tokens);
        var requiredVerification = BuildRequiredVerification(fileScopes, tokens, estimated);
        var ambiguous = pipelineOverride is null && IsAmbiguous(normalized, tokens, fileScopes);

        return new GoalObjectivePlan(
            normalized,
            workflow,
            ambiguous ? GoalObjectiveDisposition.NeedsClarification : GoalObjectiveDisposition.Ready,
            estimated,
            historicalEstimate,
            historicalOutcomeRates,
            riskLabels,
            capabilityWarnings,
            pipelineDecision,
            fileScopes,
            requiredTools,
            requiredVerification,
            BuildTaskBoundaries(pipelineDecision.Pipeline, estimated, requiredVerification),
            ambiguous
                ? "Add the target subsystem, expected behavior, and at least one file scope or concrete artifact before creating tasks."
                : BuildRecommendation(pipelineDecision, estimated, riskLabels, fileScopes, historicalEstimate));
    }

    public static GoalObjectivePlan Build(
        string objective,
        bool simple,
        IEnumerable<TaskDurationStatsRecord>? durationStats = null) =>
        Build(
            objective,
            simple ? GoalIntakePipeline.DeveloperOnly : null,
            durationStats);

    public static void ThrowIfBlocked(GoalObjectivePlan plan)
    {
        if (plan.CanCreateGoal)
        {
            return;
        }

        throw new InvalidOperationException($"Goal objective needs clarification before task creation. {plan.Recommendation}");
    }

    public static IReadOnlyList<string> BuildCapabilityWarnings(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return CapabilityWarningSignals
            .Where(signal => signal.Pattern.IsMatch(text))
            .Select(signal => $"Brief capability warning: workers cannot perform credential-required or operator-only actions; operator-side action required for {signal.Label}.")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildActionableClassificationText(string objective) =>
        BuildActionableText(objective, " ");

    private static string BuildActionableFileScopeText(string objective) =>
        BuildActionableText(objective, "\n");

    private static string BuildActionableText(string objective, string separator)
    {
        var withoutParentheticalMeta = Regex.Replace(
            objective,
            @"\((?=[^)]*\b(?:classif(?:y|ier|ication)|risk label|router|validat(?:e|ing))\b)[^)]*\)",
            " ",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var parts = Regex.Split(withoutParentheticalMeta, @"(?<=[.!?])\s+|\r?\n+")
            .Select(part => part.Trim())
            .Where(part => !string.IsNullOrWhiteSpace(part) && !IsMetaCommentary(part))
            .ToArray();

        return parts.Length == 0 ? objective : string.Join(separator, parts);
    }

    private static bool IsMetaCommentary(string text)
    {
        var lower = text.ToLowerInvariant();
        return StartsWithAny(lower, "note:", "context:", "background:", "regression:", "why:", "because ") ||
            (lower.Contains("classif", StringComparison.Ordinal) &&
                lower.Contains("risk", StringComparison.Ordinal) &&
                lower.Contains("label", StringComparison.Ordinal)) ||
            (lower.Contains("validat", StringComparison.Ordinal) &&
                lower.Contains("router", StringComparison.Ordinal));
    }

    private static bool StartsWithAny(string text, params string[] prefixes)
    {
        return prefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
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

    private static string[] InferFileScopes(string text) =>
        GoalFileScopeInference.FromText(text)
            .Select(scope => scope.Path)
            .ToArray();

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

        if (SecurityRiskSignals.Any(tokens.Contains))
        {
            labels.Add("security-risk");
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

    private static GoalIntakePipelineDecision SelectPipeline(
        string[] riskLabels,
        string[] fileScopes,
        GoalIntakePipeline? pipelineOverride)
    {
        if (pipelineOverride is { } forced)
        {
            return new GoalIntakePipelineDecision(
                forced,
                IsOverride: true,
                [$"operator override selected {FormatPipeline(forced)}"]);
        }

        if (riskLabels.Contains("scope-implicit", StringComparer.OrdinalIgnoreCase))
        {
            return new GoalIntakePipelineDecision(
                GoalIntakePipeline.FiveRole,
                IsOverride: false,
                ["scope-implicit objective needs Planner and Researcher to define the work before implementation"]);
        }

        var reviewReasons = new List<string>();
        AddReason("high-risk", "high-risk objective needs pre-acceptance review");
        AddReason("security-risk", "security-risk objective needs pre-acceptance review");
        AddReason("multi-scope", "multi-scope objective needs reviewer coverage across touched areas");
        AddReason("external-dependency", "external-dependency objective needs integration-risk review");
        AddReason("complex", "complex objective needs reviewer coverage before acceptance");

        if (reviewReasons.Count > 0)
        {
            return new GoalIntakePipelineDecision(
                GoalIntakePipeline.DeveloperReviewer,
                IsOverride: false,
                reviewReasons);
        }

        return new GoalIntakePipelineDecision(
            GoalIntakePipeline.DeveloperOnly,
            IsOverride: false,
            fileScopes.Length == 0
                ? ["no routed risk labels found"]
                : ["scoped low-risk objective can be implemented by Developer only"]);

        void AddReason(string label, string reason)
        {
            if (riskLabels.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                reviewReasons.Add(reason);
            }
        }
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
            tools.Add(InferBuildTestTool(fileScopes, tokens));
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

    private static string InferBuildTestTool(string[] fileScopes, HashSet<string> tokens)
    {
        var dotnetSignals = tokens.Contains("dotnet") || tokens.Contains("csproj") || tokens.Contains("sln") ||
            fileScopes.Any(s => s.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                               s.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                               s.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        if (dotnetSignals)
        {
            return "scripts/Invoke-IsolatedDotnet.ps1 or dotnet test with goal build lease";
        }

        var goSignals = tokens.Contains("golang") ||
            fileScopes.Any(s => s.EndsWith(".go", StringComparison.OrdinalIgnoreCase) ||
                               s.Contains("go.mod", StringComparison.OrdinalIgnoreCase));
        if (goSignals)
        {
            return "go build / go test ./...";
        }

        var nodeSignals = tokens.Contains("npm") || tokens.Contains("yarn") ||
            fileScopes.Any(s => s.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                               s.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
                               s.Contains("package.json", StringComparison.OrdinalIgnoreCase));
        if (nodeSignals)
        {
            return "npm/yarn (package.json scripts)";
        }

        var pythonSignals = tokens.Contains("python") || tokens.Contains("pip") || tokens.Contains("pytest") ||
            fileScopes.Any(s => s.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
        if (pythonSignals)
        {
            return "python / pytest";
        }

        return "run the project's build and test commands";
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
        GoalIntakePipeline pipeline,
        TaskComplexity complexity,
        string[] verification)
    {
        if (pipeline == GoalIntakePipeline.DeveloperOnly)
        {
            return
            [
                new GoalObjectiveTaskBoundary(1, AgentRole.Developer, "Implement the requested change and record evidence.", "workspace-write", verification[0])
            ];
        }

        if (pipeline == GoalIntakePipeline.DeveloperReviewer)
        {
            return
            [
                new GoalObjectiveTaskBoundary(1, AgentRole.Developer, complexity == TaskComplexity.Complex ? "Implement the scoped slice and keep changes narrow." : "Implement the focused change.", "workspace-write", verification[0]),
                new GoalObjectiveTaskBoundary(2, AgentRole.Reviewer, "Review diff, tests, and worker evidence before acceptance.", "read-only", "Review must mention residual risk and acceptance readiness.")
            ];
        }

        return
        [
            new GoalObjectiveTaskBoundary(1, AgentRole.Researcher, "Inspect current source and prior evidence before implementation.", "read-only", "Research notes must cite exact files or state that no source change is needed."),
            new GoalObjectiveTaskBoundary(2, AgentRole.Planner, "Synthesize the durable research into a criterion-mapped implementation plan.", "read-only", "Plan must map every acceptance criterion and identify target files, ownership, edge contracts, risks, and verification."),
            new GoalObjectiveTaskBoundary(3, AgentRole.Developer, complexity == TaskComplexity.Complex ? "Implement the scoped slice and keep changes narrow." : "Implement the focused change.", "workspace-write", verification[0]),
            new GoalObjectiveTaskBoundary(4, AgentRole.Tester, "Run focused verification and capture failures as evidence.", "workspace-write", verification[0]),
            new GoalObjectiveTaskBoundary(5, AgentRole.Reviewer, "Review diff, tests, and worker evidence before acceptance.", "read-only", "Review must mention residual risk and acceptance readiness.")
        ];
    }

    private static string BuildRecommendation(
        GoalIntakePipelineDecision pipelineDecision,
        TaskComplexity complexity,
        string[] riskLabels,
        string[] fileScopes,
        string? historicalEstimate)
    {
        var suffix = string.IsNullOrWhiteSpace(historicalEstimate)
            ? string.Empty
            : $" Historical estimate: {historicalEstimate}";
        if (pipelineDecision.IsOverride && pipelineDecision.Pipeline == GoalIntakePipeline.DeveloperOnly && complexity == TaskComplexity.Complex)
        {
            return "Operator override selected Developer-only; split complex work if the scope is not mechanical." + suffix;
        }

        if (pipelineDecision.Pipeline == GoalIntakePipeline.DeveloperReviewer)
        {
            return "Route through Developer+Reviewer before acceptance." + suffix;
        }

        if (pipelineDecision.Pipeline == GoalIntakePipeline.FiveRole)
        {
            return "Route through the five-role pipeline so planning and research define the open scope." + suffix;
        }

        return "Proceed with the selected workflow and focused verification." + suffix;
    }

    private static string FormatPipeline(GoalIntakePipeline pipeline) =>
        pipeline switch
        {
            GoalIntakePipeline.DeveloperOnly => "Developer-only",
            GoalIntakePipeline.DeveloperReviewer => "Developer+Reviewer",
            GoalIntakePipeline.FiveRole => "five-role",
            _ => pipeline.ToString()
        };

    private static TaskDurationStatsRecord? FindHistoricalEstimate(
        IEnumerable<TaskDurationStatsRecord>? durationStats,
        AgentRole role,
        TaskComplexity complexity)
    {
        if (durationStats is null)
        {
            return null;
        }

        return TaskDurationReport.FindEstimate(durationStats, role, complexity);
    }

    private static string? BuildHistoricalEstimate(TaskDurationStatsRecord? record)
    {
        if (record?.MedianLegitimateRuntime is null)
        {
            return null;
        }

        var p90 = record.P90LegitimateRuntime is null
            ? string.Empty
            : $" (p90 {FormatDuration(record.P90LegitimateRuntime.Value)})";
        return $"{record.Role} {record.Complexity} tasks: ~{FormatDuration(record.MedianLegitimateRuntime.Value)} legitimate runtime{p90}, " +
            $"real/code failure rate {record.RealFailureRate:P0}, environmental/infrastructure rate {record.EnvironmentalFailureRate:P0}, " +
            $"manufactured-fixed rate {record.ManufacturedFixedFailureRate:P0}, unknown-era rate {record.UnknownEraFailureRate:P0} " +
            $"(legacy aggregate {record.FailureRate:P0}); excludes {FormatDuration(record.MedianFailureInterventionOverhead)} median failure/intervention overhead.";
    }

    private static GoalHistoricalOutcomeRates? BuildHistoricalOutcomeRates(TaskDurationStatsRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        return new GoalHistoricalOutcomeRates(
            record.FailureRate,
            record.RealFailureRate,
            record.EnvironmentalFailureRate,
            record.ManufacturedFixedFailureRate,
            record.UnknownEraFailureRate);
    }

    private static string FormatDuration(TimeSpan? duration)
    {
        if (duration is null)
        {
            return "n/a";
        }

        var value = duration.Value;
        return value.TotalMinutes >= 1
            ? $"{value.TotalMinutes:0.#} min"
            : $"{value.TotalSeconds:0.#} sec";
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
