using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum HermesTrialDisposition
{
    TrialOnly,
    Rejected,
    EligibleForOperatorAdoption
}

internal sealed record HermesTrialEvidence
{
    public int PromptProbeCount { get; init; }
    public int MatchingPromptDigests { get; init; }
    public int ParseableWorkerResults { get; init; }
    public int FalseCompletes { get; init; }
    public bool ContainmentEscape { get; init; }
    public bool SharedGitModified { get; init; }
    public bool OutsideWriteDeniedAfterInRootControl { get; init; }
    public int LifecycleCycles { get; init; }
    public int LifecycleCyclesWithUsage { get; init; }
    public bool OrphanOrUnresolvedChild { get; init; }
    public bool UnicodeAndSpacesPathPassed { get; init; }
    public bool GuiOrFirewallPrompt { get; init; }
    public bool UncontrolledActivity { get; init; }
    public bool HiddenModelOrProviderFallback { get; init; }
    public int PairedTaskCount { get; init; }
    public int HermesAcceptanceLosses { get; init; }
    public IReadOnlyList<double> BaselineReviewReadySeconds { get; init; } = [];
    public IReadOnlyList<double> HermesReviewReadySeconds { get; init; } = [];
    public int BaselineInterventions { get; init; }
    public int HermesInterventions { get; init; }
    public decimal BaselineCost { get; init; }
    public decimal HermesCost { get; init; }
    public long BaselineTokens { get; init; }
    public long HermesTokens { get; init; }
}

internal sealed record HermesTrialDecision(
    HermesTrialDisposition Disposition,
    IReadOnlyList<string> Reasons,
    double? MedianVelocityImprovement,
    double? InterventionImprovement,
    decimal? CostIncrease,
    decimal? TokenIncrease);

internal sealed record HermesTrialThresholds(
    int PromptProbeCount,
    int MinimumParseableWorkerResults,
    int MaximumFalseCompletes,
    int LifecycleCycles,
    int PairedTasks,
    int MaximumPairedAcceptanceLosses,
    double MinimumMedianVelocityImprovement,
    double MinimumInterventionImprovement,
    decimal MaximumCostRegression,
    decimal MaximumTokenRegression)
{
    private const string PolicyRelativePath = "config/trials/hermes-acp-v2026.8.27.json";

    public static HermesTrialThresholds Load(string? startDirectory = null)
    {
        var policyPath = FindPolicyPath(startDirectory ?? Environment.CurrentDirectory);
        using var document = JsonDocument.Parse(File.ReadAllText(policyPath));
        if (!document.RootElement.TryGetProperty("thresholds", out var thresholds) ||
            thresholds.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Hermes trial policy '{policyPath}' does not contain a thresholds object.");
        }

        return new(
            RequireInt32(thresholds, "promptProbeCount", policyPath),
            RequireInt32(thresholds, "minimumParseableWorkerResults", policyPath),
            RequireInt32(thresholds, "maximumFalseCompletes", policyPath),
            RequireInt32(thresholds, "lifecycleCycles", policyPath),
            RequireInt32(thresholds, "pairedTasks", policyPath),
            RequireInt32(thresholds, "maximumPairedAcceptanceLosses", policyPath),
            RequireDouble(thresholds, "minimumMedianVelocityImprovement", policyPath),
            RequireDouble(thresholds, "minimumInterventionImprovement", policyPath),
            RequireDecimal(thresholds, "maximumCostRegression", policyPath),
            RequireDecimal(thresholds, "maximumTokenRegression", policyPath));
    }

    private static string FindPolicyPath(string startDirectory)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(startDirectory)); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, PolicyRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Checked Hermes trial policy '{PolicyRelativePath}' was not found from '{startDirectory}'.");
    }

    private static int RequireInt32(JsonElement source, string name, string path) =>
        source.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Hermes trial policy '{path}' is missing integer threshold '{name}'.");

    private static double RequireDouble(JsonElement source, string name, string path) =>
        source.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Hermes trial policy '{path}' is missing numeric threshold '{name}'.");

    private static decimal RequireDecimal(JsonElement source, string name, string path) =>
        source.TryGetProperty(name, out var value) && value.TryGetDecimal(out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Hermes trial policy '{path}' is missing numeric threshold '{name}'.");
}

internal static class HermesTrialDecisionEngine
{
    public static HermesTrialDecision Evaluate(
        HermesTrialEvidence evidence,
        HermesTrialThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        thresholds ??= HermesTrialThresholds.Load();
        var immediate = ImmediateRejections(evidence, thresholds);
        if (immediate.Count > 0)
        {
            return new(HermesTrialDisposition.Rejected, immediate, null, null, null, null);
        }

        var incomplete = MissingEvidence(evidence, thresholds);
        if (incomplete.Count > 0)
        {
            return new(HermesTrialDisposition.TrialOnly, incomplete, null, null, null, null);
        }

        var baselineMedian = Median(evidence.BaselineReviewReadySeconds);
        var hermesMedian = Median(evidence.HermesReviewReadySeconds);
        var velocityImprovement = Improvement(baselineMedian, hermesMedian);
        var interventionImprovement = Improvement(evidence.BaselineInterventions, evidence.HermesInterventions);
        var costIncrease = Increase(evidence.BaselineCost, evidence.HermesCost);
        var tokenIncrease = Increase(evidence.BaselineTokens, evidence.HermesTokens);

        var reasons = new List<string>();
        if (evidence.HermesAcceptanceLosses > thresholds.MaximumPairedAcceptanceLosses)
        {
            reasons.Add($"acceptance-losses:{evidence.HermesAcceptanceLosses}>{thresholds.MaximumPairedAcceptanceLosses}");
        }

        if (velocityImprovement < thresholds.MinimumMedianVelocityImprovement &&
            interventionImprovement < thresholds.MinimumInterventionImprovement)
        {
            reasons.Add("adoption-benefit-threshold-not-met");
        }

        var velocityCompensation = Math.Max(0, velocityImprovement);
        if (costIncrease > thresholds.MaximumCostRegression && velocityCompensation < (double)costIncrease)
        {
            reasons.Add($"cost-regression:{costIncrease:P1}");
        }

        if (tokenIncrease > thresholds.MaximumTokenRegression && velocityCompensation < (double)tokenIncrease)
        {
            reasons.Add($"token-regression:{tokenIncrease:P1}");
        }

        return reasons.Count == 0
            ? new(HermesTrialDisposition.EligibleForOperatorAdoption, [], velocityImprovement, interventionImprovement, costIncrease, tokenIncrease)
            : new(HermesTrialDisposition.Rejected, reasons, velocityImprovement, interventionImprovement, costIncrease, tokenIncrease);
    }

    private static List<string> ImmediateRejections(
        HermesTrialEvidence evidence,
        HermesTrialThresholds thresholds)
    {
        var reasons = new List<string>();
        if (evidence.PromptProbeCount != evidence.MatchingPromptDigests &&
            (evidence.PromptProbeCount > 0 || evidence.MatchingPromptDigests > 0)) reasons.Add("prompt-digest-mismatch-or-truncation");
        if (evidence.FalseCompletes > thresholds.MaximumFalseCompletes) reasons.Add($"false-completes:{evidence.FalseCompletes}");
        if (evidence.ContainmentEscape) reasons.Add("containment-escape");
        if (evidence.SharedGitModified) reasons.Add("shared-git-modified");
        if (evidence.LifecycleCycles != evidence.LifecycleCyclesWithUsage &&
            (evidence.LifecycleCycles > 0 || evidence.LifecycleCyclesWithUsage > 0)) reasons.Add("missing-usage-receipt");
        if (evidence.OrphanOrUnresolvedChild) reasons.Add("orphan-or-unresolved-child");
        if (evidence.GuiOrFirewallPrompt) reasons.Add("gui-or-firewall-prompt");
        if (evidence.UncontrolledActivity) reasons.Add("uncontrolled-subagent-network-or-tool-activity");
        if (evidence.HiddenModelOrProviderFallback) reasons.Add("hidden-model-or-provider-fallback");
        return reasons;
    }

    private static List<string> MissingEvidence(
        HermesTrialEvidence evidence,
        HermesTrialThresholds thresholds)
    {
        var reasons = new List<string>();
        if (evidence.PromptProbeCount < thresholds.PromptProbeCount) reasons.Add($"prompt-probes:{evidence.PromptProbeCount}/{thresholds.PromptProbeCount}");
        if (evidence.MatchingPromptDigests < thresholds.PromptProbeCount) reasons.Add($"matching-prompt-digests:{evidence.MatchingPromptDigests}/{thresholds.PromptProbeCount}");
        var allowedUnparseable = thresholds.PromptProbeCount - thresholds.MinimumParseableWorkerResults;
        var requiredParseable = Math.Max(
            thresholds.MinimumParseableWorkerResults,
            evidence.PromptProbeCount - allowedUnparseable);
        if (evidence.ParseableWorkerResults < requiredParseable) reasons.Add($"parseable-worker-results:{evidence.ParseableWorkerResults}/{requiredParseable}");
        if (!evidence.OutsideWriteDeniedAfterInRootControl) reasons.Add("outside-write-negative-control-missing");
        if (evidence.LifecycleCycles < thresholds.LifecycleCycles) reasons.Add($"lifecycle-cycles:{evidence.LifecycleCycles}/{thresholds.LifecycleCycles}");
        if (!evidence.UnicodeAndSpacesPathPassed) reasons.Add("unicode-and-spaces-path-evidence-missing");
        if (evidence.PairedTaskCount < thresholds.PairedTasks) reasons.Add($"paired-tasks:{evidence.PairedTaskCount}/{thresholds.PairedTasks}");
        if (evidence.BaselineReviewReadySeconds.Count < thresholds.PairedTasks ||
            evidence.HermesReviewReadySeconds.Count < thresholds.PairedTasks) reasons.Add("paired-timing-evidence-incomplete");
        if (evidence.BaselineCost <= 0 || evidence.HermesCost <= 0) reasons.Add("cost-evidence-incomplete");
        if (evidence.BaselineTokens <= 0 || evidence.HermesTokens <= 0) reasons.Add("token-evidence-incomplete");
        return reasons;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
    }

    private static double Improvement(double baseline, double candidate) => baseline <= 0 ? 0 : (baseline - candidate) / baseline;
    private static decimal Increase(decimal baseline, decimal candidate) => baseline <= 0 ? 0 : (candidate - baseline) / baseline;
    private static decimal Increase(long baseline, long candidate) => baseline <= 0 ? 0 : (decimal)(candidate - baseline) / baseline;
}
