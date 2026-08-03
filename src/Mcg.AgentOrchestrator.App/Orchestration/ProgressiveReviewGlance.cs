using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ProgressiveReviewGlanceTriggerKind
{
    ChangedFiles,
    Elapsed
}

internal enum ProgressiveReviewGlanceVerdict
{
    OnTrack,
    Concern,
    FundamentalMisdirection,
    Invalid
}

internal enum ProgressiveReviewGlanceReasonCode
{
    ScopeDeviation,
    Approach,
    Subsystem,
    UnmentionedWork,
    Unknown
}

internal static class ProgressiveReviewGlanceLimits
{
    public const int DefaultTranscriptTailByteLimit = 32 * 1024;
}

internal sealed record ProgressiveReviewGlanceOptions(
    int ChangedFileThreshold = 3,
    TimeSpan? FirstElapsedThreshold = null,
    TimeSpan? ElapsedInterval = null,
    int PerRoundBudget = 2,
    int GlobalConcurrentCap = 1,
    TimeSpan? SmallRoundSuppressionThreshold = null,
    int ObjectiveCharacterLimit = 2000,
    int AcceptanceCharacterLimit = 4000,
    int CriteriaCorrectionOverlayCharacterLimit = 2000,
    int CriteriaCorrectionOverlayItemLimit = 20,
    int ChangedFilePromptLimit = 20,
    int ChangedFileListCharacterLimit = 3000,
    int DiffCharacterLimit = 8000,
    int TranscriptCharacterLimit = 3000,
    int TranscriptTailByteLimit = ProgressiveReviewGlanceLimits.DefaultTranscriptTailByteLimit,
    int TaskBriefCharacterLimit = 4000,
    TimeSpan? DispatchTimeout = null)
{
    public TimeSpan EffectiveFirstElapsedThreshold => FirstElapsedThreshold ?? TimeSpan.FromMinutes(15);
    public TimeSpan EffectiveElapsedInterval => ElapsedInterval ?? TimeSpan.FromMinutes(15);
    public TimeSpan EffectiveSmallRoundSuppressionThreshold => SmallRoundSuppressionThreshold ?? TimeSpan.FromMinutes(10);
    public TimeSpan EffectiveDispatchTimeout => DispatchTimeout ?? SubscriptionCliCompleter.DefaultTimeout;
}

internal sealed record ProgressiveReviewGlanceInputs(
    string GoalId,
    string TaskId,
    ProgressiveReviewGlanceTriggerKind Trigger,
    string TriggerDetail,
    string GoalObjective,
    string TaskBrief,
    string AcceptanceSection,
    IReadOnlyList<string> CriteriaCorrectionOverlay,
    IReadOnlyList<string> ChangedFiles,
    string DiffExcerpt,
    string TranscriptTail,
    IReadOnlyList<string> TrustedScopePaths,
    RepositoryScopeConfidence ScopeConfidence);

internal sealed record ProgressiveReviewGlanceDispatchResult(
    ProgressiveReviewGlanceVerdict Verdict,
    string Note,
    string EvidenceLine,
    int? InputTokens = null,
    int? OutputTokens = null,
    string? Model = null,
    string? Profile = null,
    ProgressiveReviewGlanceReasonCode? ReasonCode = null);

internal sealed record ProgressiveReviewGlanceGuardEvaluation(
    ProgressiveReviewGlanceDispatchResult Result,
    ProgressiveReviewGlanceGuardReceipt? Receipt);

internal interface IProgressiveReviewGlanceRunner
{
    Task<ProgressiveReviewGlanceDispatchResult> RunAsync(
        ProgressiveReviewGlanceInputs inputs,
        CancellationToken cancellationToken = default);
}

internal sealed record ProgressiveReviewGlanceObservationResult(
    bool MutatedTaskState,
    IReadOnlyList<string> ProgressLines);

internal sealed class ProgressiveReviewGlanceCoordinator
{
    private const string CriteriaCorrectionLabel = "criteria correction(s)";
    private const string ChangedFileLabel = "changed file(s)";
    private const int UntrackedDiffFileLimit = 20;
    private const int UntrackedDiffFileCharacterLimit = 6000;

    private readonly ProgressiveReviewGlanceOptions _options;
    private readonly IProgressiveReviewGlanceRunner _runner;
    private readonly IGoalLifecycleEventWriter _eventWriter;
    private readonly ICollaborationItemStore _collaborationStore;
    private readonly IProgressiveReviewSteeringStore? _steeringStore;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string?, string?, DispatchLiveChangeSnapshot> _liveChanges;
    private readonly Func<string, string?, string> _diffReader;
    private readonly Func<TaskProcessRecord?, string> _transcriptReader;
    private readonly Dictionary<string, RoundState> _rounds = new(StringComparer.Ordinal);
    private readonly List<RunningGlance> _running = [];
    private readonly Dictionary<string, GoalSummary> _summaries = new(StringComparer.Ordinal);

    internal static Func<string, string[], GitCli.GitResult> RunGit { get; set; } =
        (dir, args) => GitCli.Run(dir, args);

    public ProgressiveReviewGlanceCoordinator(
        IProgressiveReviewGlanceRunner runner,
        IGoalLifecycleEventWriter eventWriter,
        ICollaborationItemStore collaborationStore,
        ProgressiveReviewGlanceOptions? options = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string?, string?, DispatchLiveChangeSnapshot>? liveChanges = null,
        Func<string, string?, string>? diffReader = null,
        Func<TaskProcessRecord?, string>? transcriptReader = null,
        IProgressiveReviewSteeringStore? steeringStore = null)
    {
        _runner = runner;
        _eventWriter = eventWriter;
        _collaborationStore = collaborationStore;
        _options = options ?? new ProgressiveReviewGlanceOptions();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _liveChanges = liveChanges ?? ((worktree, baseCommit) => GoalChangesReader.BuildLiveDispatchSnapshot(worktree, baseCommit, displayLimit: _options.ChangedFilePromptLimit));
        _diffReader = diffReader ?? ReadDiff;
        _transcriptReader = transcriptReader ?? (process => ReadTranscriptTail(process, _options.TranscriptTailByteLimit));
        _steeringStore = steeringStore;
    }

    public static ProgressiveReviewGlanceCoordinator CreateDefault(
        OrchestratorWorkspace workspace,
        WorkerProfileCatalog workerProfiles,
        ProgressiveReviewGlanceOptions? options = null)
    {
        return new ProgressiveReviewGlanceCoordinator(
            new SubscriptionCliProgressiveReviewGlanceRunner(workerProfiles),
            new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory),
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
            options,
            steeringStore: SqliteProgressiveReviewSteeringStore.ForDirectory(workspace.OrchestratorDirectory));
    }

    public ProgressiveReviewGlanceObservationResult Observe(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<Goal> goals,
        IReadOnlyList<TaskDurationStatsRecord>? durationStats = null,
        Func<TaskSpec, DispatchLiveChangeSnapshot>? liveChanges = null)
    {
        try
        {
            return ObserveCore(kernel, goals, durationStats, liveChanges);
        }
        catch (Exception ex)
        {
            return new ProgressiveReviewGlanceObservationResult(
                false,
                [$"GLANCE result=advisory-error error={BoundSingleLine(ex.Message, 300)}"]);
        }
    }

    private ProgressiveReviewGlanceObservationResult ObserveCore(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<Goal> goals,
        IReadOnlyList<TaskDurationStatsRecord>? durationStats,
        Func<TaskSpec, DispatchLiveChangeSnapshot>? liveChanges)
    {
        var lines = new List<string>();
        var mutated = HarvestCompleted(lines);
        mutated |= SurfaceQueuedConcernsOnFailure(kernel);

        if (_running.Count >= _options.GlobalConcurrentCap)
        {
            return new ProgressiveReviewGlanceObservationResult(mutated, lines);
        }

        foreach (var goal in goals)
        {
            foreach (var task in goal.Tasks.Where(IsRunningDeveloperDispatch))
            {
                if (_running.Count >= _options.GlobalConcurrentCap)
                {
                    return new ProgressiveReviewGlanceObservationResult(mutated, lines);
                }

                TryStartGlance(goal, task, durationStats ?? [], lines, liveChanges);
            }
        }

        return new ProgressiveReviewGlanceObservationResult(mutated, lines);
    }

    internal static string BuildPrompt(ProgressiveReviewGlanceInputs inputs)
    {
        var overlay = inputs.CriteriaCorrectionOverlay.Count == 0
            ? "none"
            : string.Join(Environment.NewLine, BoundList(
                inputs.CriteriaCorrectionOverlay,
                maxItems: 20,
                charLimit: 2000,
                omittedLabel: CriteriaCorrectionLabel).Select(item => "- " + item));
        var files = inputs.ChangedFiles.Count == 0
            ? "none"
            : string.Join(Environment.NewLine, BoundList(
                inputs.ChangedFiles,
                maxItems: 20,
                charLimit: 3000,
                omittedLabel: ChangedFileLabel).Select(item => "- " + item));

        return $$"""
You are a progressive review glance for an in-flight Developer dispatch.

Return only JSON:
{"verdict":"on-track|concern|fundamental-misdirection","reasonCode":"scope-deviation|approach|subsystem|unmentioned-work","note":"short evidence-grounded note","evidenceLine":"single strongest evidence line"}

High bar: use fundamental-misdirection only for a defective criterion, forbidden scope, or provably impossible task. Concerns are queued advisory evidence only. Never ask to cancel unless the evidence is fundamental.
The current task brief and trusted scope below are authoritative over generated intake fallback text. Unknown scope is absence of evidence: it must never justify a scope-deviation verdict.

Goal:
{{inputs.GoalObjective}}

Current task brief:
{{inputs.TaskBrief}}

Trusted repository scope ({{inputs.ScopeConfidence}}):
{{(inputs.TrustedScopePaths.Count == 0 ? "unknown" : string.Join(Environment.NewLine, inputs.TrustedScopePaths.Select(path => "- " + path)))}}

Acceptance and criteria:
{{inputs.AcceptanceSection}}

Criteria correction overlay:
{{overlay}}

Trigger:
{{inputs.Trigger}} - {{inputs.TriggerDetail}}

Changed files:
{{files}}

Current diff excerpt:
{{inputs.DiffExcerpt}}

Transcript tail:
{{inputs.TranscriptTail}}
""";
    }

    internal static ProgressiveReviewGlanceDispatchResult ParseResult(
        string output,
        int? estimatedInputTokens = null)
    {
        try
        {
            var json = ExtractJson(output);
            var dto = JsonSerializer.Deserialize<GlanceVerdictDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (dto is null)
            {
                return Invalid("empty glance verdict", estimatedInputTokens);
            }

            var verdict = NormalizeVerdict(dto.Verdict);
            var note = BoundReceiptField(dto.Note);
            var evidence = BoundReceiptField(dto.EvidenceLine);
            if (verdict == ProgressiveReviewGlanceVerdict.Invalid)
            {
                return Invalid("unrecognized glance verdict", estimatedInputTokens);
            }

            return new ProgressiveReviewGlanceDispatchResult(
                verdict,
                string.IsNullOrWhiteSpace(note) ? verdict.ToString() : note,
                string.IsNullOrWhiteSpace(evidence) ? note : evidence,
                dto.InputTokens ?? estimatedInputTokens,
                dto.OutputTokens,
                dto.Model,
                dto.Profile,
                NormalizeReasonCode(dto.ReasonCode));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return Invalid($"invalid glance output: {ex.Message}", estimatedInputTokens);
        }
    }

    private void TryStartGlance(
        Goal goal,
        TaskSpec task,
        IReadOnlyList<TaskDurationStatsRecord> durationStats,
        List<string> lines,
        Func<TaskSpec, DispatchLiveChangeSnapshot>? liveChanges)
    {
        var roundKey = RoundKey(goal, task);
        var state = GetRoundState(roundKey);
        if (state.FiredCount >= _options.PerRoundBudget || IsSmallRound(goal, task, durationStats))
        {
            return;
        }

        var now = _utcNow();
        var elapsed = now - task.LastDispatch!.DispatchedAt;
        var elapsedTriggerDue = elapsed >= state.NextElapsedThreshold;
        if (!elapsedTriggerDue &&
            state.LastChangeProbeAt is { } lastProbe &&
            now - lastProbe < TimeSpan.FromSeconds(ConductorWatchProgressReporter.DefaultThrottleSeconds))
        {
            return;
        }

        var snapshot = liveChanges?.Invoke(task) ??
            _liveChanges(task.LastDispatch.WorkingDirectory, task.LastDispatch.BaseCommit);
        state.LastChangeProbeAt = now;
        ProgressiveReviewGlanceTriggerKind? trigger = null;
        string triggerDetail = string.Empty;
        if (!state.FileCountTriggered && snapshot.Files.Count >= _options.ChangedFileThreshold)
        {
            trigger = ProgressiveReviewGlanceTriggerKind.ChangedFiles;
            triggerDetail = $"changed_files={snapshot.Files.Count} threshold={_options.ChangedFileThreshold}";
            state.FileCountTriggered = true;
        }
        else
        {
            if (elapsed >= state.NextElapsedThreshold)
            {
                trigger = ProgressiveReviewGlanceTriggerKind.Elapsed;
                triggerDetail = $"elapsed_minutes={(int)elapsed.TotalMinutes} threshold_minutes={(int)state.NextElapsedThreshold.TotalMinutes}";
                state.NextElapsedThreshold += _options.EffectiveElapsedInterval;
            }
        }

        if (trigger is null)
        {
            return;
        }

        var scope = GoalFileScopeInference.ForScheduling(goal, task);
        var inputs = new ProgressiveReviewGlanceInputs(
            goal.Id.Value,
            task.Id.Value,
            trigger.Value,
            triggerDetail,
            BoundBlock(goal.Objective, _options.ObjectiveCharacterLimit),
            BoundBlock(task.Description, _options.TaskBriefCharacterLimit),
            BuildAcceptanceSection(goal, task, _options.AcceptanceCharacterLimit),
            BoundList(
                FormatCriteriaCorrectionOverlay(goal.EffectiveAcceptanceCriteriaCorrections),
                _options.CriteriaCorrectionOverlayItemLimit,
                _options.CriteriaCorrectionOverlayCharacterLimit,
                CriteriaCorrectionLabel),
            BoundChangedFiles(snapshot),
            BoundBlock(_diffReader(task.LastDispatch.WorkingDirectory, task.LastDispatch.BaseCommit), _options.DiffCharacterLimit),
            BoundTail(_transcriptReader(task.LastProcess), _options.TranscriptCharacterLimit),
            scope.Includes,
            scope.Confidence);
        var inputHash = HashInputs(inputs);
        var stopwatch = Stopwatch.StartNew();
        Task<ProgressiveReviewGlanceDispatchResult> run;
        try
        {
            run = RunGlanceWithTimeoutAsync(inputs);
        }
        catch (Exception ex)
        {
            run = Task.FromResult(new ProgressiveReviewGlanceDispatchResult(
                ProgressiveReviewGlanceVerdict.Invalid,
                $"glance runner failed: {BoundSingleLine(ex.Message, 300)}",
                BoundSingleLine(ex.Message, 300)));
        }

        _running.Add(new RunningGlance(
            roundKey,
            goal.Id,
            task.Id,
            task.LastDispatch.DispatchedAt,
            task.LastDispatch.WorkingDirectory,
            task.LastDispatch.ProviderSessionId,
            inputHash,
            inputs,
            run,
            stopwatch));
        state.FiredCount++;
        lines.Add($"GLANCE goal={Short(goal.Id.Value)} task={Short(task.Id.Value)} result=started trigger={trigger.Value} inputHash={inputHash}");
    }

    private async Task<ProgressiveReviewGlanceDispatchResult> RunGlanceWithTimeoutAsync(
        ProgressiveReviewGlanceInputs inputs)
    {
        using var timeoutCts = new CancellationTokenSource(_options.EffectiveDispatchTimeout);
        try
        {
            return await _runner.RunAsync(inputs, timeoutCts.Token).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return new ProgressiveReviewGlanceDispatchResult(
                ProgressiveReviewGlanceVerdict.Invalid,
                $"glance runner timed out after {(int)Math.Ceiling(_options.EffectiveDispatchTimeout.TotalSeconds)}s",
                "glance runner timeout");
        }
    }

    private bool HarvestCompleted(List<string> lines)
    {
        var mutated = false;
        for (var index = _running.Count - 1; index >= 0; index--)
        {
            var running = _running[index];
            if (!running.Task.IsCompleted)
            {
                continue;
            }

            _running.RemoveAt(index);
            running.Stopwatch.Stop();
            var evaluation = EvaluateUnsupportedScopeVerdict(running.Inputs, Complete(running));
            var result = evaluation.Result;
            var inputTokens = result.InputTokens ?? EstimateTokens(BuildPrompt(running.Inputs));
            var outputTokens = result.OutputTokens ?? EstimateTokens(result.Note + result.EvidenceLine);
            var totalTokens = inputTokens + outputTokens;

            TryAppendReceipt(running, result, inputTokens, outputTokens, totalTokens, lines);
            TryAppendGuardReceipt(running, evaluation.Receipt, lines);

            UpdateSummary(running, result, totalTokens);
            var summary = _summaries[running.GoalId.Value];
            TryAppendSummary(running, summary, lines);

            if (result.Verdict == ProgressiveReviewGlanceVerdict.Concern)
            {
                GetRoundState(running.RoundKey).QueuedConcerns.Add(result.Note);
            }
            else if (result.Verdict == ProgressiveReviewGlanceVerdict.FundamentalMisdirection)
            {
                TryEnqueueSteerIntent(running, result, lines);
            }

            lines.Add($"GLANCE goal={Short(running.GoalId.Value)} task={Short(running.TaskId.Value)} result=receipt verdict={result.Verdict} tokens={totalTokens} wall_ms={(long)running.Stopwatch.Elapsed.TotalMilliseconds}");
        }

        return mutated;
    }

    private void TryAppendGuardReceipt(
        RunningGlance running,
        ProgressiveReviewGlanceGuardReceipt? receipt,
        List<string> lines)
    {
        if (receipt is null)
            return;

        try
        {
            _eventWriter.AppendProgressiveReviewGlanceGuardReceipt(running.GoalId, running.TaskId, receipt);
        }
        catch (Exception ex)
        {
            lines.Add($"GLANCE goal={Short(running.GoalId.Value)} task={Short(running.TaskId.Value)} result=guard-receipt-write-failed error={BoundSingleLine(ex.Message, 300)}");
        }

        lines.Add(
            $"GLANCE goal={Short(running.GoalId.Value)} task={Short(running.TaskId.Value)} result=guard-evaluated " +
            $"original={receipt.OriginalVerdict} final={receipt.FinalVerdict} reason_code={receipt.ReasonCode} " +
            $"scope_confidence={receipt.ScopeConfidence} comparison={receipt.StructuralComparison} " +
            $"legacy_hint={receipt.LegacyPhraseHintMatched.ToString().ToLowerInvariant()} downgraded={receipt.Downgraded.ToString().ToLowerInvariant()} " +
            $"reason={BoundToken(receipt.DowngradeReason, 240)}");
    }

    private void TryAppendReceipt(
        RunningGlance running,
        ProgressiveReviewGlanceDispatchResult result,
        int inputTokens,
        int outputTokens,
        int totalTokens,
        List<string> lines)
    {
        try
        {
            _eventWriter.AppendProgressiveReviewGlanceReceipt(
                running.GoalId,
                running.TaskId,
                running.Inputs.Trigger.ToString(),
                running.InputHash,
                result.Verdict.ToString(),
                result.Note,
                inputTokens,
                outputTokens,
                totalTokens,
                running.Stopwatch.Elapsed,
                result.Model,
                result.Profile);
        }
        catch (Exception ex)
        {
            lines.Add($"GLANCE goal={Short(running.GoalId.Value)} task={Short(running.TaskId.Value)} result=receipt-write-failed error={BoundSingleLine(ex.Message, 300)}");
        }
    }

    private void TryAppendSummary(
        RunningGlance running,
        GoalSummary summary,
        List<string> lines)
    {
        try
        {
            _eventWriter.AppendProgressiveReviewGlanceSummary(
                running.GoalId,
                summary.Total,
                summary.OnTrack,
                summary.Concern,
                summary.FundamentalMisdirection,
                summary.Invalid,
                summary.TotalTokens);
        }
        catch (Exception ex)
        {
            lines.Add($"GLANCE goal={Short(running.GoalId.Value)} result=summary-write-failed error={BoundSingleLine(ex.Message, 300)}");
        }
    }

    private bool SurfaceQueuedConcernsOnFailure(AgentOrchestratorKernel kernel)
    {
        var mutated = false;
        foreach (var pair in _rounds.ToArray())
        {
            var state = pair.Value;
            if (state.QueuedConcerns.Count == 0 || state.ConcernsSurfaced)
            {
                continue;
            }

            if (!TryFindTask(kernel, pair.Key, out var goal, out var task))
            {
                continue;
            }

            if (ShouldSurfaceQueuedConcerns(goal, task, pair.Key))
            {
                kernel.RecordTaskNote(
                    goal.Id,
                    task.Id,
                    "Progressive review glance concern(s) queued for this failed round: " +
                    string.Join(" | ", state.QueuedConcerns.Select(note => BoundSingleLine(note, 300))));
                state.ConcernsSurfaced = true;
                mutated = true;
            }
        }

        return mutated;
    }

    private ProgressiveReviewGlanceDispatchResult Complete(RunningGlance running)
    {
        try
        {
            return running.Task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return new ProgressiveReviewGlanceDispatchResult(
                ProgressiveReviewGlanceVerdict.Invalid,
                $"glance runner failed: {BoundSingleLine(ex.Message, 300)}",
                BoundSingleLine(ex.Message, 300));
        }
    }

    internal static ProgressiveReviewGlanceDispatchResult GuardUnsupportedScopeVerdict(
        ProgressiveReviewGlanceInputs inputs,
        ProgressiveReviewGlanceDispatchResult result) =>
        EvaluateUnsupportedScopeVerdict(inputs, result).Result;

    internal static ProgressiveReviewGlanceGuardEvaluation EvaluateUnsupportedScopeVerdict(
        ProgressiveReviewGlanceInputs inputs,
        ProgressiveReviewGlanceDispatchResult result)
    {
        if (result.Verdict != ProgressiveReviewGlanceVerdict.FundamentalMisdirection)
        {
            return new ProgressiveReviewGlanceGuardEvaluation(result, null);
        }

        var changedFiles = inputs.ChangedFiles
            .Where(path => !path.Contains("more changed file", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var allChangesWithinTrustedScope =
            inputs.ScopeConfidence == RepositoryScopeConfidence.Precise &&
            inputs.TrustedScopePaths.Count > 0 &&
            changedFiles.Length > 0 &&
            changedFiles.All(changed => inputs.TrustedScopePaths.Any(scope =>
                changed.Equals(scope, StringComparison.OrdinalIgnoreCase) ||
                changed.StartsWith(scope.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)));
        var positivelyNonScope = result.ReasonCode is
            ProgressiveReviewGlanceReasonCode.Approach or
            ProgressiveReviewGlanceReasonCode.Subsystem or
            ProgressiveReviewGlanceReasonCode.UnmentionedWork;
        var shouldDowngrade = !positivelyNonScope &&
            (inputs.ScopeConfidence == RepositoryScopeConfidence.Unknown || allChangesWithinTrustedScope);
        var structuralComparison = inputs.ScopeConfidence == RepositoryScopeConfidence.Unknown
            ? "scope-unknown"
            : allChangesWithinTrustedScope
                ? "all-changes-within-trusted-scope"
                : "changes-not-proven-within-trusted-scope";
        var reason = positivelyNonScope
            ? $"typed non-scope reason {result.ReasonCode} preserves the kill verdict"
            : inputs.ScopeConfidence == RepositoryScopeConfidence.Unknown
                ? "scope confidence is Unknown and no typed non-scope reason was supplied"
                : allChangesWithinTrustedScope
                    ? "all changed files are within the trusted scope"
                    : "changed files are not all within the precise trusted scope";
        var guarded = shouldDowngrade
            ? result with
            {
                Verdict = ProgressiveReviewGlanceVerdict.Concern,
                Note = $"Scope-based fundamental verdict downgraded because {reason}: {result.Note}"
            }
            : result;
        var receipt = new ProgressiveReviewGlanceGuardReceipt(
            inputs.ScopeConfidence.ToString(),
            inputs.TrustedScopePaths.ToArray(),
            changedFiles,
            BoundReceiptField(result.Note),
            BoundReceiptField(result.EvidenceLine),
            result.ReasonCode?.ToString() ?? "absent",
            LooksLikeScopeDeviationHint(result.Note + " " + result.EvidenceLine),
            result.Verdict.ToString(),
            guarded.Verdict.ToString(),
            shouldDowngrade,
            reason,
            structuralComparison);
        return new ProgressiveReviewGlanceGuardEvaluation(guarded, receipt);
    }

    private static bool LooksLikeScopeDeviationHint(string text) =>
        text.Contains("scope deviation", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("outside scope", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("outside the scope", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("forbidden scope", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("target file", StringComparison.OrdinalIgnoreCase);

    private static string BoundReceiptField(string? value)
    {
        const int limit = 4096;
        const string marker = "...[truncated at 4096 chars]";
        var text = value ?? string.Empty;
        return text.Length <= limit ? text : text[..(limit - marker.Length)] + marker;
    }

    private static string BoundToken(string value, int limit) =>
        BoundSingleLine(value, limit).Replace(' ', '-');

    private void RaiseMisdirectionAttention(RunningGlance running, ProgressiveReviewGlanceDispatchResult result)
    {
        var goalPrefix = Short(running.GoalId.Value);
        var taskPrefix = Short(running.TaskId.Value);
        var subject = $"Progressive review glance: possible misdirection in {goalPrefix}";
        var body = $"""
Goal: {running.GoalId.Value}
Task: {running.TaskId.Value}
Evidence: {result.EvidenceLine}
Suggested operator action: inspect the running Developer dispatch; cancel plus resume-with-guidance only if this evidence is confirmed.
Note: {result.Note}
""";
        _ = _collaborationStore.RaiseAsync(
            CollaborationItemType.Decision,
            running.GoalId.Value,
            subject,
            body,
            $"progressive-review-glance:misdirection:{running.RoundKey}",
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private void TryRaiseMisdirectionAttention(
        RunningGlance running,
        ProgressiveReviewGlanceDispatchResult result,
        List<string> lines)
    {
        try
        {
            RaiseMisdirectionAttention(running, result);
        }
        catch (Exception ex)
        {
            lines.Add($"GLANCE goal={Short(running.GoalId.Value)} task={Short(running.TaskId.Value)} result=attention-write-failed error={BoundSingleLine(ex.Message, 300)}");
        }
    }

    private void TryEnqueueSteerIntent(
        RunningGlance running,
        ProgressiveReviewGlanceDispatchResult result,
        List<string> lines)
    {
        if (_steeringStore is null)
        {
            TryRaiseMisdirectionAttention(running, result, lines);
            return;
        }

        try
        {
            var now = _utcNow();
            var steeringInputsHash = BuildSteeringInputsHash(
                running,
                now,
                TryResolveHead(running.WorkingDirectory));
            var steerIdentity = $"glance-{steeringInputsHash}";
            var intent = new ProgressiveReviewSteerIntent(
                Id: steerIdentity,
                GoalId: running.GoalId.Value,
                TaskId: running.TaskId.Value,
                Role: AgentRole.Developer.ToString(),
                RoundKey: running.RoundKey,
                TriggerGlanceId: steerIdentity,
                InputsHash: steeringInputsHash,
                GlanceVerdictTimestamp: now,
                MisdirectionEvidence: result.EvidenceLine,
                CorrectiveDirection: result.Note,
                GuidanceText: BuildSteeringGuidance(running, result),
                CreatedAt: now);
            _steeringStore.EnqueueIntentAsync(intent, CancellationToken.None).GetAwaiter().GetResult();
            lines.Add($"GLANCE goal={Short(running.GoalId.Value)} task={Short(running.TaskId.Value)} result=steer-intent id={intent.Id}");
        }
        catch (Exception ex)
        {
            lines.Add($"GLANCE goal={Short(running.GoalId.Value)} task={Short(running.TaskId.Value)} result=steer-intent-write-failed error={BoundSingleLine(ex.Message, 300)}");
            TryRaiseMisdirectionAttention(running, result, lines);
        }
    }

    private static string BuildSteeringGuidance(
        RunningGlance running,
        ProgressiveReviewGlanceDispatchResult result)
    {
        var overlay = running.Inputs.CriteriaCorrectionOverlay.Count == 0
            ? "- none"
            : string.Join(Environment.NewLine, running.Inputs.CriteriaCorrectionOverlay.Select(item => $"- {item}"));
        var changedFiles = running.Inputs.ChangedFiles.Count == 0
            ? "- none"
            : string.Join(Environment.NewLine, running.Inputs.ChangedFiles.Select(item => $"- {item}"));
        return $"""
ProgressiveReviewSteer guidance. Treat this message as authoritative over remembered session context.

Freshness envelope:
- Goal id: {running.GoalId.Value}
- Task id: {running.TaskId.Value}
- Worktree diff at steer time:
{running.Inputs.DiffExcerpt}

Current acceptance criteria:
{running.Inputs.AcceptanceSection}

Active criteria-correction overlay:
{overlay}

Changed files at steer time:
{changedFiles}

Misdirection evidence:
{result.EvidenceLine}

Corrective direction:
{result.Note}
""";
    }

    private bool IsSmallRound(
        Goal goal,
        TaskSpec task,
        IReadOnlyList<TaskDurationStatsRecord> durationStats)
    {
        var complexity = TaskComplexityEstimator.Estimate(task.Description, goal.Objective, task.RequiredRole);
        var estimate = TaskDurationReport.FindEstimate(durationStats, task.RequiredRole, complexity);
        return estimate?.MedianLegitimateRuntime is { } median &&
            median < _options.EffectiveSmallRoundSuppressionThreshold;
    }

    private RoundState GetRoundState(string roundKey)
    {
        if (_rounds.TryGetValue(roundKey, out var state))
        {
            return state;
        }

        state = new RoundState(_options.EffectiveFirstElapsedThreshold);
        _rounds[roundKey] = state;
        return state;
    }

    private void UpdateSummary(
        RunningGlance running,
        ProgressiveReviewGlanceDispatchResult result,
        int tokenCount)
    {
        if (!_summaries.TryGetValue(running.GoalId.Value, out var summary))
        {
            summary = new GoalSummary();
            _summaries[running.GoalId.Value] = summary;
        }

        summary.Total++;
        summary.TotalTokens += tokenCount;
        switch (result.Verdict)
        {
            case ProgressiveReviewGlanceVerdict.OnTrack:
                summary.OnTrack++;
                break;
            case ProgressiveReviewGlanceVerdict.Concern:
                summary.Concern++;
                break;
            case ProgressiveReviewGlanceVerdict.FundamentalMisdirection:
                summary.FundamentalMisdirection++;
                break;
            default:
                summary.Invalid++;
                break;
        }
    }

    private static bool IsRunningDeveloperDispatch(TaskSpec task) =>
        task.RequiredRole == AgentRole.Developer &&
        task.Status == WorkTaskStatus.Running &&
        task.LastDispatch is not null;

    private static bool TryFindTask(
        AgentOrchestratorKernel kernel,
        string roundKey,
        out Goal goal,
        out TaskSpec task)
    {
        var parts = roundKey.Split('|');
        foreach (var candidate in kernel.Goals)
        {
            if (!candidate.Id.Value.Equals(parts[0], StringComparison.Ordinal))
            {
                continue;
            }

            var found = candidate.Tasks.FirstOrDefault(t => t.Id.Value.Equals(parts[1], StringComparison.Ordinal));
            if (found is not null)
            {
                goal = candidate;
                task = found;
                return true;
            }
        }

        goal = null!;
        task = null!;
        return false;
    }

    private static string RoundKey(Goal goal, TaskSpec task) =>
        $"{goal.Id.Value}|{task.Id.Value}|{task.LastDispatch!.DispatchedAt.UtcTicks}";

    private static bool ShouldSurfaceQueuedConcerns(Goal goal, TaskSpec task, string roundKey) =>
        task.LastVerification?.Succeeded is false ||
        WasOriginalRoundRetried(roundKey, task) ||
        HasDownstreamFailureAfterRound(goal, task, roundKey);

    private static bool WasOriginalRoundRetried(string roundKey, TaskSpec task) =>
        task.LatestRetryAt is { } latestRetryAt &&
        TryGetRoundDispatchAt(roundKey, out var dispatchedAt) &&
        latestRetryAt > dispatchedAt;

    private static bool HasDownstreamFailureAfterRound(Goal goal, TaskSpec origin, string roundKey)
    {
        if (!TryGetRoundDispatchAt(roundKey, out var dispatchedAt))
        {
            return false;
        }

        return goal.Tasks.Any(candidate =>
            IsDownstreamRole(origin.RequiredRole, candidate.RequiredRole) &&
            candidate.LastVerification is { Succeeded: false } verification &&
            verification.CompletedAt >= dispatchedAt);
    }

    private static bool IsDownstreamRole(AgentRole upstream, AgentRole candidate) =>
        SdlcRoleOrder(candidate) > SdlcRoleOrder(upstream);

    private static int SdlcRoleOrder(AgentRole role) => role switch
    {
        AgentRole.Researcher => 0,
        AgentRole.Planner => 1,
        AgentRole.Ideation => 2,
        AgentRole.Developer => 3,
        AgentRole.Tester => 4,
        AgentRole.Reviewer => 5,
        _ => int.MaxValue
    };

    private static bool TryGetRoundDispatchAt(string roundKey, out DateTimeOffset dispatchedAt)
    {
        var parts = roundKey.Split('|');
        if (parts.Length >= 3 && long.TryParse(parts[2], out var ticks))
        {
            dispatchedAt = new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
            return true;
        }

        dispatchedAt = default;
        return false;
    }

    private static string HashInputs(ProgressiveReviewGlanceInputs inputs)
    {
        var text = JsonSerializer.Serialize(inputs, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
    }

    private static string BuildSteeringInputsHash(
        RunningGlance running,
        DateTimeOffset verdictTimestamp,
        string? worktreeHeadSha)
    {
        var inputs = new ProgressiveReviewSteeringHashInputs(
            running.GoalId.Value,
            running.TaskId.Value,
            AgentRole.Developer.ToString(),
            running.ProviderSessionId ?? string.Empty,
            worktreeHeadSha ?? string.Empty,
            verdictTimestamp,
            $"sha256:{HashText(running.Inputs.AcceptanceSection)}",
            $"sha256:{HashLines(running.Inputs.CriteriaCorrectionOverlay)}");
        var text = JsonSerializer.Serialize(inputs, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string HashLines(IReadOnlyList<string> values) =>
        HashText(string.Join("\n", values));

    private static string? TryResolveHead(string? worktree)
    {
        if (string.IsNullOrWhiteSpace(worktree))
            return null;

        try
        {
            var result = RunGit(worktree, ["rev-parse", "HEAD"]);
            return result.Succeeded ? result.Output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private IReadOnlyList<string> BoundChangedFiles(DispatchLiveChangeSnapshot snapshot)
    {
        var hasCompleteFileSet = snapshot.Files.Count > 0;
        return BoundList(
            hasCompleteFileSet ? snapshot.Files : snapshot.DisplayFiles,
            _options.ChangedFilePromptLimit,
            _options.ChangedFileListCharacterLimit,
            ChangedFileLabel,
            hasCompleteFileSet ? 0 : snapshot.RemainingFileCount);
    }

    private static IReadOnlyList<string> FormatCriteriaCorrectionOverlay(
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> corrections)
    {
        return corrections
            .Select(correction =>
            {
                var source = correction.SourceTaskId is null
                    ? correction.SourceKind.ToString()
                    : $"{correction.SourceKind} task={correction.SourceTaskId.Value}";
                return correction.IsWaiver
                    ? $"criterion=\"{correction.SupersededCriterion}\"; status=waived; reason=\"{correction.WaiverReason}\"; actor={correction.Actor}; recordedAt={correction.RecordedAt:u}; source={source}"
                    : $"supersedes=\"{correction.SupersededCriterion}\"; correction=\"{correction.Correction}\"; actor={correction.Actor}; recordedAt={correction.RecordedAt:u}; source={source}";
            })
            .ToArray();
    }

    private static string BuildAcceptanceSection(Goal goal, TaskSpec task, int limit)
    {
        var sections = new List<string>();
        if (goal.RefinedSpec?.AcceptanceCriteria is { Count: > 0 } criteria)
        {
            sections.Add("Current refined acceptance criteria:" + Environment.NewLine +
                string.Join(Environment.NewLine, criteria.Select(criterion => $"- {criterion.Trim()}")));
        }

        sections.Add("Task acceptance excerpt:" + Environment.NewLine +
            ExtractAcceptanceSection(task.Description, limit));
        return BoundBlock(string.Join(Environment.NewLine + Environment.NewLine, sections), limit);
    }

    private static string ExtractAcceptanceSection(string description, int limit)
    {
        var markers = new[] { "## Acceptance", "ACCEPTANCE", "Acceptance criteria:", "Acceptance Criteria:" };
        var start = markers
            .Select(marker => description.IndexOf(marker, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();
        return BoundBlock(description[start..], limit);
    }

    internal static string ReadDiff(string workingDirectory, string? baseCommit)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return "diff unavailable: missing working directory";
        }

        var committed = string.IsNullOrWhiteSpace(baseCommit)
            ? string.Empty
            : RunGit(workingDirectory, ["diff", $"{baseCommit}..HEAD", "--"]).Output;
        var working = RunGit(workingDirectory, ["diff", "--"]).Output;
        var staged = RunGit(workingDirectory, ["diff", "--cached", "--"]).Output;
        var untracked = ReadUntrackedDiff(workingDirectory);
        var combined = string.Join(Environment.NewLine, new[] { committed, staged, working, untracked }.Where(text => !string.IsNullOrWhiteSpace(text)));
        return string.IsNullOrWhiteSpace(combined) ? "(no diff content available)" : combined;
    }

    internal static string ReadUntrackedDiff(string workingDirectory)
    {
        var result = RunGit(workingDirectory, ["ls-files", "--others", "--exclude-standard"]);
        if (result.ExitCode != 0)
        {
            return string.Empty;
        }

        var files = GoalChangesReader.ParseFileList(result.Output);
        if (files.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var file in files.Take(UntrackedDiffFileLimit))
        {
            if (!TryResolveWorktreeFile(workingDirectory, file, out var fullPath) ||
                !File.Exists(fullPath))
            {
                continue;
            }

            var normalized = file.Replace('\\', '/');
            builder.AppendLine($"diff --git a/{normalized} b/{normalized}");
            builder.AppendLine("new file mode 100644");
            builder.AppendLine("index 0000000..0000000");
            builder.AppendLine("--- /dev/null");
            builder.AppendLine($"+++ b/{normalized}");
            AppendAddedLines(builder, fullPath);
        }

        var remaining = files.Count - UntrackedDiffFileLimit;
        if (remaining > 0)
        {
            builder.AppendLine($"[... {remaining} more untracked file(s) omitted ...]");
        }

        return builder.ToString();
    }

    private static bool TryResolveWorktreeFile(string workingDirectory, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var root = Path.GetFullPath(workingDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static void AppendAddedLines(StringBuilder builder, string fullPath)
    {
        var text = ReadTextPrefix(fullPath, UntrackedDiffFileCharacterLimit, out var truncated);
        if (text.IndexOf('\0') >= 0)
        {
            builder.AppendLine("+[binary content omitted]");
            return;
        }

        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            builder.Append('+');
            builder.AppendLine(line);
        }

        if (truncated)
        {
            builder.AppendLine("+[untracked file truncated]");
        }
    }

    private static string ReadTextPrefix(string fullPath, int characterLimit, out bool truncated)
    {
        using var reader = new StreamReader(fullPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[Math.Max(0, characterLimit) + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        truncated = read > characterLimit || !reader.EndOfStream;
        return new string(buffer, 0, Math.Min(read, characterLimit));
    }

    internal static string ReadTranscriptTail(
        TaskProcessRecord? process,
        int transcriptTailByteLimit = ProgressiveReviewGlanceLimits.DefaultTranscriptTailByteLimit)
    {
        if (process is null)
        {
            return "(no process transcript yet)";
        }

        return string.Join(Environment.NewLine, [
            "stdout:",
            ReadLogTail(process.StandardOutputPath, transcriptTailByteLimit),
            "stderr:",
            ReadLogTail(process.StandardErrorPath, transcriptTailByteLimit)]);
    }

    internal static string ReadLogTail(string path, int byteLimit)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || byteLimit <= 0)
        {
            return string.Empty;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = stream.Length;
        var bytesToRead = (int)Math.Min(byteLimit, length);
        var start = length - bytesToRead;
        var buffer = new byte[bytesToRead];
        stream.Seek(start, SeekOrigin.Begin);
        var totalRead = 0;
        while (totalRead < bytesToRead)
        {
            var read = stream.Read(buffer, totalRead, bytesToRead - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        var offset = start == 0 ? 0 : LeadingPartialLineByteCount(buffer, totalRead);
        using var tail = new MemoryStream(buffer, offset, totalRead - offset, writable: false);
        using var reader = new StreamReader(tail, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static int LeadingPartialLineByteCount(byte[] buffer, int length)
    {
        for (var index = 0; index < length; index++)
        {
            if (buffer[index] == '\n')
            {
                return index + 1;
            }
        }

        return length;
    }

    private static string BoundBlock(string text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        text = text.Trim();
        return text.Length <= limit
            ? text
            : text[..limit] + $"{Environment.NewLine}...(truncated at {limit} chars)";
    }

    private static string BoundTail(string text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        text = text.Trim();
        return text.Length <= limit
            ? text
            : $"...(tail truncated to {limit} chars){Environment.NewLine}" + text[^limit..];
    }

    private static IReadOnlyList<string> BoundList(
        IEnumerable<string> values,
        int maxItems,
        int charLimit,
        string omittedLabel,
        int extraOmittedCount = 0)
    {
        var result = new List<string>();
        var usedCharacters = 0;
        var omitted = Math.Max(0, extraOmittedCount);
        foreach (var value in values)
        {
            var text = BoundSingleLine(value, Math.Min(500, Math.Max(1, charLimit)));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (result.Count >= maxItems || usedCharacters + text.Length > charLimit)
            {
                omitted++;
                continue;
            }

            result.Add(text);
            usedCharacters += text.Length;
        }

        if (omitted > 0)
        {
            result.Add($"... {omitted} more {omittedLabel} omitted");
        }

        return result;
    }

    private static string BoundSingleLine(string? value, int limit)
    {
        var text = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return text.Length <= limit ? text : text[..limit];
    }

    internal static string BoundSingleLineForSteering(string? value, int limit) => BoundSingleLine(value, limit);

    private static int EstimateTokens(string text) =>
        Math.Max(1, (int)Math.Ceiling((text?.Length ?? 0) / 4.0));

    private static string ExtractJson(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("missing JSON object");
        }

        return output[start..(end + 1)];
    }

    private static ProgressiveReviewGlanceVerdict NormalizeVerdict(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "on-track" => ProgressiveReviewGlanceVerdict.OnTrack,
            "concern" => ProgressiveReviewGlanceVerdict.Concern,
            "fundamental-misdirection" => ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            _ => ProgressiveReviewGlanceVerdict.Invalid
        };
    }

    private static ProgressiveReviewGlanceReasonCode? NormalizeReasonCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "scope-deviation" => ProgressiveReviewGlanceReasonCode.ScopeDeviation,
            "approach" => ProgressiveReviewGlanceReasonCode.Approach,
            "subsystem" => ProgressiveReviewGlanceReasonCode.Subsystem,
            "unmentioned-work" => ProgressiveReviewGlanceReasonCode.UnmentionedWork,
            _ => ProgressiveReviewGlanceReasonCode.Unknown
        };
    }

    private static ProgressiveReviewGlanceDispatchResult Invalid(string note, int? estimatedInputTokens) =>
        new(
            ProgressiveReviewGlanceVerdict.Invalid,
            BoundSingleLine(note, 500),
            BoundSingleLine(note, 300),
            estimatedInputTokens,
            null);

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];

    private sealed record RunningGlance(
        string RoundKey,
        GoalId GoalId,
        TaskId TaskId,
        DateTimeOffset DispatchedAt,
        string WorkingDirectory,
        string? ProviderSessionId,
        string InputHash,
        ProgressiveReviewGlanceInputs Inputs,
        Task<ProgressiveReviewGlanceDispatchResult> Task,
        Stopwatch Stopwatch);

    private sealed record ProgressiveReviewSteeringHashInputs(
        string GoalId,
        string TaskId,
        string Role,
        string SessionId,
        string WorktreeHeadSha,
        DateTimeOffset GlanceVerdictTimestamp,
        string AcceptanceCriteriaVersionHash,
        string CriteriaCorrectionOverlayVersionHash);

    private sealed class RoundState
    {
        public RoundState(TimeSpan firstElapsedThreshold) => NextElapsedThreshold = firstElapsedThreshold;

        public int FiredCount { get; set; }
        public bool FileCountTriggered { get; set; }
        public TimeSpan NextElapsedThreshold { get; set; }
        public DateTimeOffset? LastChangeProbeAt { get; set; }
        public bool ConcernsSurfaced { get; set; }
        public List<string> QueuedConcerns { get; } = [];
    }

    private sealed class GoalSummary
    {
        public int Total { get; set; }
        public int OnTrack { get; set; }
        public int Concern { get; set; }
        public int FundamentalMisdirection { get; set; }
        public int Invalid { get; set; }
        public int TotalTokens { get; set; }
    }

    private sealed class GlanceVerdictDto
    {
        public string? Verdict { get; set; }
        public string? Note { get; set; }
        public string? EvidenceLine { get; set; }
        public string? ReasonCode { get; set; }
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public string? Model { get; set; }
        public string? Profile { get; set; }
    }
}

internal sealed class SubscriptionCliProgressiveReviewGlanceRunner : IProgressiveReviewGlanceRunner
{
    private readonly WorkerProfileCatalog _profiles;
    private readonly Func<SubscriptionCliCompleter, string, CancellationToken, Task<string>> _complete;

    public SubscriptionCliProgressiveReviewGlanceRunner(
        WorkerProfileCatalog profiles,
        Func<SubscriptionCliCompleter, string, CancellationToken, Task<string>>? complete = null)
    {
        _profiles = profiles;
        _complete = complete ?? ((completer, prompt, ct) => completer.CompleteAsync(prompt, "progressive-review-glance.md", ct));
    }

    public async Task<ProgressiveReviewGlanceDispatchResult> RunAsync(
        ProgressiveReviewGlanceInputs inputs,
        CancellationToken cancellationToken = default)
    {
        var selection = SelectProfile(_profiles);
        var completer = new SubscriptionCliCompleter(
            _profiles,
            selection.ProfileName,
            selection.ModelAlias,
            AgentCatalog.RoutineSubscriptionReasoningEffort);
        var prompt = ProgressiveReviewGlanceCoordinator.BuildPrompt(inputs);
        var stdout = await _complete(completer, prompt, cancellationToken).ConfigureAwait(false);
        var parsed = ProgressiveReviewGlanceCoordinator.ParseResult(stdout, EstimateTokens(prompt));
        return parsed with
        {
            Model = parsed.Model ?? selection.ModelAlias,
            Profile = parsed.Profile ?? selection.ProfileName
        };
    }

    internal static GlanceProfileSelection SelectProfile(WorkerProfileCatalog profiles)
    {
        var spark = profiles.Profiles.FirstOrDefault(profile =>
            profile.Name.Equals(WorkerProfileDispatcher.OpenAiSparkSubscriptionProfileName, StringComparison.OrdinalIgnoreCase));
        if (spark is not null &&
            !WorkerProfileDiagnostics.IsEchoOnlyCommand(spark.CommandTemplate) &&
            WorkerProfileDiagnostics.UsesSubscriptionModelPlaceholder(spark.CommandTemplate) &&
            WorkerProfileDiagnostics.UsesSubscriptionReasoningPlaceholder(spark.CommandTemplate))
        {
            return new GlanceProfileSelection(
                WorkerProfileDispatcher.OpenAiSparkSubscriptionProfileName,
                WorkerProfileDispatcher.OpenAiSparkSubscriptionModelName);
        }

        _ = profiles.GetRequired(WorkerProfileDispatcher.OpenAiSubscriptionProfileName);
        return new GlanceProfileSelection(
            WorkerProfileDispatcher.OpenAiSubscriptionProfileName,
            AgentCatalog.OpenAiSubscriptionModelAlias);
    }

    private static int EstimateTokens(string text) =>
        Math.Max(1, (int)Math.Ceiling((text?.Length ?? 0) / 4.0));
}

internal sealed record GlanceProfileSelection(string ProfileName, string ModelAlias);
