using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ProgressiveReviewSteeringResult(
    bool MutatedTaskState,
    IReadOnlyList<string> ProgressLines)
{
    public static ProgressiveReviewSteeringResult None { get; } = new(false, []);
}

internal sealed record ProgressiveReviewSteeringOptions(
    int PerRoundSteerCap = 1,
    TimeSpan? MaxResumeSessionAge = null,
    int MaxResumeTranscriptTokens = 64000)
{
    public TimeSpan EffectiveMaxResumeSessionAge => MaxResumeSessionAge ?? TimeSpan.FromMinutes(60);
}

internal sealed class ProgressiveReviewSteeringCoordinator
{
    private readonly OrchestratorWorkspace _workspace;
    private readonly IReadOnlyList<AgentDefinition> _agents;
    private readonly WorkerProfileCatalog _profiles;
    private readonly IModelProviderRegistry _providers;
    private readonly IProgressiveReviewSteeringStore _store;
    private readonly ICollaborationItemStore _collaborationStore;
    private readonly ProgressiveReviewSteeringOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<int, bool> _isProcessRunning;
    private readonly Func<AgentOrchestratorKernel, GoalId, TaskId, TaskProcessRecord> _cancelProcess;
    private readonly Func<AgentOrchestratorKernel, GoalId, TaskId, TaskProcessRecord> _startProcess;
    private readonly Action<AgentOrchestratorKernel, Goal, TaskSpec, string> _prepareFreshDispatch;

    public ProgressiveReviewSteeringCoordinator(
        OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        IModelProviderRegistry providers,
        IProgressiveReviewSteeringStore? store = null,
        ICollaborationItemStore? collaborationStore = null,
        ProgressiveReviewSteeringOptions? options = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<int, bool>? isProcessRunning = null,
        Func<AgentOrchestratorKernel, GoalId, TaskId, TaskProcessRecord>? cancelProcess = null,
        Func<AgentOrchestratorKernel, GoalId, TaskId, TaskProcessRecord>? startProcess = null,
        Action<AgentOrchestratorKernel, Goal, TaskSpec, string>? prepareFreshDispatch = null)
    {
        _workspace = workspace;
        _agents = agents;
        _profiles = profiles;
        _providers = providers;
        _store = store ?? SqliteProgressiveReviewSteeringStore.ForDirectory(workspace.OrchestratorDirectory);
        _collaborationStore = collaborationStore ?? CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        _options = options ?? new ProgressiveReviewSteeringOptions();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _isProcessRunning = isProcessRunning ?? IsProcessRunning;
        _cancelProcess = cancelProcess ?? ((kernel, goalId, taskId) => new BackgroundDispatchRunner().CancelLatestProcess(kernel, goalId, taskId));
        _startProcess = startProcess ?? ((kernel, goalId, taskId) => new BackgroundDispatchRunner().StartLatestDispatch(kernel, goalId, taskId, workspace.LogDirectory));
        _prepareFreshDispatch = prepareFreshDispatch ?? PrepareFreshSubscriptionDispatch;
    }

    public static ProgressiveReviewSteeringCoordinator CreateDefault(
        OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        IModelProviderRegistry providers)
    {
        return new ProgressiveReviewSteeringCoordinator(workspace, agents, profiles, providers);
    }

    public ProgressiveReviewSteeringResult ExecutePending(AgentOrchestratorKernel kernel, Goal goal)
    {
        var lines = new List<string>();
        try
        {
            return ExecutePendingCore(kernel, goal, lines);
        }
        catch (Exception ex)
        {
            lines.Add($"STEER goal={Short(goal.Id.Value)} result=advisory-error error={ProgressiveReviewGlanceCoordinator.BoundSingleLineForSteering(ex.Message, 300)}");
            return new ProgressiveReviewSteeringResult(false, lines);
        }
    }

    private ProgressiveReviewSteeringResult ExecutePendingCore(AgentOrchestratorKernel kernel, Goal goal, List<string> lines)
    {
        var intent = _store.ReserveNextPendingAsync(goal.Id.Value).GetAwaiter().GetResult();
        if (intent is null)
            return ProgressiveReviewSteeringResult.None;

        var taskId = new TaskId(intent.TaskId);
        var now = _utcNow();
        var priorReceipts = _store.CountReceiptsForRoundAsync(intent.RoundKey).GetAwaiter().GetResult();
        if (priorReceipts >= _options.PerRoundSteerCap)
        {
            AppendFailSafeReceipt(intent, "steer-cap-reached", "operator-attention", now);
            RaiseAttention(intent, "Per-round progressive-review steer cap reached; no second steer attempted.");
            _store.CompleteIntentAsync(intent.Id, now).GetAwaiter().GetResult();
            lines.Add($"STEER goal={Short(intent.GoalId)} task={Short(intent.TaskId)} result=operator-attention reason=steer-cap");
            return new ProgressiveReviewSteeringResult(true, lines);
        }

        var task = kernel.GetTask(goal.Id, taskId);
        if (task.LastDispatch is null || task.LastProcess is null || task.Status != WorkTaskStatus.Running)
        {
            AppendFailSafeReceipt(intent, "running dispatch unavailable", "operator-attention", now);
            RaiseAttention(intent, "Misdirection steer requested, but the target Developer dispatch is no longer live.");
            _store.CompleteIntentAsync(intent.Id, now).GetAwaiter().GetResult();
            lines.Add($"STEER goal={Short(intent.GoalId)} task={Short(intent.TaskId)} result=operator-attention reason=not-live");
            return new ProgressiveReviewSteeringResult(true, lines);
        }

        var originalDispatch = task.LastDispatch;
        var originalProcess = task.LastProcess;
        var trackedPids = originalProcess.TrackedProcessIds.ToArray();
        var cancelled = _cancelProcess(kernel, goal.Id, taskId);
        var cancelConfirmation = ConfirmTreeDead(cancelled, trackedPids);
        if (!cancelConfirmation.Confirmed)
        {
            AppendFailSafeReceipt(intent, cancelConfirmation.Proof, "operator-attention", now);
            RaiseAttention(intent, $"Progressive-review steer suppressed because tree-death confirmation failed: {cancelConfirmation.Proof}");
            _store.CompleteIntentAsync(intent.Id, now).GetAwaiter().GetResult();
            lines.Add($"STEER goal={Short(intent.GoalId)} task={Short(intent.TaskId)} result=operator-attention reason=tree-death-unconfirmed");
            return new ProgressiveReviewSteeringResult(true, lines);
        }

        kernel.RecordTaskNote(goal.Id, taskId, intent.GuidanceText);
        kernel.RequeueInterruptedDispatch(goal.Id, taskId, "ProgressiveReviewSteer: cancelled misdirected Developer dispatch; restarting with guidance.");
        var refreshedGoal = kernel.GetGoal(goal.Id);
        var refreshedTask = refreshedGoal.Tasks.Single(candidate => candidate.Id == taskId);
        var admission = BuildAdmission(refreshedGoal, refreshedTask, originalDispatch, originalProcess, intent);
        var stopwatch = Stopwatch.StartNew();
        var decision = admission.AllowsResume ? "warm-resume" : "fresh-dispatch";
        if (admission.AllowsResume)
            PrepareWarmResumeDispatch(kernel, refreshedGoal, refreshedTask, originalDispatch, intent.GuidanceText);
        else
            _prepareFreshDispatch(kernel, refreshedGoal, refreshedTask, intent.GuidanceText);

        var started = _startProcess(kernel, goal.Id, taskId);
        stopwatch.Stop();

        var receipt = BuildReceipt(
            intent,
            cancelConfirmation.Proof,
            decision,
            admission.Checks.Select(check => $"{check.Kind}:{check.Status}:{check.Reason}").ToArray(),
            originalDispatch,
            originalProcess,
            started,
            stopwatch.Elapsed,
            "steer-started",
            _utcNow());
        _store.AppendReceiptAsync(receipt).GetAwaiter().GetResult();
        _store.CompleteIntentAsync(intent.Id, _utcNow()).GetAwaiter().GetResult();
        lines.Add($"STEER goal={Short(intent.GoalId)} task={Short(intent.TaskId)} result={decision} receipt={receipt.Id}");
        return new ProgressiveReviewSteeringResult(true, lines);
    }

    private InquiryAdmissionDecision BuildAdmission(
        Goal goal,
        TaskSpec task,
        TaskDispatchRecord originalDispatch,
        TaskProcessRecord originalProcess,
        ProgressiveReviewSteerIntent intent)
    {
        var currentHead = TryResolveHead(originalDispatch.WorkingDirectory);
        var context = new InquiryAdmissionContext(
            goal.Id,
            task.Id,
            AgentRole.Developer,
            originalDispatch.WorkerProviderKind,
            originalDispatch.WorkingDirectory,
            currentHead,
            CapturedHeadIsAncestor(originalDispatch.WorkingDirectory, originalDispatch.WorktreeHeadSha, currentHead),
            _utcNow(),
            goal.EffectiveAcceptanceCriteriaCorrections
                .Where(correction => correction.RecordedAt > originalDispatch.DispatchedAt)
                .Select(correction => (DateTimeOffset?)correction.RecordedAt)
                .FirstOrDefault(),
            null,
            false,
            Math.Max(1, EstimateTokens(ProgressiveReviewGlanceCoordinator.ReadTranscriptTail(originalProcess))));
        return InquiryResumeAdmission.Evaluate(
            goal,
            task,
            originalDispatch,
            context,
            new InquiryAdmissionOptions(_options.EffectiveMaxResumeSessionAge, _options.MaxResumeTranscriptTokens));
    }

    private void PrepareWarmResumeDispatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        TaskDispatchRecord originalDispatch,
        string guidanceText)
    {
        var promptPath = WriteGuidancePrompt(goal.Id, task.Id, originalDispatch.WorkerName, guidanceText);
        var command = BuildWarmResumeCommand(originalDispatch);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            originalDispatch.WorkerName,
            command,
            originalDispatch.WorkingDirectory,
            _utcNow(),
            originalDispatch.ProviderName,
            originalDispatch.ModelName,
            originalDispatch.ReasoningEffort,
            originalDispatch.TaskComplexity,
            guidanceText.Length,
            originalDispatch.UsesComplexModel,
            PromptPath: promptPath,
            WorkerProviderKind: originalDispatch.WorkerProviderKind,
            ReasoningEffortReason: originalDispatch.ReasoningEffortReason,
            DispatchLane: originalDispatch.DispatchLane,
            ModelSelectionReason: "progressive-review-steer:warm-resume"));
    }

    private void PrepareFreshSubscriptionDispatch(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string guidanceText)
    {
        _ = guidanceText;
        GoalManagementCommandService.SubscriptionDispatchTask(
            kernel,
            _workspace,
            goal,
            task,
            _agents,
            _profiles,
            providers: _providers);
    }

    private ProgressiveReviewSteerReceipt BuildReceipt(
        ProgressiveReviewSteerIntent intent,
        string cancelConfirmation,
        string decision,
        IReadOnlyList<string> admissionChecks,
        TaskDispatchRecord originalDispatch,
        TaskProcessRecord originalProcess,
        TaskProcessRecord startedProcess,
        TimeSpan steeredWall,
        string outcome,
        DateTimeOffset createdAt)
    {
        var cancelledWall = originalProcess.CompletedAt is { } completed
            ? completed - originalProcess.StartedAt
            : _utcNow() - originalDispatch.DispatchedAt;
        return new ProgressiveReviewSteerReceipt(
            Guid.NewGuid().ToString("n"),
            intent.Id,
            intent.GoalId,
            intent.TaskId,
            intent.RoundKey,
            intent.TriggerGlanceId,
            intent.InputsHash,
            intent.MisdirectionEvidence,
            cancelConfirmation,
            decision,
            admissionChecks,
            intent.GuidanceText,
            originalDispatch.PromptCharacterCount ?? 0,
            0,
            startedProcess.Command.Length,
            0,
            Math.Max(0, (long)cancelledWall.TotalMilliseconds),
            Math.Max(0, (long)steeredWall.TotalMilliseconds),
            outcome,
            createdAt);
    }

    private void AppendFailSafeReceipt(ProgressiveReviewSteerIntent intent, string cancelConfirmation, string outcome, DateTimeOffset now)
    {
        _store.AppendReceiptAsync(new ProgressiveReviewSteerReceipt(
            Guid.NewGuid().ToString("n"),
            intent.Id,
            intent.GoalId,
            intent.TaskId,
            intent.RoundKey,
            intent.TriggerGlanceId,
            intent.InputsHash,
            intent.MisdirectionEvidence,
            cancelConfirmation,
            "operator-attention",
            ["not-attempted"],
            intent.GuidanceText,
            0,
            0,
            0,
            0,
            0,
            0,
            outcome,
            now)).GetAwaiter().GetResult();
    }

    private TreeDeathConfirmation ConfirmTreeDead(TaskProcessRecord cancelled, IReadOnlyList<int> trackedPids)
    {
        if (!cancelled.WasCancelled || cancelled.CompletedAt is null)
            return new TreeDeathConfirmation(false, "cancelled process record missing terminal cancellation fields");

        var live = trackedPids.Where(_isProcessRunning).Distinct().ToArray();
        if (live.Length > 0)
            return new TreeDeathConfirmation(false, $"owned pid(s) still alive: {string.Join(",", live)}");

        return new TreeDeathConfirmation(true, $"tree-dead pids=[{string.Join(",", trackedPids)}] cancelled_at={cancelled.CompletedAt:u}; partial dispatch receipt consumed");
    }

    private void RaiseAttention(ProgressiveReviewSteerIntent intent, string reason)
    {
        var subject = $"Progressive review steer attention: {Short(intent.GoalId)}";
        var body = $"""
Goal: {intent.GoalId}
Task: {intent.TaskId}
Glance: {intent.TriggerGlanceId}
Reason: {reason}
Evidence: {intent.MisdirectionEvidence}
""";
        _collaborationStore.RaiseAsync(
            CollaborationItemType.Decision,
            intent.GoalId,
            subject,
            body,
            $"progressive-review-steer:{intent.RoundKey}:{intent.Id}",
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private string WriteGuidancePrompt(GoalId goalId, TaskId taskId, string workerName, string guidanceText)
    {
        Directory.CreateDirectory(_workspace.PromptDirectory);
        var path = Path.Combine(
            _workspace.PromptDirectory,
            $"{Short(goalId.Value)}-{Short(taskId.Value)}-{_utcNow():yyyyMMddHHmmssfffffff}-{Sanitize(workerName)}-steer.md");
        File.WriteAllText(path, guidanceText, new UTF8Encoding(false));
        return path;
    }

    private static string BuildWarmResumeCommand(TaskDispatchRecord dispatch)
    {
        var sessionId = dispatch.ProviderSessionId ?? throw new InvalidOperationException("Warm resume requires provider session id.");
        var model = string.IsNullOrWhiteSpace(dispatch.ModelName) ? string.Empty : $" --model {Quote(dispatch.ModelName)}";
        return dispatch.WorkerProviderKind switch
        {
            ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexSpark => BuildCodexResumeCommand(dispatch, model, sessionId),
            ProviderKind.AnthropicClaudeCli => $"claude{model} --permission-mode bypassPermissions -p --resume {Quote(sessionId)}",
            _ => throw new InvalidOperationException($"Progressive review steering supports codex and claude subscription lanes only; dispatch provider was {dispatch.WorkerProviderKind}.")
        };
    }

    private static string BuildCodexResumeCommand(TaskDispatchRecord dispatch, string model, string sessionId)
    {
        var reasoning = string.IsNullOrWhiteSpace(dispatch.ReasoningEffort)
            ? string.Empty
            : $" -c model_reasoning_effort={Quote(dispatch.ReasoningEffort)}";
        return $"codex exec --skip-git-repo-check{model}{reasoning} --sandbox workspace-write --cd {Quote(dispatch.WorkingDirectory)} resume {Quote(sessionId)} -";
    }

    private static string? TryResolveHead(string worktree)
    {
        try
        {
            var result = GitCli.Run(worktree, "rev-parse", "HEAD");
            return result.ExitCode == 0 ? result.Output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool CapturedHeadIsAncestor(string worktree, string? capturedHead, string? currentHead)
    {
        if (string.IsNullOrWhiteSpace(capturedHead) || string.IsNullOrWhiteSpace(currentHead))
            return false;

        try
        {
            return GitCli.Run(worktree, "merge-base", "--is-ancestor", capturedHead, currentHead).ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static int EstimateTokens(string text) => Math.Max(1, text.Length / 4);

    private static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        return sb.ToString().Trim('-');
    }

    private sealed record TreeDeathConfirmation(bool Confirmed, string Proof);
}
