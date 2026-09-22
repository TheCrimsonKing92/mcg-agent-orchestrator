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

internal enum HermesTrialGateStatus
{
    Passed,
    Incomplete,
    Failed
}

internal sealed record HermesTrialGateDecision(
    string Gate,
    HermesTrialGateStatus Status,
    IReadOnlyList<string> Reasons);

internal sealed record HermesTrialEvaluation(
    HermesTrialEvidence Evidence,
    IReadOnlyList<HermesTrialGateDecision> Gates,
    HermesTrialDecision Decision);

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

    public static HermesTrialThresholds Load(string? deploymentRoot = null)
    {
        var policyPath = ResolvePolicyPath(deploymentRoot);
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

    private static string ResolvePolicyPath(string? deploymentRoot)
    {
        var root = Path.GetFullPath(deploymentRoot ?? AppContext.BaseDirectory);
        var policyPath = Path.Combine(root, PolicyRelativePath);
        if (File.Exists(policyPath))
        {
            return policyPath;
        }

        throw new FileNotFoundException(
            $"Checked Hermes trial policy '{PolicyRelativePath}' was not found beneath deployment root '{root}'.",
            policyPath);
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
    public static HermesTrialEvaluation EvaluateForComparison(
        TrialComparisonResult comparison,
        HermesTrialEvidence evidence,
        HermesTrialThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(evidence);
        thresholds ??= HermesTrialThresholds.Load();

        var aggregate = Evaluate(evidence, thresholds);
        var terminalGate = EvaluateTerminalReceipt(comparison);
        var reasons = aggregate.Reasons.Concat(terminalGate.Reasons).Distinct(StringComparer.Ordinal).ToArray();
        var disposition = terminalGate.Status switch
        {
            HermesTrialGateStatus.Failed => HermesTrialDisposition.Rejected,
            HermesTrialGateStatus.Incomplete when aggregate.Disposition == HermesTrialDisposition.EligibleForOperatorAdoption =>
                HermesTrialDisposition.TrialOnly,
            _ => aggregate.Disposition
        };
        var decision = aggregate with { Disposition = disposition, Reasons = reasons };
        var gates = new[]
        {
            BuildGate("prompt-output", reasons,
                "prompt-digest", "false-completes", "prompt-probes", "matching-prompt", "parseable-worker"),
            BuildGate("containment", reasons,
                "containment-escape", "shared-git-modified", "outside-write"),
            BuildGate("lifecycle", reasons,
                "missing-usage", "orphan-or-unresolved-child", "lifecycle-cycles"),
            BuildGate("windows-path", reasons,
                "unicode-and-spaces", "gui-or-firewall"),
            BuildGate("paired-quality", reasons,
                "acceptance-losses", "paired-tasks"),
            BuildGate("adoption-benefit", reasons,
                "adoption-benefit", "paired-timing", "cost-", "token-"),
            BuildGate("identity-and-activity", reasons,
                "uncontrolled-subagent", "hidden-model"),
            terminalGate
        };
        return new(evidence, gates, decision);
    }

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

    private static HermesTrialGateDecision EvaluateTerminalReceipt(TrialComparisonResult comparison)
    {
        var hermesArms = comparison.Harnesses
            .Where(arm => arm.HermesTerminalReceipt is not null ||
                arm.Name.Equals("hermes-acp", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (hermesArms.Length == 0)
        {
            return new("terminal-receipt", HermesTrialGateStatus.Incomplete, ["hermes-terminal-receipt-missing"]);
        }

        if (hermesArms.Length != 1)
        {
            return new("terminal-receipt", HermesTrialGateStatus.Failed, ["hermes-terminal-arm-ambiguous"]);
        }

        var arm = hermesArms[0];
        var receipt = arm.HermesTerminalReceipt;
        if (receipt is null)
        {
            return new("terminal-receipt", HermesTrialGateStatus.Incomplete, ["hermes-terminal-receipt-missing"]);
        }

        var failures = new List<string>();
        if (!receipt.Completed || receipt.ExitCode != 0 ||
            arm.Outcome != TrialHarnessOutcome.Completed || arm.ExitCode != 0)
        {
            failures.Add("hermes-terminal-not-completed");
        }
        if (receipt.InputTokens <= 0 || receipt.OutputTokens <= 0) failures.Add("hermes-terminal-usage-missing");
        if (!receipt.CancellationOrShutdownAcknowledged ||
            !receipt.JobExitConfirmed ||
            !receipt.SurvivorInventoryEmpty)
        {
            failures.Add("hermes-terminal-process-unresolved");
        }
        if (receipt.StandardErrorSha256.Length != 64 || !receipt.StandardErrorSha256.All(Uri.IsHexDigit))
        {
            failures.Add("hermes-terminal-stderr-digest-invalid");
        }
        if (receipt.PermissionPolicyViolated || receipt.UnexpectedChild) failures.Add("hermes-terminal-policy-violated");
        if (!WorkerResultParser.TryParseFields(receipt.FinalOutput, out _, out _))
        {
            failures.Add("hermes-terminal-worker-result-invalid");
        }
        if (!string.Equals(receipt.PinnedRelease, HermesAcpAdapter.PinnedRelease, StringComparison.Ordinal) ||
            !string.Equals(receipt.PinnedCommit, HermesAcpAdapter.PinnedCommit, StringComparison.Ordinal))
        {
            failures.Add("hermes-terminal-pinned-version-mismatch");
        }
        if (!string.Equals(receipt.PromptSha256, comparison.WorkloadIdentity?.BriefDigest, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("hermes-terminal-prompt-digest-mismatch");
        }

        var expectedIdentity = comparison.WorkloadIdentity?.ModelIdentity;
        if (string.IsNullOrWhiteSpace(expectedIdentity) ||
            (!expectedIdentity.Equals($"{receipt.Provider}/{receipt.Model}", StringComparison.OrdinalIgnoreCase) &&
             !expectedIdentity.Equals($"{receipt.Provider}:{receipt.Model}", StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add("hermes-terminal-model-provider-mismatch");
        }

        return failures.Count == 0
            ? new("terminal-receipt", HermesTrialGateStatus.Passed, [])
            : new("terminal-receipt", HermesTrialGateStatus.Failed, failures);
    }

    private static HermesTrialGateDecision BuildGate(
        string gate,
        IReadOnlyList<string> allReasons,
        params string[] prefixes)
    {
        var reasons = allReasons
            .Where(reason => prefixes.Any(prefix => reason.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();
        var status = reasons.Length == 0
            ? HermesTrialGateStatus.Passed
            : reasons.All(IsIncompleteReason)
                ? HermesTrialGateStatus.Incomplete
                : HermesTrialGateStatus.Failed;
        return new(gate, status, reasons);
    }

    private static bool IsIncompleteReason(string reason) =>
        reason.StartsWith("prompt-probes", StringComparison.Ordinal) ||
        reason.StartsWith("matching-prompt-digests", StringComparison.Ordinal) ||
        reason.StartsWith("parseable-worker-results", StringComparison.Ordinal) ||
        reason.StartsWith("outside-write-negative-control-missing", StringComparison.Ordinal) ||
        reason.StartsWith("lifecycle-cycles", StringComparison.Ordinal) ||
        reason.StartsWith("unicode-and-spaces-path-evidence-missing", StringComparison.Ordinal) ||
        reason.StartsWith("paired-tasks", StringComparison.Ordinal) ||
        reason.StartsWith("paired-timing-evidence-incomplete", StringComparison.Ordinal) ||
        reason.StartsWith("cost-evidence-incomplete", StringComparison.Ordinal) ||
        reason.StartsWith("token-evidence-incomplete", StringComparison.Ordinal) ||
        reason.StartsWith("hermes-terminal-receipt-missing", StringComparison.Ordinal);
}
