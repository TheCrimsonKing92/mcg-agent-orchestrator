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
// profile's CommandTemplate (read-only sandbox, plan permission mode), runs PowerShell synchronously
// capturing stdout, and parses the fenced JSON verdict. ADVISORY: CLI failures, timeouts, and parse
// errors become invalid verdicts and never propagate to the merge gate.
internal sealed class SubscriptionCliSemanticJudge : ISemanticJudge
{
    private readonly string _profileName;
    private readonly string _modelAlias;
    private readonly string? _reasoningEffort;
    private readonly string _commandTemplate;
    private readonly Func<string, string, CancellationToken, Task<string>> _runner;

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
        _commandTemplate = commandTemplate;
        _profileName = profileName;
        _modelAlias = modelAlias;
        _reasoningEffort = reasoningEffort;
        _runner = runner;
    }

    public string Name => $"sub:{_profileName}:{_modelAlias}";

    public TimeSpan? JudgeTimeout => TimeSpan.FromSeconds(180);

    public async Task<SemanticAcceptanceVerdict> JudgeAsync(
        SemanticAcceptanceInputs inputs,
        CancellationToken cancellationToken)
    {
        var prompt = SemanticAcceptancePlanner.BuildPrompt(SemanticAcceptancePlanner.BuildEvidenceContext(inputs));
        var tempDir = Path.Combine(Path.GetTempPath(), $"mcg-judge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var promptPath = Path.Combine(tempDir, "judge-prompt.md");
        try
        {
            await File.WriteAllTextAsync(promptPath, prompt, cancellationToken).ConfigureAwait(false);
            var command = SubstitutePlaceholders(_commandTemplate, promptPath, _modelAlias, _reasoningEffort, tempDir);
            var stdout = await _runner(command, tempDir, cancellationToken).ConfigureAwait(false);
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
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static string SubstitutePlaceholders(
        string template,
        string promptPath,
        string modelAlias,
        string? reasoningEffort,
        string workingDirectory)
    {
        return template
            .Replace("{promptPath}", Quote(promptPath), StringComparison.OrdinalIgnoreCase)
            .Replace("{subscriptionModelName}", Quote(modelAlias), StringComparison.OrdinalIgnoreCase)
            .Replace("{subscriptionReasoningEffort}", Quote(string.IsNullOrWhiteSpace(reasoningEffort) ? AgentCatalog.ComplexReasoningEffort : reasoningEffort), StringComparison.OrdinalIgnoreCase)
            .Replace("{sandboxMode}", Quote("read-only"), StringComparison.OrdinalIgnoreCase)
            .Replace("{permissionMode}", Quote("plan"), StringComparison.OrdinalIgnoreCase)
            .Replace("{workingDirectory}", Quote(workingDirectory), StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    internal static async Task<string> RunCommandAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var arg in WorkerShell.BaseArguments())
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start subscription CLI for semantic acceptance.");

        // Link to the outer token so either cancellation path cancels the reads.
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(drainCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(drainCts.Token);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        // Cap the drain so a grandchild that inherits the stdout pipe handle (e.g. the node
        // process spawned by claude-cli) cannot keep ReadToEndAsync alive indefinitely after
        // the parent exits. On timeout, kill the tree; the verdict is recorded as invalid
        // (advisory — the judge never blocks the deterministic merge gate).
        const int DrainTimeoutMs = 12_000;
        drainCts.CancelAfter(DrainTimeoutMs);
        string stdout;
        try
        {
            stdout = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            stdout = string.Empty;
        }
        return stdout.Trim();
    }
}

// Decorator that runs a leaf judge once per changed file rather than once over the whole diff,
// fixing the 6KB truncation blind spot for large changes. Falls back to whole-diff judging when
// PerFileDiffs is empty (graceful degradation for callers that don't populate it).
internal sealed class RecursivePerFileSemanticJudge : ISemanticJudge
{
    private readonly ISemanticJudge _leaf;

    public RecursivePerFileSemanticJudge(ISemanticJudge leaf) => _leaf = leaf;

    public string Name => $"recursive({_leaf.Name})";

    public TimeSpan? JudgeTimeout => _leaf.JudgeTimeout;

    public async Task<SemanticAcceptanceVerdict> JudgeAsync(
        SemanticAcceptanceInputs inputs,
        CancellationToken cancellationToken)
    {
        var perFileDiffs = inputs.PerFileDiffs;
        // CLI judges have a cold-start cost per invocation; fan-out multiplies that cost N-fold and
        // caused observed 90s timeouts on 3-file changes. Judge the whole diff in one call instead.
        if (perFileDiffs is null or { Count: 0 } || _leaf is SubscriptionCliSemanticJudge)
        {
            return await _leaf.JudgeAsync(inputs, cancellationToken).ConfigureAwait(false);
        }

        var perFileVerdicts = await Task.WhenAll(
            perFileDiffs.Select(file => _leaf.JudgeAsync(
                inputs with
                {
                    DiffExcerpt = file.Diff,
                    ChangedFiles = [file.File],
                    PerFileDiffs = null
                },
                cancellationToken)))
            .ConfigureAwait(false);

        return Aggregate(perFileVerdicts);
    }

    private static SemanticAcceptanceVerdict Aggregate(SemanticAcceptanceVerdict[] verdicts)
    {
        var valid = verdicts.Where(v => v.IsValid).ToList();
        if (valid.Count == 0)
        {
            return SemanticAcceptanceVerdict.Invalid(
                "RecursivePerFileSemanticJudge: no valid per-file verdict to aggregate.");
        }

        var criteriaMet = valid.All(v => v.CriteriaMet);
        var confidence = LowestConfidence(valid.Select(v => v.Confidence));
        var reasons = valid.SelectMany(v => v.Reasons).ToList();
        var unmet = valid.SelectMany(v => v.UnmetCriteria).Distinct(StringComparer.Ordinal).ToList();
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
