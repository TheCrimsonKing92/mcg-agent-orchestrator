using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A semantic-acceptance judge: given the change inputs, returns a verdict on whether the diff
// satisfies the objective. Implementations wrap a model (local Ollama via the provider registry
// now; a parallel subscription judge is the next increment, for local-vs-paid comparison).
internal interface ISemanticJudge
{
    string Name { get; }

    // null means use the per-judge default passed to EvaluateAsync; non-null overrides it for slow lanes.
    TimeSpan? JudgeTimeout { get; }

    // false for slow-lane CLI judges (SubscriptionCliSemanticJudge): N cold process launches per file
    // would exceed the 180s budget. Fast local judges (ModelRegistrySemanticJudge) default to true.
    bool SupportsFanOut => true;

    Task<SemanticAcceptanceVerdict> JudgeAsync(SemanticAcceptanceInputs inputs, CancellationToken cancellationToken);
}

internal sealed record JudgeVerdict(string Judge, SemanticAcceptanceVerdict Verdict);

internal sealed record SemanticAcceptanceReport(IReadOnlyList<JudgeVerdict> Verdicts)
{
    public bool RanAnyJudge => Verdicts.Count > 0;

    public IReadOnlyList<JudgeVerdict> ValidVerdicts =>
        Verdicts.Where(verdict => verdict.Verdict.IsValid).ToList();

    // Null when no judge returned a valid verdict, or when valid judges disagree on the outcome.
    // Used by the (future) blocking flip and the local-vs-subscription agreement comparison.
    public bool? Consensus
    {
        get
        {
            var valid = ValidVerdicts;
            if (valid.Count == 0)
            {
                return null;
            }

            var first = valid[0].Verdict.CriteriaMet;
            return valid.All(verdict => verdict.Verdict.CriteriaMet == first) ? first : null;
        }
    }

    public bool AllValidJudgesAgree => ValidVerdicts.Count > 1 && Consensus is not null;
}

// Local-model semantic judge over the provider registry (e.g. Ollama). Text-only completion — the
// judge needs no file access, just the prompt-rendered evidence.
internal sealed class ModelRegistrySemanticJudge : ISemanticJudge
{
    private readonly IModelProviderRegistry _providers;
    private readonly string _providerName;
    private readonly string _modelName;

    public ModelRegistrySemanticJudge(IModelProviderRegistry providers, string providerName, string modelName)
    {
        _providers = providers;
        _providerName = providerName;
        _modelName = modelName;
    }

    public string Name => $"{_providerName.ToLowerInvariant()}:{_modelName}";

    public TimeSpan? JudgeTimeout => null;

    public async Task<SemanticAcceptanceVerdict> JudgeAsync(
        SemanticAcceptanceInputs inputs,
        CancellationToken cancellationToken)
    {
        var prompt = SemanticAcceptancePlanner.BuildPrompt(SemanticAcceptancePlanner.BuildEvidenceContext(inputs));
        var provider = _providers.GetRequired(_providerName);
        var request = new ModelRequest(
            "You are a strict, evidence-grounded software acceptance reviewer.",
            [new ModelMessage("user", prompt)],
            new ModelOptions(Temperature: 0.0, MaxOutputTokens: 800, ModelName: _modelName));

        var response = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        return SemanticAcceptancePlanner.Parse(response.Text);
    }
}

// Semantic judge that runs via the subscription CLI (claude-cli / codex-cli) rather than the paid
// API. Builds the evidence prompt, writes it to a temp file, substitutes placeholders in the worker
// profile's CommandTemplate with completion-specific read-only/direct-output placeholders, runs PowerShell synchronously
// capturing stdout, and parses the fenced JSON verdict. ADVISORY: CLI failures, timeouts, and parse
// errors become invalid verdicts and never propagate to the merge gate.
internal sealed class SubscriptionCliSemanticJudge : ISemanticJudge
{
    private readonly string _profileName;
    private readonly string _modelAlias;
    private readonly SubscriptionCliCompleter _completer;

    public SubscriptionCliSemanticJudge(
        WorkerProfileCatalog profiles,
        string profileName,
        string modelAlias,
        string? reasoningEffort = null)
        : this(profiles.GetRequired(profileName).CommandTemplate, profileName, modelAlias, reasoningEffort, RunCommandAsync)
    {
    }

    internal SubscriptionCliSemanticJudge(
        string commandTemplate,
        string profileName,
        string modelAlias,
        string? reasoningEffort,
        Func<string, string, CancellationToken, Task<string>> runner)
    {
        _profileName = profileName;
        _modelAlias = modelAlias;
        _completer = new SubscriptionCliCompleter(commandTemplate, profileName, modelAlias, reasoningEffort, runner);
    }

    public string Name => $"sub:{_profileName}:{_modelAlias}";

    public TimeSpan? JudgeTimeout => TimeSpan.FromSeconds(180);

    // CLI judges must never fan out per-file: each call is a cold process launch that can easily
    // consume the entire 180s budget across N files. Judge the whole diff in a single call instead.
    public bool SupportsFanOut => false;

    public async Task<SemanticAcceptanceVerdict> JudgeAsync(
        SemanticAcceptanceInputs inputs,
        CancellationToken cancellationToken)
    {
        var prompt = SemanticAcceptancePlanner.BuildPrompt(SemanticAcceptancePlanner.BuildEvidenceContext(inputs));
        try
        {
            var stdout = await _completer.CompleteAsync(prompt, "judge-prompt.md", cancellationToken).ConfigureAwait(false);
            return SemanticAcceptancePlanner.Parse(stdout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SemanticAcceptanceVerdict.Invalid($"SubscriptionCliSemanticJudge '{_profileName}': {ex.Message}");
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string command, string workingDirectory) =>
        SubscriptionCliCompleter.BuildStartInfo(command, workingDirectory);

    internal static async Task<string> RunCommandAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken) =>
        await SubscriptionCliCompleter.RunCommandAsync(command, workingDirectory, cancellationToken).ConfigureAwait(false);
}

// Decorator that runs a leaf judge once per changed file rather than once over the whole diff,
// fixing the 6KB truncation blind spot for large changes. Falls back to whole-diff judging when
// PerFileDiffs is empty, all files are whitespace-only, or the substantive file count exceeds the
// cap (prevents excessive leaf invocations on very large changes).
internal sealed class RecursivePerFileSemanticJudge : ISemanticJudge
{
    internal const int MaxPerFileJudgeCalls = 10;
    internal const int MaxPerFileDiffChars = 4000;

    private readonly ISemanticJudge _leaf;

    public RecursivePerFileSemanticJudge(ISemanticJudge leaf) => _leaf = leaf;

    public string Name => $"recursive({_leaf.Name})";

    public TimeSpan? JudgeTimeout => _leaf.JudgeTimeout;

    public async Task<SemanticAcceptanceVerdict> JudgeAsync(
        SemanticAcceptanceInputs inputs,
        CancellationToken cancellationToken)
    {
        var perFileDiffs = inputs.PerFileDiffs;
        if (!_leaf.SupportsFanOut || perFileDiffs is null or { Count: 0 })
        {
            return await _leaf.JudgeAsync(inputs, cancellationToken).ConfigureAwait(false);
        }

        // Skip whitespace-only files — they carry no semantic signal.
        var substantive = perFileDiffs.Where(f => !IsWhitespaceOnly(f.Diff)).ToList();

        if (substantive.Count == 0)
        {
            return await _leaf.JudgeAsync(inputs, cancellationToken).ConfigureAwait(false);
        }

        var perFileVerdicts = await Task.WhenAll(
            substantive.Select(file => JudgeFileAsync(file, inputs, cancellationToken)))
            .ConfigureAwait(false);

        return Aggregate(perFileVerdicts);
    }

    private async Task<PerFileJudgeVerdict> JudgeFileAsync(
        (string File, string Diff) file,
        SemanticAcceptanceInputs inputs,
        CancellationToken cancellationToken)
    {
        try
        {
            var verdict = await _leaf.JudgeAsync(
                inputs with
                {
                    DiffExcerpt = BudgetDiff(file.Diff),
                    ChangedFiles = [file.File],
                    PerFileDiffs = null
                },
                cancellationToken).ConfigureAwait(false);
            return new PerFileJudgeVerdict(file.File, verdict);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PerFileJudgeVerdict(
                file.File,
                SemanticAcceptanceVerdict.Invalid($"Per-file judge failed for {file.File}: {ex.Message}"));
        }
    }

    private static string BudgetDiff(string diff)
    {
        if (diff.Length <= MaxPerFileDiffChars)
        {
            return diff;
        }

        return diff[..MaxPerFileDiffChars] +
            $"{Environment.NewLine}...(per-file judge diff summarized at {MaxPerFileDiffChars} chars; oversized single-file change still judged)";
    }

    private static bool IsWhitespaceOnly(string diff)
    {
        foreach (var line in diff.Split('\n'))
        {
            if ((line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)) ||
                (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)))
            {
                if (!string.IsNullOrWhiteSpace(line[1..]))
                    return false;
            }
        }
        return true;
    }

    private static SemanticAcceptanceVerdict Aggregate(PerFileJudgeVerdict[] verdicts)
    {
        var valid = verdicts.Where(v => v.Verdict.IsValid).ToList();
        var coverageReason = $"RecursivePerFileSemanticJudge coverage: {valid.Count} of {verdicts.Length} files judged.";
        var reasons = valid.SelectMany(v => v.Verdict.Reasons).Prepend(coverageReason).ToList();
        var unmet = valid.SelectMany(v => v.Verdict.UnmetCriteria).Distinct(StringComparer.Ordinal).ToList();
        var invalid = verdicts.Where(v => !v.Verdict.IsValid).ToList();
        foreach (var file in invalid)
        {
            var error = file.Verdict.ValidationErrors.Count == 0
                ? "judge produced no valid verdict"
                : string.Join("; ", file.Verdict.ValidationErrors);
            reasons.Add($"{file.File}: {error}");
            unmet.Add($"{file.File}: no valid per-file verdict");
        }

        if (valid.Count == 0)
        {
            unmet.Add("No substantive file produced a valid per-file verdict");
            return new SemanticAcceptanceVerdict(false, "low", reasons, unmet, []);
        }

        var criteriaMet = invalid.Count == 0 && valid.All(v => v.Verdict.CriteriaMet);
        var confidence = invalid.Count == 0 ? LowestConfidence(valid.Select(v => v.Verdict.Confidence)) : "low";
        return new SemanticAcceptanceVerdict(criteriaMet, confidence, reasons, unmet, []);
    }

    private static string LowestConfidence(IEnumerable<string> confidences)
    {
        static int Rank(string c) => c switch
        {
            "high" => 3,
            "medium" => 2,
            "low" => 1,
            _ => 0
        };

        return confidences.OrderBy(Rank).FirstOrDefault() ?? "unknown";
    }

    private sealed record PerFileJudgeVerdict(string File, SemanticAcceptanceVerdict Verdict);
}

internal static class SemanticAcceptanceEvaluator
{
    // Resolves the configured judge lanes from the orchestrator's model-function registry: one judge
    // per `acceptance-judge` binding, deduped so the same lane isn't judged twice. When a binding
    // carries a SubscriptionLaunchProfile and a WorkerProfileCatalog is provided, builds a
    // SubscriptionCliSemanticJudge; otherwise falls back to the API-based ModelRegistrySemanticJudge.
    // Returns empty when none is configured (semantic acceptance stays dormant). Judges are model-
    // function bindings, NOT worker agents — they never touch task routing or the SDLC role catalog.
    public static IReadOnlyList<ISemanticJudge> BuildJudges(
        ModelFunctionCatalog modelFunctions,
        IModelProviderRegistry providers,
        WorkerProfileCatalog? workerProfiles = null)
    {
        return modelFunctions
            .ForPurpose(ModelFunctionPurposes.AcceptanceJudge)
            .DistinctBy(
                binding => binding.Subscription is { } sub
                    ? $"sub:{sub.WorkerProfileName}:{sub.ModelAlias ?? string.Empty}"
                    : $"{binding.Model.ProviderName}/{binding.Model.ModelName}",
                StringComparer.OrdinalIgnoreCase)
            .Select(binding => (ISemanticJudge)(binding.Subscription is { } sub && workerProfiles is not null
                ? new SubscriptionCliSemanticJudge(workerProfiles, sub.WorkerProfileName, sub.ModelAlias ?? string.Empty, sub.ReasoningEffort)
                : new ModelRegistrySemanticJudge(providers, binding.Model.ProviderName, binding.Model.ModelName)))
            .ToList();
    }

    // Runs all judges IN PARALLEL, each with its own timeout. ADVISORY: a judge that throws, times
    // out, or returns an unparseable verdict is recorded as an invalid verdict and NEVER propagates
    // — semantic acceptance must not be able to break the deterministic merge gate while advisory.
    // Verdict order matches judge order (Task.WhenAll preserves it); aggregation is set-based so it
    // is order-independent regardless.
    public static async Task<SemanticAcceptanceReport> EvaluateAsync(
        IReadOnlyList<ISemanticJudge> judges,
        SemanticAcceptanceInputs inputs,
        TimeSpan perJudgeTimeout,
        CancellationToken cancellationToken = default)
    {
        var verdicts = await Task.WhenAll(
            judges.Select(judge => RunJudgeAsync(judge, inputs, perJudgeTimeout, cancellationToken)))
            .ConfigureAwait(false);

        return new SemanticAcceptanceReport(verdicts);
    }

    private static async Task<JudgeVerdict> RunJudgeAsync(
        ISemanticJudge judge,
        SemanticAcceptanceInputs inputs,
        TimeSpan perJudgeTimeout,
        CancellationToken cancellationToken)
    {
        var effectiveTimeout = judge.JudgeTimeout ?? perJudgeTimeout;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);
            var verdict = await judge.JudgeAsync(inputs, timeoutCts.Token).ConfigureAwait(false);
            return new JudgeVerdict(judge.Name, verdict);
        }
        catch (OperationCanceledException)
        {
            return new JudgeVerdict(judge.Name, SemanticAcceptanceVerdict.Invalid(
                $"Judge '{judge.Name}' timed out after {effectiveTimeout.TotalSeconds:F0}s."));
        }
        catch (Exception ex)
        {
            return new JudgeVerdict(judge.Name, SemanticAcceptanceVerdict.Invalid(
                $"Judge '{judge.Name}' failed: {ex.Message}"));
        }
    }
}
