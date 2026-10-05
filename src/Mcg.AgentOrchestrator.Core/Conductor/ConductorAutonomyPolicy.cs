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

// Immutable autonomy envelope for the upcoming conductor. Controls concurrency, retry limits,
// risk-gated auto-promotion at Merged, and per-lifecycle-state Auto/Escalate decisions.
// Load from .orchestrator/conductor-policy.json or use a named preset.
public sealed record ConductorAutonomyPolicy(
    string Name,
    int MaxConcurrentPaidWorkers,
    int MaxCriterionRetries,
    ChangeRiskTier? AutoPromoteRiskThreshold,
    IReadOnlyDictionary<GoalLifecycleState, ConductorTransitionDecision> TransitionMap,
    int MaxEmptyOutputDispatchRetries = 8,
    int MaxEmptyOutputAutoRecoverCycles = 3,
    double EmptyOutputRetryInitialDelaySeconds = 0,
    double EmptyOutputRetryBackoffMultiplier = 2,
    double EmptyOutputRetryMaxDelaySeconds = 30,
    int ReviewAutoRetryWarningRound = 4,
    int ReviewAutoRetryStopRound = 7,
    int ReviewAutoRetryLifetimeMultiplier = 3,
    int PlannerSampleCount = 1,
    int AcceptanceWidth = 2,
    int AcceptanceCohortGatherWindowSeconds = 480,
    bool AcceptanceAttemptBelowNormalPriority = true,
    ConductorBoardFillMode BoardFillMode = ConductorBoardFillMode.Shadow,
    int BoardFillTargetActiveGoals = 10,
    int BoardFillMaxDraftsPerDay = 3,
    bool CascadeTesterCheapFirst = true,
    string CascadeCheapModelAlias = ConductorAutonomyPolicy.DefaultCascadeCheapModelAlias)
{
    public const string DefaultCascadeCheapModelAlias = "gpt-6-luna";
    public const int DefaultAcceptanceCohortGatherWindowSeconds = 480;
    public const int MinimumAcceptanceWidth = 1;
    public const int MaximumAcceptanceWidth = 4;

    private static readonly GoalLifecycleState[] AllStates =
        Enum.GetValues<GoalLifecycleState>();

    private static readonly JsonDocumentOptions JsonParseOptions = new() { CommentHandling = JsonCommentHandling.Skip };

    public static ConductorAutonomyPolicy Default => Conservative;

    // Auto the mechanical transitions; escalate Merged→main unless the change is low-risk (DocsOnly);
    // escalate on failures, blocks, and human-input waits.
    public static ConductorAutonomyPolicy Conservative { get; } = new(
        "Conservative",
        MaxConcurrentPaidWorkers: 4,
        MaxCriterionRetries: 1,
        AutoPromoteRiskThreshold: ChangeRiskTier.DocsOnly,
        TransitionMap: new Dictionary<GoalLifecycleState, ConductorTransitionDecision>
        {
            [GoalLifecycleState.Created] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.AwaitingClarification] = ConductorTransitionDecision.Escalate,
            [GoalLifecycleState.WorkspaceReady] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.Dispatched] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.Running] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.AwaitingVerification] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.Verifying] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.Verified] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.AcceptanceFailed] = ConductorTransitionDecision.Escalate,
            [GoalLifecycleState.Merged] = ConductorTransitionDecision.Escalate,
            [GoalLifecycleState.Recorded] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.CleanedUp] = ConductorTransitionDecision.Auto,
            [GoalLifecycleState.Failed] = ConductorTransitionDecision.Escalate,
            [GoalLifecycleState.Blocked] = ConductorTransitionDecision.Escalate,
            [GoalLifecycleState.AwaitingHumanInput] = ConductorTransitionDecision.Escalate,
        },
        PlannerSampleCount: 1);

    // Auto everything within caps. AutoPromoteRiskThreshold = Broad so all change types
    // get auto-promoted at Merged when risk is known.
    public static ConductorAutonomyPolicy Permissive { get; } = new(
        "Permissive",
        MaxConcurrentPaidWorkers: 5,
        MaxCriterionRetries: 2,
        AutoPromoteRiskThreshold: ChangeRiskTier.Broad,
        TransitionMap: BuildUniformMap(ConductorTransitionDecision.Auto),
        PlannerSampleCount: 1);

    // Escalate everything; no autonomous transitions regardless of risk.
    public static ConductorAutonomyPolicy Manual { get; } = new(
        "Manual",
        MaxConcurrentPaidWorkers: 1,
        MaxCriterionRetries: 0,
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

    // Whether the conductor may auto-approve dispatch of tasks whose write-set touches a high-risk
    // ownership area (scripts, shared infrastructure, build system, configuration,
    // skills) without human sign-off. Tied to the most permissive auto-promote envelope (Broad), so
    // Conservative (DocsOnly) and Manual (null) still require explicit operator approval. Without
    // this, any goal touching those areas escalates at WorkspaceReady and cannot run unattended.
    public bool AllowsAutonomousHighRiskOwnership =>
        AutoPromoteRiskThreshold == ChangeRiskTier.Broad;

    // Returns all validation errors. Empty list means valid.
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (MaxConcurrentPaidWorkers <= 0)
            errors.Add($"maxConcurrentPaidWorkers must be greater than zero (got {MaxConcurrentPaidWorkers}).");

        if (MaxCriterionRetries < 0)
            errors.Add($"maxCriterionRetries must be zero or greater (got {MaxCriterionRetries}).");

        if (MaxEmptyOutputDispatchRetries <= 0)
            errors.Add($"maxEmptyOutputDispatchRetries must be greater than zero (got {MaxEmptyOutputDispatchRetries}).");

        if (MaxEmptyOutputAutoRecoverCycles <= 0)
            errors.Add($"maxEmptyOutputAutoRecoverCycles must be greater than zero (got {MaxEmptyOutputAutoRecoverCycles}).");

        if (EmptyOutputRetryInitialDelaySeconds < 0)
            errors.Add($"emptyOutputRetryInitialDelaySeconds must be zero or greater (got {EmptyOutputRetryInitialDelaySeconds}).");

        if (EmptyOutputRetryBackoffMultiplier < 1)
            errors.Add($"emptyOutputRetryBackoffMultiplier must be at least 1 (got {EmptyOutputRetryBackoffMultiplier}).");

        if (EmptyOutputRetryMaxDelaySeconds < 0)
            errors.Add($"emptyOutputRetryMaxDelaySeconds must be zero or greater (got {EmptyOutputRetryMaxDelaySeconds}).");

        if (ReviewAutoRetryWarningRound <= 0)
            errors.Add($"reviewAutoRetryWarningRound must be greater than zero (got {ReviewAutoRetryWarningRound}).");

        if (ReviewAutoRetryStopRound <= 0)
            errors.Add($"reviewAutoRetryStopRound must be greater than zero (got {ReviewAutoRetryStopRound}).");

        if (ReviewAutoRetryLifetimeMultiplier < 2)
            errors.Add($"reviewAutoRetryLifetimeMultiplier must be at least two (got {ReviewAutoRetryLifetimeMultiplier}).");

        if (ReviewAutoRetryStopRound <= ReviewAutoRetryWarningRound)
            errors.Add($"reviewAutoRetryStopRound ({ReviewAutoRetryStopRound}) must be greater than reviewAutoRetryWarningRound ({ReviewAutoRetryWarningRound}).");

        if (PlannerSampleCount is < PlannerSamplingPolicy.MinimumSampleCount or > PlannerSamplingPolicy.MaximumSampleCount)
            errors.Add($"plannerSampleCount must be between {PlannerSamplingPolicy.MinimumSampleCount} and {PlannerSamplingPolicy.MaximumSampleCount} (got {PlannerSampleCount}).");

        if (AcceptanceWidth is < MinimumAcceptanceWidth or > MaximumAcceptanceWidth)
            errors.Add($"acceptanceWidth must be between {MinimumAcceptanceWidth} and {MaximumAcceptanceWidth} (got {AcceptanceWidth}).");

        if (AcceptanceCohortGatherWindowSeconds < 0)
            errors.Add($"acceptanceCohortGatherWindowSeconds must be zero or greater (got {AcceptanceCohortGatherWindowSeconds}).");

        if (string.IsNullOrWhiteSpace(CascadeCheapModelAlias))
            errors.Add("cascadeCheapModelAlias must not be empty.");
        if (!Enum.IsDefined(BoardFillMode))
            errors.Add($"boardFillMode must be Off, Shadow or File (got {BoardFillMode}).");
        if (BoardFillTargetActiveGoals is < 0 or > 30)
            errors.Add($"boardFillTargetActiveGoals must be between 0 and 30 (got {BoardFillTargetActiveGoals}).");
        if (BoardFillMaxDraftsPerDay is < 0 or > 20)
            errors.Add($"boardFillMaxDraftsPerDay must be between 0 and 20 (got {BoardFillMaxDraftsPerDay}).");

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
        sb.AppendLine($"  \"maxCriterionRetries\": {MaxCriterionRetries},");
        sb.AppendLine($"  \"maxEmptyOutputDispatchRetries\": {MaxEmptyOutputDispatchRetries},");
        sb.AppendLine($"  \"maxEmptyOutputAutoRecoverCycles\": {MaxEmptyOutputAutoRecoverCycles},");
        sb.AppendLine($"  \"emptyOutputRetryInitialDelaySeconds\": {EmptyOutputRetryInitialDelaySeconds},");
        sb.AppendLine($"  \"emptyOutputRetryBackoffMultiplier\": {EmptyOutputRetryBackoffMultiplier},");
        sb.AppendLine($"  \"emptyOutputRetryMaxDelaySeconds\": {EmptyOutputRetryMaxDelaySeconds},");
        sb.AppendLine($"  \"reviewAutoRetryWarningRound\": {ReviewAutoRetryWarningRound},");
        sb.AppendLine($"  \"reviewAutoRetryStopRound\": {ReviewAutoRetryStopRound},");
        sb.AppendLine($"  \"reviewAutoRetryLifetimeMultiplier\": {ReviewAutoRetryLifetimeMultiplier},");
        sb.AppendLine($"  \"plannerSampleCount\": {PlannerSampleCount},");
        sb.AppendLine($"  \"acceptanceWidth\": {AcceptanceWidth},");
        sb.AppendLine($"  \"acceptanceCohortGatherWindowSeconds\": {AcceptanceCohortGatherWindowSeconds},");
        sb.AppendLine($"  \"acceptanceAttemptBelowNormalPriority\": {AcceptanceAttemptBelowNormalPriority.ToString().ToLowerInvariant()},");
        sb.AppendLine($"  \"boardFillMode\": {JsonStr(BoardFillMode.ToString())},");
        sb.AppendLine($"  \"boardFillTargetActiveGoals\": {BoardFillTargetActiveGoals},");
        sb.AppendLine($"  \"boardFillMaxDraftsPerDay\": {BoardFillMaxDraftsPerDay},");
        sb.AppendLine($"  \"cascadeTesterCheapFirst\": {CascadeTesterCheapFirst.ToString().ToLowerInvariant()},");
        sb.AppendLine($"  \"cascadeCheapModelAlias\": {JsonStr(CascadeCheapModelAlias)},");

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

            var name = RequireString(root, "name", src);
            if (string.IsNullOrWhiteSpace(name))
                throw new FormatException(
                    $"conductor-policy.json{src}: name is required and must be a non-empty string.");
            var maxWorkers = RequireInt(root, "maxConcurrentPaidWorkers", src);
            var maxCriterionRetries = root.TryGetProperty("maxCriterionRetries", out _)
                ? RequireInt(root, "maxCriterionRetries", src)
                : 1;
            var maxEmptyOutputDispatchRetries = root.TryGetProperty("maxEmptyOutputDispatchRetries", out _)
                ? RequireInt(root, "maxEmptyOutputDispatchRetries", src)
                : 8;
            var maxEmptyOutputAutoRecoverCycles = root.TryGetProperty("maxEmptyOutputAutoRecoverCycles", out _)
                ? RequireInt(root, "maxEmptyOutputAutoRecoverCycles", src)
                : 3;
            var emptyOutputRetryInitialDelaySeconds = root.TryGetProperty("emptyOutputRetryInitialDelaySeconds", out _)
                ? RequireDouble(root, "emptyOutputRetryInitialDelaySeconds", src)
                : 0;
            var emptyOutputRetryBackoffMultiplier = root.TryGetProperty("emptyOutputRetryBackoffMultiplier", out _)
                ? RequireDouble(root, "emptyOutputRetryBackoffMultiplier", src)
                : 2;
            var emptyOutputRetryMaxDelaySeconds = root.TryGetProperty("emptyOutputRetryMaxDelaySeconds", out _)
                ? RequireDouble(root, "emptyOutputRetryMaxDelaySeconds", src)
                : 30;
            var reviewAutoRetryWarningRound = root.TryGetProperty("reviewAutoRetryWarningRound", out _)
                ? RequireInt(root, "reviewAutoRetryWarningRound", src)
                : 4;
            var reviewAutoRetryStopRound = root.TryGetProperty("reviewAutoRetryStopRound", out _)
                ? RequireInt(root, "reviewAutoRetryStopRound", src)
                : 7;
            var reviewAutoRetryLifetimeMultiplier = root.TryGetProperty("reviewAutoRetryLifetimeMultiplier", out _)
                ? RequireInt(root, "reviewAutoRetryLifetimeMultiplier", src)
                : 3;
            var plannerSampleCount = root.TryGetProperty("plannerSampleCount", out _)
                ? RequireInt(root, "plannerSampleCount", src)
                : 1;
            var acceptanceWidth = root.TryGetProperty("acceptanceWidth", out _)
                ? RequireInt(root, "acceptanceWidth", src)
                : 2;
            var gatherWindowSeconds = DefaultAcceptanceCohortGatherWindowSeconds;
            if (root.TryGetProperty("acceptanceCohortGatherWindowSeconds", out var gatherWindowElement) &&
                gatherWindowElement.ValueKind != JsonValueKind.Null)
            {
                if (gatherWindowElement.ValueKind != JsonValueKind.Number)
                    throw new FormatException(
                        $"conductor-policy.json{src}: acceptanceCohortGatherWindowSeconds must be an integer.");
                gatherWindowSeconds = Math.Max(0, RequireInt(root, "acceptanceCohortGatherWindowSeconds", src));
            }
            var belowNormalPriority = true;
            if (root.TryGetProperty("acceptanceAttemptBelowNormalPriority", out var priorityElement) &&
                priorityElement.ValueKind != JsonValueKind.Null)
            {
                if (priorityElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new FormatException(
                        $"conductor-policy.json{src}: acceptanceAttemptBelowNormalPriority must be a boolean.");
                belowNormalPriority = priorityElement.GetBoolean();
            }
            var cascadeTesterCheapFirst = true;
            if (root.TryGetProperty("cascadeTesterCheapFirst", out var cascadeSwitch) &&
                cascadeSwitch.ValueKind != JsonValueKind.Null)
            {
                if (cascadeSwitch.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new FormatException($"conductor-policy.json{src}: cascadeTesterCheapFirst must be a boolean.");
                cascadeTesterCheapFirst = cascadeSwitch.GetBoolean();
            }
            var cascadeCheapModelAlias = DefaultCascadeCheapModelAlias;
            if (root.TryGetProperty("cascadeCheapModelAlias", out var cascadeAlias) &&
                cascadeAlias.ValueKind != JsonValueKind.Null)
            {
                if (cascadeAlias.ValueKind != JsonValueKind.String)
                    throw new FormatException($"conductor-policy.json{src}: cascadeCheapModelAlias must be a string.");
                cascadeCheapModelAlias = cascadeAlias.GetString()!;
            }
            var boardFillMode = ConductorBoardFillMode.Shadow;
            if (root.TryGetProperty("boardFillMode", out var boardFillElement) &&
                boardFillElement.ValueKind != JsonValueKind.Null)
            {
                if (boardFillElement.ValueKind != JsonValueKind.String ||
                    !Enum.TryParse(boardFillElement.GetString(), ignoreCase: true, out boardFillMode) ||
                    !Enum.IsDefined(boardFillMode))
                    throw new FormatException(
                        $"conductor-policy.json{src}: boardFillMode must be Off, Shadow or File.");
            }
            var boardFillTarget = root.TryGetProperty("boardFillTargetActiveGoals", out _)
                ? RequireInt(root, "boardFillTargetActiveGoals", src) : 10;
            var boardFillCap = root.TryGetProperty("boardFillMaxDraftsPerDay", out _)
                ? RequireInt(root, "boardFillMaxDraftsPerDay", src) : 3;
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
                name,
                maxWorkers,
                maxCriterionRetries,
                riskThreshold,
                transitionMap,
                maxEmptyOutputDispatchRetries,
                maxEmptyOutputAutoRecoverCycles,
                emptyOutputRetryInitialDelaySeconds,
                emptyOutputRetryBackoffMultiplier,
                emptyOutputRetryMaxDelaySeconds,
                reviewAutoRetryWarningRound,
                reviewAutoRetryStopRound,
                reviewAutoRetryLifetimeMultiplier,
                plannerSampleCount,
                acceptanceWidth,
                gatherWindowSeconds,
                belowNormalPriority,
                boardFillMode,
                boardFillTarget,
                boardFillCap,
                cascadeTesterCheapFirst,
                cascadeCheapModelAlias);

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
    // Throws with the file path in the message when an existing file cannot be read, parsed, or validated.
    public static ConductorAutonomyPolicy LoadFromOrchestratorDirectory(string rootDirectory)
        => LoadFromOrchestratorDirectory(
            new DirectoryInfo(Path.Combine(rootDirectory, ".orchestrator")));

    // Loads from conductor-policy.json under an already-resolved (and potentially project/tenant-scoped)
    // orchestrator directory.
    public static ConductorAutonomyPolicy LoadFromOrchestratorDirectory(DirectoryInfo orchestratorDirectory)
    {
        var path = Path.GetFullPath(Path.Combine(orchestratorDirectory.FullName, "conductor-policy.json"));
        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (FileNotFoundException)
        {
            return Conservative;
        }
        catch (DirectoryNotFoundException)
        {
            return Conservative;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Unable to read conductor policy file '{path}': {ex.Message}", ex);
        }

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

    private static double RequireDouble(JsonElement root, string property, string src)
    {
        if (!root.TryGetProperty(property, out var el) || !el.TryGetDouble(out var value))
            throw new FormatException(
                $"conductor-policy.json{src}: {property} is required and must be a number.");
        return value;
    }
}
