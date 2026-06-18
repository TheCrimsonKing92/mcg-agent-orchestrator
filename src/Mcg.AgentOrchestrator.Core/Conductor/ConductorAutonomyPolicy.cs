using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Core.Conductor;

// Auto = conductor proceeds without human escalation; Escalate = require human sign-off.
public enum ConductorTransitionDecision
{
    Auto,
    Escalate
}

// Risk tiers ordered lowest (0) to highest (4), aligned to RepositoryChangeSummary properties:
// DocsOnly=IsDocsOnly, Behavior=HasBehaviorChanges, Build=HasBuildSystemChanges,
// Security=HasSecuritySensitiveChanges, Broad=RequiresBroadVerification.
public enum ChangeRiskTier
{
    DocsOnly = 0,
    Behavior = 1,
    Build = 2,
    Security = 3,
    Broad = 4
}

// Immutable autonomy envelope for the upcoming conductor. Controls concurrency, budget caps,
// risk-gated auto-promotion at Merged, and per-lifecycle-state Auto/Escalate decisions.
// Load from .orchestrator/conductor-policy.json or use a named preset.
public sealed record ConductorAutonomyPolicy(
    string Name,
    int MaxConcurrentPaidWorkers,
    decimal MaxTotalBudget,
    int MaxCriterionRetries,
    IReadOnlyDictionary<string, decimal>? PerProviderBudgetCaps,
    ChangeRiskTier? AutoPromoteRiskThreshold,
    IReadOnlyDictionary<GoalLifecycleState, ConductorTransitionDecision> TransitionMap)
{
    private static readonly GoalLifecycleState[] AllStates =
        Enum.GetValues<GoalLifecycleState>();

    private static readonly JsonDocumentOptions JsonParseOptions = new() { CommentHandling = JsonCommentHandling.Skip };

    public static ConductorAutonomyPolicy Default => Conservative;

    // Auto the mechanical transitions; escalate Merged→main unless the change is low-risk (DocsOnly);
    // escalate on failures, blocks, and human-input waits.
    public static ConductorAutonomyPolicy Conservative { get; } = new(
        "Conservative",
        MaxConcurrentPaidWorkers: 2,
        MaxTotalBudget: 5.00m,
        MaxCriterionRetries: 1,
        PerProviderBudgetCaps: null,
        AutoPromoteRiskThreshold: ChangeRiskTier.DocsOnly,
        TransitionMap: new Dictionary<GoalLifecycleState, ConductorTransitionDecision>
        {
            [GoalLifecycleState.Created] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.AwaitingClarification] = ConductorTransitionDecision.Escalate,
            [GoalLifecycleState.WorkspaceReady] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.Dispatched] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.Running] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.AwaitingVerification] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.Verified] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.Merged] = ConductorTransitionDecision.Escalate,
            [GoalLifecycleState.Recorded] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.CleanedUp] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.Failed] = ConductorTransitionDecision.Escalate,
            [GoalLifecycleState.Blocked] = ConductorTransitionDecision.Escalate,
            [GoalLifecycleState.AwaitingHumanInput] = ConductorTransitionDecision.Escalate,
        });

    // Auto everything within caps. AutoPromoteRiskThreshold = Broad so all change types
    // get auto-promoted at Merged when risk is known.
    public static ConductorAutonomyPolicy Permissive { get; } = new(
        "Permissive",
        MaxConcurrentPaidWorkers: 5,
        MaxTotalBudget: 20.00m,
        MaxCriterionRetries: 2,
        PerProviderBudgetCaps: null,
        AutoPromoteRiskThreshold: ChangeRiskTier.Broad,
        TransitionMap: BuildUniformMap(ConductorTransitionDecision.Auto));

    // Escalate everything; no autonomous transitions regardless of risk or budget.
    public static ConductorAutonomyPolicy Manual { get; } = new(
        "Manual",
        MaxConcurrentPaidWorkers: 1,
        MaxTotalBudget: 2.00m,
        MaxCriterionRetries: 0,
        PerProviderBudgetCaps: null,
        AutoPromoteRiskThreshold: null,
        TransitionMap: BuildUniformMap(ConductorTransitionDecision.Escalate));

    public static IReadOnlyList<ConductorAutonomyPolicy> All { get; } = [Conservative, Permissive, Manual];

    // Returns the effective conductor decision for a lifecycle state transition.
    // At Merged: if the base decision is Escalate and changeRisk is provided and is within
    // the AutoPromoteRiskThreshold, upgrades to Auto (risk-gated auto-promotion).
    // AutoPromoteRiskThreshold=null (Manual) never upgrades.
    public ConductorTransitionDecision GetTransitionDecision(
        GoalLifecycleState state,
        ChangeRiskTier? changeRisk = null)
    {
        var baseDecision = TransitionMap[state];

        if (state == GoalLifecycleState.Merged
            && baseDecision == ConductorTransitionDecision.Escalate
            && changeRisk.HasValue
            && AutoPromoteRiskThreshold.HasValue
            && changeRisk.Value <= AutoPromoteRiskThreshold.Value)
        {
            return ConductorTransitionDecision.Auto;
        }

        return baseDecision;
    }

    // Returns all validation errors. Empty list means valid.
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (MaxConcurrentPaidWorkers <= 0)
            errors.Add($"maxConcurrentPaidWorkers must be greater than zero (got {MaxConcurrentPaidWorkers}).");

        if (MaxTotalBudget <= 0)
            errors.Add($"maxTotalBudget must be greater than zero (got {MaxTotalBudget}).");

        if (MaxCriterionRetries < 0)
            errors.Add($"maxCriterionRetries must be zero or greater (got {MaxCriterionRetries}).");

        if (PerProviderBudgetCaps is not null)
        {
            foreach (var (provider, cap) in PerProviderBudgetCaps)
            {
                if (cap <= 0)
                    errors.Add($"perProviderBudgetCaps[\"{provider}\"] must be greater than zero (got {cap}).");
                else if (cap > MaxTotalBudget)
                    errors.Add($"perProviderBudgetCaps[\"{provider}\"] ({cap}) exceeds maxTotalBudget ({MaxTotalBudget}).");
            }
        }

        foreach (var state in AllStates)
        {
            if (!TransitionMap.ContainsKey(state))
                errors.Add($"transitionMap is missing required lifecycle state '{state}'.");
        }

        return errors;
    }

    // Serializes this policy to JSON suitable for storing in .orchestrator/conductor-policy.json.
    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"name\": {JsonStr(Name)},");
        sb.AppendLine($"  \"maxConcurrentPaidWorkers\": {MaxConcurrentPaidWorkers},");
        sb.AppendLine($"  \"maxTotalBudget\": {MaxTotalBudget},");
        sb.AppendLine($"  \"maxCriterionRetries\": {MaxCriterionRetries},");

        if (PerProviderBudgetCaps is { Count: > 0 })
        {
            sb.AppendLine("  \"perProviderBudgetCaps\": {");
            var caps = PerProviderBudgetCaps.ToArray();
            for (var i = 0; i < caps.Length; i++)
            {
                var comma = i < caps.Length - 1 ? "," : "";
                sb.AppendLine($"    {JsonStr(caps[i].Key)}: {caps[i].Value}{comma}");
            }
            sb.AppendLine("  },");
        }
        else
        {
            sb.AppendLine("  \"perProviderBudgetCaps\": null,");
        }

        sb.AppendLine(AutoPromoteRiskThreshold.HasValue
            ? $"  \"autoPromoteRiskThreshold\": {JsonStr(AutoPromoteRiskThreshold.Value.ToString())},"
            : "  \"autoPromoteRiskThreshold\": null,");

        sb.AppendLine("  \"transitionMap\": {");
        var entries = TransitionMap.ToArray();
        for (var i = 0; i < entries.Length; i++)
        {
            var comma = i < entries.Length - 1 ? "," : "";
            sb.AppendLine($"    {JsonStr(entries[i].Key.ToString())}: {JsonStr(entries[i].Value.ToString())}{comma}");
        }
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // Parses and validates a conductor-policy.json string.
    // Throws FormatException with a clear message on any parse or validation failure.
    // sourcePath is used in error messages to aid operator debugging.
    public static ConductorAutonomyPolicy ParseJson(string json, string? sourcePath = null)
    {
        var src = sourcePath is not null ? $" (from '{sourcePath}')" : "";

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, JsonParseOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"conductor-policy.json{src} is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;

            var name = RequireString(root, "name", src) ?? "custom";
            var maxWorkers = RequireInt(root, "maxConcurrentPaidWorkers", src);
            var maxBudget = RequireDecimal(root, "maxTotalBudget", src);
            var maxCriterionRetries = root.TryGetProperty("maxCriterionRetries", out _)
                ? RequireInt(root, "maxCriterionRetries", src)
                : 1;

            IReadOnlyDictionary<string, decimal>? providerCaps = null;
            if (root.TryGetProperty("perProviderBudgetCaps", out var capsEl)
                && capsEl.ValueKind == JsonValueKind.Object)
            {
                var caps = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in capsEl.EnumerateObject())
                {
                    if (!prop.Value.TryGetDecimal(out var cap))
                        throw new FormatException(
                            $"conductor-policy.json{src}: perProviderBudgetCaps[\"{prop.Name}\"] must be a number.");
                    caps[prop.Name] = cap;
                }
                providerCaps = caps;
            }

            ChangeRiskTier? riskThreshold = null;
            if (root.TryGetProperty("autoPromoteRiskThreshold", out var thresholdEl)
                && thresholdEl.ValueKind != JsonValueKind.Null)
            {
                var thresholdStr = thresholdEl.GetString()
                    ?? throw new FormatException(
                        $"conductor-policy.json{src}: autoPromoteRiskThreshold must be a string or null.");
                if (!Enum.TryParse<ChangeRiskTier>(thresholdStr, ignoreCase: true, out var parsed))
                    throw new FormatException(
                        $"conductor-policy.json{src}: autoPromoteRiskThreshold '{thresholdStr}' is not valid. " +
                        $"Valid values: {string.Join(", ", Enum.GetNames<ChangeRiskTier>())}.");
                riskThreshold = parsed;
            }

            if (!root.TryGetProperty("transitionMap", out var mapEl)
                || mapEl.ValueKind != JsonValueKind.Object)
                throw new FormatException(
                    $"conductor-policy.json{src}: transitionMap is required and must be an object.");

            var transitionMap = new Dictionary<GoalLifecycleState, ConductorTransitionDecision>();
            foreach (var prop in mapEl.EnumerateObject())
            {
                if (!Enum.TryParse<GoalLifecycleState>(prop.Name, ignoreCase: true, out var state))
                    throw new FormatException(
                        $"conductor-policy.json{src}: transitionMap key '{prop.Name}' is not a valid lifecycle state. " +
                        $"Valid states: {string.Join(", ", Enum.GetNames<GoalLifecycleState>())}.");
                var decisionStr = prop.Value.GetString();
                if (!Enum.TryParse<ConductorTransitionDecision>(decisionStr, ignoreCase: true, out var decision))
                    throw new FormatException(
                        $"conductor-policy.json{src}: transitionMap[\"{prop.Name}\"] value '{decisionStr}' " +
                        $"must be 'Auto' or 'Escalate'.");
                transitionMap[state] = decision;
            }

            var policy = new ConductorAutonomyPolicy(
                name, maxWorkers, maxBudget, maxCriterionRetries, providerCaps, riskThreshold, transitionMap);

            var errors = policy.Validate();
            if (errors.Count > 0)
                throw new FormatException(
                    $"conductor-policy.json{src} failed validation:\n" +
                    string.Join("\n", errors.Select(e => $"  - {e}")));

            return policy;
        }
    }

    // Loads from .orchestrator/conductor-policy.json under rootDirectory.
    // Returns Conservative (default) when the file does not exist.
    // Throws FormatException with the file path in the message on parse or validation failure.
    public static ConductorAutonomyPolicy LoadFromOrchestratorDirectory(string rootDirectory)
    {
        var path = Path.Combine(rootDirectory, ".orchestrator", "conductor-policy.json");
        if (!File.Exists(path))
            return Conservative;
        var json = File.ReadAllText(path, Encoding.UTF8);
        return ParseJson(json, path);
    }

    private static Dictionary<GoalLifecycleState, ConductorTransitionDecision> BuildUniformMap(
        ConductorTransitionDecision decision) =>
        AllStates.ToDictionary(s => s, _ => decision);

    private static string JsonStr(string value) => JsonSerializer.Serialize(value);

    private static string? RequireString(JsonElement root, string property, string src)
    {
        if (!root.TryGetProperty(property, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.String)
            throw new FormatException($"conductor-policy.json{src}: {property} must be a string.");
        return el.GetString();
    }

    private static int RequireInt(JsonElement root, string property, string src)
    {
        if (!root.TryGetProperty(property, out var el) || !el.TryGetInt32(out var value))
            throw new FormatException(
                $"conductor-policy.json{src}: {property} is required and must be an integer.");
        return value;
    }

    private static decimal RequireDecimal(JsonElement root, string property, string src)
    {
        if (!root.TryGetProperty(property, out var el) || !el.TryGetDecimal(out var value))
            throw new FormatException(
                $"conductor-policy.json{src}: {property} is required and must be a number.");
        return value;
    }
}
