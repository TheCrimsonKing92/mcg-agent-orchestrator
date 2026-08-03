using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum WorkerCapability
{
    MultipleGoalCoordination,
    LiveConductorControl,
    HostPerformanceObservation,
    SandboxForbiddenOperation
}

internal sealed record CriterionFeasibilityFinding(
    string Criterion,
    AgentRole Role,
    IReadOnlyList<string> TriggerCategories,
    IReadOnlyList<WorkerCapability> MissingCapabilities)
{
    public string TopicKey => AcceptanceCriterionFeasibility.BuildTopicKey(Criterion);
}

internal sealed record FeasibilityDisposition(
    string Kind,
    string? Value)
{
    public const string ReScope = "re-scope";
    public const string OperatorOwned = "operator-owned";
    public const string ReproducingScenario = "supply-reproducing-scenario";
}

internal static class AcceptanceCriterionFeasibility
{
    internal const string ForkKind = "feasibility";
    internal const string FixedBlastRadius = "high";

    private sealed record RoleCapabilityProfile(
        AgentRole Role,
        IReadOnlySet<WorkerCapability> UnavailableCapabilities);

    private sealed record CapabilityRule(
        string Category,
        WorkerCapability Capability,
        IReadOnlyList<Regex> DemandPatterns);

    private static readonly IReadOnlyList<RoleCapabilityProfile> RoleProfiles =
    [
        new(
            AgentRole.Developer,
            new HashSet<WorkerCapability>
            {
                WorkerCapability.MultipleGoalCoordination,
                WorkerCapability.LiveConductorControl,
                WorkerCapability.HostPerformanceObservation,
                WorkerCapability.SandboxForbiddenOperation
            })
    ];

    // The rules are deliberately data: extending the detector is one additive row plus fixtures.
    // Each pattern requires a capability-demand shape, never a bare common repository word.
    private static readonly IReadOnlyList<CapabilityRule> Rules =
    [
        new(
            "multiple-or-concurrent-goals",
            WorkerCapability.MultipleGoalCoordination,
            Patterns(
                @"\b(?:run|execute|drive|start|dispatch|coordinate|compare|observe)\b.{0,80}\b(?:two|multiple|several|more\s+than\s+one|concurrent|parallel)\b.{0,30}\bgoals?\b",
                @"\b(?:two|multiple|several|more\s+than\s+one|concurrent|parallel)\b.{0,30}\bgoals?\b.{0,80}\b(?:run|execute|drive|start|dispatch|coordinate|compare|observe)\b")),
        new(
            "live-conductor-or-multiple-ticks",
            WorkerCapability.LiveConductorControl,
            Patterns(
                @"\b(?:run|execute|drive|start|restart|observe|control)\b.{0,80}\b(?:real|live|production)?\s*conductor(?:\s+loop)?\b",
                @"\b(?:assert|demonstrate|exercise|observe|test|verify)\b.{0,100}\b(?:cross[-\s]tick|(?:across|over|spanning)\s+(?:two|multiple|several)\s+(?:consecutive\s+)?(?:conductor\s+)?ticks?|(?:two|multiple|several)\s+consecutive\s+(?:conductor\s+)?ticks?|multiple\s+conductor\s+ticks?)\b",
                @"\b(?:cross[-\s]tick|(?:across|over|spanning)\s+(?:two|multiple|several)\s+(?:consecutive\s+)?(?:conductor\s+)?ticks?|(?:two|multiple|several)\s+consecutive\s+(?:conductor\s+)?ticks?|multiple\s+conductor\s+ticks?)\b.{0,100}\b(?:assert|demonstrate|exercise|observe|test|verify)\b")),
        new(
            "whole-host-or-wall-clock-performance",
            WorkerCapability.HostPerformanceObservation,
            Patterns(
                @"\b(?:measure|benchmark|compare|record|assert|observe)\b.{0,100}\b(?:makespan|wall[-\s]clock|elapsed\s+time|performance|latency|throughput)\b.{0,100}\b(?:this|whole|entire|idle|otherwise[-\s]idle)?\s*(?:host|machine)\b",
                @"\b(?:this|whole|entire|idle|otherwise[-\s]idle)\s+(?:host|machine)\b.{0,100}\b(?:measure|benchmark|compare|record|assert|observe)\b",
                @"\b(?:measure|benchmark|compare|record|assert|observe|scan|enumerate)\b.{0,80}\b(?:whole|entire|idle|otherwise[-\s]idle)\s+(?:host|machine)\b")),
        new(
            "worker-sandbox-forbidden-operation",
            WorkerCapability.SandboxForbiddenOperation,
            Patterns(
                @"\b(?:reserve|acquire|hold|occupy|use|schedule)\b.{0,70}\b(?:build|acceptance)\s+slots?\b",
                @"\b(?:run|execute|show|demonstrate|capture|prove)\b.{0,100}\btests?\b.{0,60}\b(?:red|pre[-\s]fix|without\s+(?:the|its)\s+fix)\b",
                @"\b(?:red|pre[-\s]fix)\s+tests?\b.{0,80}\b(?:run|execute|show|demonstrate|capture|prove)\b"))
    ];

    public static IReadOnlyList<CriterionFeasibilityFinding> Evaluate(
        IReadOnlyList<string> criteria,
        AgentRole role)
    {
        var profile = RoleProfiles.SingleOrDefault(candidate => candidate.Role == role);
        if (profile is null)
            return [];

        var findings = new List<CriterionFeasibilityFinding>();
        foreach (var criterion in criteria.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var matches = Rules
                .Where(rule => profile.UnavailableCapabilities.Contains(rule.Capability))
                .Where(rule => rule.DemandPatterns.Any(pattern => pattern.IsMatch(criterion)))
                .ToList();
            if (matches.Count == 0)
                continue;

            findings.Add(new CriterionFeasibilityFinding(
                criterion,
                role,
                matches.Select(match => match.Category).Distinct(StringComparer.Ordinal).ToList(),
                matches.Select(match => match.Capability).Distinct().ToList()));
        }

        return findings;
    }

    public static string BuildQuestion(CriterionFeasibilityFinding finding) => $"""
        Criterion infeasible for {finding.Role}: {finding.Criterion}
        Choose exactly one disposition:
        1. Re-scope — provide a replacement criterion the worker can satisfy.
        2. Mark OPERATOR-OWNED and post-landing — remove it from worker acceptance and retain it for operator verification after landing.
        3. Supply the reproducing scenario — provide the worker-accessible scenario that makes the existing criterion feasible.
        """;

    public static string BuildTopicKey(string criterion)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(criterion.Trim())))
            .ToLowerInvariant()[..16];
        return $"feasibility-{hash}";
    }

    public static bool TryParseDisposition(string? answer, out FeasibilityDisposition disposition)
    {
        disposition = new FeasibilityDisposition(string.Empty, null);
        if (string.IsNullOrWhiteSpace(answer))
            return false;

        var trimmed = answer.Trim();
        if (trimmed.Equals(FeasibilityDisposition.OperatorOwned, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("OPERATOR-OWNED", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("OPERATOR-OWNED/post-landing", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("mark OPERATOR-OWNED and post-landing", StringComparison.OrdinalIgnoreCase))
        {
            disposition = new FeasibilityDisposition(FeasibilityDisposition.OperatorOwned, null);
            return true;
        }

        if (TryParseValuedDisposition(trimmed, FeasibilityDisposition.ReScope, out disposition) ||
            TryParseValuedDisposition(trimmed, FeasibilityDisposition.ReproducingScenario, out disposition))
        {
            return true;
        }

        if (TryParseValuedDisposition(trimmed, "supply the reproducing scenario", out var scenario))
        {
            disposition = scenario with { Kind = FeasibilityDisposition.ReproducingScenario };
            return true;
        }

        return false;
    }

    public static bool IsMeasurementOrAssertionForkFor(
        SpecRefinementFork fork,
        IReadOnlyCollection<CriterionFeasibilityFinding> findings)
    {
        var forkText = $"{fork.TopicKey} {fork.Question}";
        if (!Regex.IsMatch(forkText, @"\b(?:measure|measurement|assert|assertion|verify|verification|evidence|test|makespan)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;

        var forkTokens = SignificantTokens(forkText);
        return findings.Any(finding => SignificantTokens(finding.Criterion).Overlaps(forkTokens));
    }

    private static bool TryParseValuedDisposition(
        string answer,
        string kind,
        out FeasibilityDisposition disposition)
    {
        disposition = new FeasibilityDisposition(string.Empty, null);
        if (!answer.StartsWith(kind, StringComparison.OrdinalIgnoreCase))
            return false;

        var value = answer[kind.Length..].TrimStart(' ', ':', '-', '—').Trim();
        if (value.Length == 0)
            return false;

        disposition = new FeasibilityDisposition(kind, value);
        return true;
    }

    private static IReadOnlyList<Regex> Patterns(params string[] patterns) =>
        patterns.Select(pattern => new Regex(
            pattern,
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline))
        .ToList();

    private static HashSet<string> SignificantTokens(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(match => match.Value)
            .Where(token => token.Length >= 5 && token is not "criterion" and not "worker" and not "should")
            .ToHashSet(StringComparer.Ordinal);
}
