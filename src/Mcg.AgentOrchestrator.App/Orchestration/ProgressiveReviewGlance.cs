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
    int TranscriptCharacterLimit = 3000)
{
    public TimeSpan EffectiveFirstElapsedThreshold => FirstElapsedThreshold ?? TimeSpan.FromMinutes(15);
    public TimeSpan EffectiveElapsedInterval => ElapsedInterval ?? TimeSpan.FromMinutes(15);
    public TimeSpan EffectiveSmallRoundSuppressionThreshold => SmallRoundSuppressionThreshold ?? TimeSpan.FromMinutes(10);
}

internal sealed record ProgressiveReviewGlanceInputs(
    string GoalId,
    string TaskId,
    ProgressiveReviewGlanceTriggerKind Trigger,
    string TriggerDetail,
    string GoalObjective,
    string AcceptanceSection,
    IReadOnlyList<string> CriteriaCorrectionOverlay,
    IReadOnlyList<string> ChangedFiles,
    string DiffExcerpt,
    string TranscriptTail);

internal sealed record ProgressiveReviewGlanceDispatchResult(
    ProgressiveReviewGlanceVerdict Verdict,
    string Note,
    string EvidenceLine,
    int? InputTokens = null,
    int? OutputTokens = null,
    string? Model = null,
    string? Profile = null);

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

    private readonly ProgressiveReviewGlanceOptions _options;
    private readonly IProgressiveReviewGlanceRunner _runner;
    private readonly IGoalLifecycleEventWriter _eventWriter;
    private readonly ICollaborationItemStore _collaborationStore;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string?, string?, DispatchLiveChangeSnapshot> _liveChanges;
    private readonly Func<string, string?, string> _diffReader;
    private readonly Func<TaskProcessRecord?, string> _transcriptReader;
    private readonly Dictionary<string, RoundState> _rounds = new(StringComparer.Ordinal);
    private readonly List<RunningGlance> _running = [];
    private readonly Dictionary<string, GoalSummary> _summaries = new(StringComparer.Ordinal);

    public ProgressiveReviewGlanceCoordinator(
        IProgressiveReviewGlanceRunner runner,
        IGoalLifecycleEventWriter eventWriter,
        ICollaborationItemStore collaborationStore,
        ProgressiveReviewGlanceOptions? options = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string?, string?, DispatchLiveChangeSnapshot>? liveChanges = null,
        Func<string, string?, string>? diffReader = null,
        Func<TaskProcessRecord?, string>? transcriptReader = null)
    {
        _runner = runner;
        _eventWriter = eventWriter;
        _collaborationStore = collaborationStore;
        _options = options ?? new ProgressiveReviewGlanceOptions();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _liveChanges = liveChanges ?? ((worktree, baseCommit) => GoalChangesReader.BuildLiveDispatchSnapshot(worktree, baseCommit, displayLimit: _options.ChangedFilePromptLimit));
        _diffReader = diffReader ?? ReadDiff;
        _transcriptReader = transcriptReader ?? ReadTranscriptTail;
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
            options);
    }

    public ProgressiveReviewGlanceObservationResult Observe(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<Goal> goals,
        IReadOnlyList<TaskDurationStatsRecord>? durationStats = null)
    {
        try
        {
            return ObserveCore(kernel, goals, durationStats);
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
        IReadOnlyList<TaskDurationStatsRecord>? durationStats)
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

                TryStartGlance(goal, task, durationStats ?? [], lines);
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
{"verdict":"on-track|concern|fundamental-misdirection","note":"short evidence-grounded note","evidenceLine":"single strongest evidence line"}

High bar: use fundamental-misdirection only for a defective criterion, forbidden scope, or provably impossible task. Concerns are queued advisory evidence only. Never ask to cancel unless the evidence is fundamental.

Goal:
{{inputs.GoalObjective}}

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
            var note = BoundSingleLine(dto.Note, 500);
            var evidence = BoundSingleLine(dto.EvidenceLine, 300);
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
                dto.Profile);
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
        List<string> lines)
    {
        var roundKey = RoundKey(goal, task);
        var state = GetRoundState(roundKey);
        if (state.FiredCount >= _options.PerRoundBudget || IsSmallRound(goal, task, durationStats))
        {
            return;
        }

        var snapshot = _liveChanges(task.LastDispatch!.WorkingDirectory, task.LastDispatch.BaseCommit);
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
            var elapsed = _utcNow() - task.LastDispatch.DispatchedAt;
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

        var inputs = new ProgressiveReviewGlanceInputs(
            goal.Id.Value,
            task.Id.Value,
            trigger.Value,
            triggerDetail,
            BoundBlock(goal.Objective, _options.ObjectiveCharacterLimit),
            ExtractAcceptanceSection(task.Description, _options.AcceptanceCharacterLimit),
            BoundList(
                task.CriterionRetryFeedback,
                _options.CriteriaCorrectionOverlayItemLimit,
                _options.CriteriaCorrectionOverlayCharacterLimit,
                CriteriaCorrectionLabel),
            BoundChangedFiles(snapshot),
            BoundBlock(_diffReader(task.LastDispatch.WorkingDirectory, task.LastDispatch.BaseCommit), _options.DiffCharacterLimit),
            BoundTail(_transcriptReader(task.LastProcess), _options.TranscriptCharacterLimit));
        var inputHash = HashInputs(inputs);
        var stopwatch = Stopwatch.StartNew();
        Task<ProgressiveReviewGlanceDispatchResult> run;
        try
        {
            run = _runner.RunAsync(inputs);
        }
        catch (Exception ex)
        {
            run = Task.FromResult(new ProgressiveReviewGlanceDispatchResult(
                ProgressiveReviewGlanceVerdict.Invalid,
                $"glance runner failed: {BoundSingleLine(ex.Message, 300)}",
                BoundSingleLine(ex.Message, 300)));
        }

        _running.Add(new RunningGlance(roundKey, goal.Id, task.Id, task.LastDispatch.DispatchedAt, inputHash, inputs, run, stopwatch));
        state.FiredCount++;
        lines.Add($"GLANCE goal={Short(goal.Id.Value)} task={Short(task.Id.Value)} result=started trigger={trigger.Value} inputHash={inputHash}");
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
            var result = Complete(running);
            var inputTokens = result.InputTokens ?? EstimateTokens(BuildPrompt(running.Inputs));
            var outputTokens = result.OutputTokens ?? EstimateTokens(result.Note + result.EvidenceLine);
            var totalTokens = inputTokens + outputTokens;

            TryAppendReceipt(running, result, inputTokens, outputTokens, totalTokens, lines);

            UpdateSummary(running, result, totalTokens);
            var summary = _summaries[running.GoalId.Value];
            TryAppendSummary(running, summary, lines);

            if (result.Verdict == ProgressiveReviewGlanceVerdict.Concern)
            {
                GetRoundState(running.RoundKey).QueuedConcerns.Add(result.Note);
            }
            else if (result.Verdict == ProgressiveReviewGlanceVerdict.FundamentalMisdirection)
            {
                TryRaiseMisdirectionAttention(running, result, lines);
            }

            lines.Add($"GLANCE goal={Short(running.GoalId.Value)} task={Short(running.TaskId.Value)} result=receipt verdict={result.Verdict} tokens={totalTokens} wall_ms={(long)running.Stopwatch.Elapsed.TotalMilliseconds}");
        }

        return mutated;
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

            if (task.LastVerification?.Succeeded is false)
            {
                kernel.RecordCriterionRetryFeedback(
                    goal.Id,
                    task.Id,
                    task.CriterionRetryFeedback.Concat(state.QueuedConcerns.Select(note => $"Progressive review glance concern: {note}")).ToArray());
                state.ConcernsSurfaced = true;
                mutated = true;
            }
            else if (task.LastVerification?.Succeeded is true || task.Status != WorkTaskStatus.Running)
            {
                state.QueuedConcerns.Clear();
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

    private static string HashInputs(ProgressiveReviewGlanceInputs inputs)
    {
        var text = JsonSerializer.Serialize(inputs, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
    }

    private IReadOnlyList<string> BoundChangedFiles(DispatchLiveChangeSnapshot snapshot) =>
        BoundList(
            snapshot.DisplayFiles.Count > 0 ? snapshot.DisplayFiles : snapshot.Files,
            _options.ChangedFilePromptLimit,
            _options.ChangedFileListCharacterLimit,
            ChangedFileLabel,
            snapshot.RemainingFileCount);

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

    private static string ReadDiff(string workingDirectory, string? baseCommit)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return "diff unavailable: missing working directory";
        }

        var committed = string.IsNullOrWhiteSpace(baseCommit)
            ? string.Empty
            : GitCli.Run(workingDirectory, "diff", $"{baseCommit}..HEAD", "--").Output;
        var working = GitCli.Run(workingDirectory, "diff", "--").Output;
        var staged = GitCli.Run(workingDirectory, "diff", "--cached", "--").Output;
        var combined = string.Join(Environment.NewLine, new[] { committed, staged, working }.Where(text => !string.IsNullOrWhiteSpace(text)));
        return string.IsNullOrWhiteSpace(combined) ? "(no diff content available)" : combined;
    }

    private static string ReadTranscriptTail(TaskProcessRecord? process)
    {
        if (process is null)
        {
            return "(no process transcript yet)";
        }

        var snapshot = ProcessLogReader.Read(process);
        return string.Join(Environment.NewLine, [
            "stdout:",
            snapshot.StandardOutput,
            "stderr:",
            snapshot.StandardError]);
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
        string InputHash,
        ProgressiveReviewGlanceInputs Inputs,
        Task<ProgressiveReviewGlanceDispatchResult> Task,
        Stopwatch Stopwatch);

    private sealed class RoundState
    {
        public RoundState(TimeSpan firstElapsedThreshold) => NextElapsedThreshold = firstElapsedThreshold;

        public int FiredCount { get; set; }
        public bool FileCountTriggered { get; set; }
        public TimeSpan NextElapsedThreshold { get; set; }
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
