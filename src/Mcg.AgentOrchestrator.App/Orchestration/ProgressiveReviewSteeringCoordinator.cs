using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    int MaxResumeTranscriptTokens = 64000,
    bool WriteTerminalCancelProofArtifacts = true)
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
    private readonly Func<TaskProcessRecord, IReadOnlyList<int>> _getLineageDescendants;
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
        Func<TaskProcessRecord, IReadOnlyList<int>>? getLineageDescendants = null,
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
        _getLineageDescendants = getLineageDescendants ?? GetLiveLineageDescendants;
        _cancelProcess = cancelProcess ?? ((kernel, goalId, taskId) => new BackgroundDispatchRunner().CancelLatestProcess(kernel, goalId, taskId));
        _startProcess = startProcess ?? ((kernel, goalId, taskId) => new BackgroundDispatchRunner().StartLatestDispatch(kernel, goalId, taskId, workspace.LogDirectory));
        _prepareFreshDispatch = prepareFreshDispatch ?? PrepareRawFreshSubscriptionDispatch;
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

        try
        {
            return ExecuteReservedIntent(kernel, goal, lines, intent);
        }
        catch (Exception ex)
        {
            var now = _utcNow();
            var reason = $"steering exception: {ProgressiveReviewGlanceCoordinator.BoundSingleLineForSteering(ex.Message, 300)}";
            AppendFailSafeReceipt(intent, reason, "operator-attention", now);
            RaiseAttention(intent, reason);
            _store.CompleteIntentAsync(intent.Id, now).GetAwaiter().GetResult();
            lines.Add($"STEER goal={Short(intent.GoalId)} task={Short(intent.TaskId)} result=operator-attention reason=exception");
            return new ProgressiveReviewSteeringResult(true, lines);
        }
    }

    private ProgressiveReviewSteeringResult ExecuteReservedIntent(
        AgentOrchestratorKernel kernel,
        Goal goal,
        List<string> lines,
        ProgressiveReviewSteerIntent intent)
    {
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
        var cancelTimeOwnedProcessSet = CaptureCancelTimeOwnedProcessSet(originalProcess);
        var cancelled = _cancelProcess(kernel, goal.Id, taskId);
        EnsureTerminalCancelProofArtifacts(cancelled, cancelTimeOwnedProcessSet, now);
        var cancelConfirmation = ConfirmTreeDead(cancelled, cancelTimeOwnedProcessSet);
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
        TaskProcessRecord started;
        try
        {
            if (admission.AllowsResume)
                PrepareWarmResumeDispatch(kernel, refreshedGoal, refreshedTask, originalDispatch, intent.GuidanceText);
            else
                PrepareFreshDispatchWithGuidance(kernel, refreshedGoal, refreshedTask, intent.GuidanceText);

            started = _startProcess(kernel, goal.Id, taskId);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var failureReceipt = BuildReceipt(
                intent,
                cancelConfirmation.Proof,
                "operator-attention",
                admission.Checks.Select(check => $"{check.Kind}:{check.Status}:{check.Reason}").Concat([$"RestartDecision:{decision}"]).ToArray(),
                originalDispatch,
                originalProcess,
                null,
                stopwatch.Elapsed,
                $"steer-restart-failed: {ProgressiveReviewGlanceCoordinator.BoundSingleLineForSteering(ex.Message, 300)}",
                _utcNow());
            _store.AppendReceiptAsync(failureReceipt).GetAwaiter().GetResult();
            RaiseAttention(intent, $"Progressive-review steer restart failed after confirmed cancel ({decision}): {ex.Message}");
            _store.CompleteIntentAsync(intent.Id, _utcNow()).GetAwaiter().GetResult();
            lines.Add($"STEER goal={Short(intent.GoalId)} task={Short(intent.TaskId)} result=operator-attention reason=restart-failed receipt={failureReceipt.Id}");
            return new ProgressiveReviewSteeringResult(true, lines);
        }
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
        var currentWorktree = _workspace.ResolveExecutionDirectory(goal.Id);
        var currentHead = TryResolveHead(currentWorktree);
        var requestedProvider = ResolveCurrentProviderKind(task);
        var requestedModel = ResolveCurrentModelName(goal, task);
        var latestIntegrationChange = LatestIntegrationChangeAfter(_workspace.GoalLifecycleEventsDirectory, goal.Id, originalDispatch.DispatchedAt);
        var context = new InquiryAdmissionContext(
            goal.Id,
            task.Id,
            AgentRole.Developer,
            requestedProvider,
            requestedModel,
            currentWorktree,
            currentHead,
            CapturedHeadIsAncestor(currentWorktree, originalDispatch.WorktreeHeadSha, currentHead),
            _utcNow(),
            goal.EffectiveAcceptanceCriteriaCorrections
                .Where(correction => correction.RecordedAt > originalDispatch.DispatchedAt)
                .Select(correction => (DateTimeOffset?)correction.RecordedAt)
                .FirstOrDefault(),
            latestIntegrationChange,
            false,
            Math.Max(1, EstimateTokens(ProgressiveReviewGlanceCoordinator.ReadTranscriptTail(originalProcess))));
        var decision = InquiryResumeAdmission.Evaluate(
            goal,
            task,
            originalDispatch,
            context,
            new InquiryAdmissionOptions(_options.EffectiveMaxResumeSessionAge, _options.MaxResumeTranscriptTokens));
        return decision with
        {
            Checks = decision.Checks.Concat([
                BuildAcceptanceCriteriaHashCheck(goal, originalDispatch),
                BuildBranchMovementCheck(originalDispatch, currentHead, context.CapturedHeadIsAncestorOfCurrentHead),
                BuildMainIntegrationMovementCheck(latestIntegrationChange, originalDispatch.DispatchedAt)
            ]).ToArray()
        };
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

    private void PrepareRawFreshSubscriptionDispatch(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string guidanceText)
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

    private void PrepareFreshDispatchWithGuidance(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string guidanceText)
    {
        _prepareFreshDispatch(kernel, goal, task, guidanceText);
        var prepared = kernel.GetTask(goal.Id, task.Id).LastDispatch
            ?? throw new InvalidOperationException("Fresh steering fallback did not produce a dispatch record.");
        var freshPrompt = string.IsNullOrWhiteSpace(prepared.PromptPath) || !File.Exists(prepared.PromptPath)
            ? string.Empty
            : File.ReadAllText(prepared.PromptPath);
        var guidedPrompt = string.IsNullOrWhiteSpace(freshPrompt)
            ? guidanceText
            : $"{guidanceText}{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}{freshPrompt}";
        var promptPath = WriteGuidancePrompt(goal.Id, task.Id, prepared.WorkerName, guidedPrompt);
        var command = RewritePromptPath(prepared.Command, prepared.PromptPath, promptPath);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            prepared with
            {
                Command = command,
                PromptPath = promptPath,
                PromptCharacterCount = guidedPrompt.Length,
                ModelSelectionReason = "progressive-review-steer:fresh-fallback"
            },
            allowPendingRecordedDispatchRefresh: true);
    }

    private ProgressiveReviewSteerReceipt BuildReceipt(
        ProgressiveReviewSteerIntent intent,
        string cancelConfirmation,
        string decision,
        IReadOnlyList<string> admissionChecks,
        TaskDispatchRecord originalDispatch,
        TaskProcessRecord originalProcess,
        TaskProcessRecord? startedProcess,
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
            startedProcess?.Command.Length ?? intent.GuidanceText.Length,
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

    private IReadOnlyList<int> CaptureCancelTimeOwnedProcessSet(TaskProcessRecord process)
    {
        var processIds = new HashSet<int>();
        AddProcessId(processIds, process.ProcessId);
        foreach (var processId in process.TrackedProcessIds)
            AddProcessId(processIds, processId);

        var heartbeat = ProcessLogReader.ReadHeartbeat(process);
        if (heartbeat.IsAvailable)
        {
            AddProcessId(processIds, heartbeat.ProcessId);
            if (heartbeat.ChildProcessId is { } childProcessId)
                AddProcessId(processIds, childProcessId);

            foreach (var processId in heartbeat.OwnedProcessIds)
                AddProcessId(processIds, processId);
        }

        foreach (var processId in _getLineageDescendants(process))
            AddProcessId(processIds, processId);

        return processIds.OrderBy(processId => processId).ToArray();
    }

    private static void AddProcessId(HashSet<int> processIds, int processId)
    {
        if (processId > 0)
            processIds.Add(processId);
    }

    private TreeDeathConfirmation ConfirmTreeDead(TaskProcessRecord cancelled, IReadOnlyList<int> cancelTimeOwnedProcessSet)
    {
        if (!cancelled.WasCancelled || cancelled.CompletedAt is null)
            return new TreeDeathConfirmation(false, "cancelled process record missing terminal cancellation fields");

        var processIds = new HashSet<int>(cancelTimeOwnedProcessSet);
        var heartbeat = ProcessLogReader.ReadHeartbeat(cancelled, _utcNow());
        if (heartbeat.IsAvailable)
        {
            AddProcessId(processIds, heartbeat.ProcessId);
            if (heartbeat.ChildProcessId is { } childProcessId)
                AddProcessId(processIds, childProcessId);

            foreach (var processId in heartbeat.OwnedProcessIds)
                AddProcessId(processIds, processId);
        }

        foreach (var processId in _getLineageDescendants(cancelled))
            AddProcessId(processIds, processId);

        var observedPids = processIds.OrderBy(processId => processId).ToArray();
        var live = observedPids.Where(_isProcessRunning).Distinct().OrderBy(processId => processId).ToArray();
        if (live.Length > 0)
            return new TreeDeathConfirmation(false, $"owned pid(s) still alive: {string.Join(",", live)}");

        if (!File.Exists(cancelled.ExitCodePath))
            return new TreeDeathConfirmation(false, $"exit artifact missing: {cancelled.ExitCodePath}");

        if (!heartbeat.IsAvailable)
            return new TreeDeathConfirmation(false, $"terminal heartbeat unavailable: {heartbeat.UnavailableReason ?? "unknown"} at {heartbeat.Path}");

        if (!IsTerminalHeartbeat(heartbeat))
            return new TreeDeathConfirmation(false, $"heartbeat not terminal: state={heartbeat.State} child_pid={heartbeat.ChildProcessId?.ToString() ?? "unknown"}");

        return new TreeDeathConfirmation(
            true,
            $"tree-dead pids=[{string.Join(",", observedPids)}] heartbeat_state={heartbeat.State} child_pid=null exit_artifact={cancelled.ExitCodePath} cancelled_at={cancelled.CompletedAt:u}; partial dispatch receipt consumed");
    }

    private static bool IsTerminalHeartbeat(DispatchHeartbeatStatus heartbeat) =>
        heartbeat.ChildProcessId is null &&
        (heartbeat.State.Equals("exited", StringComparison.OrdinalIgnoreCase) ||
         heartbeat.State.Equals("exiting", StringComparison.OrdinalIgnoreCase) ||
         heartbeat.State.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
         heartbeat.State.Equals("cancelled", StringComparison.OrdinalIgnoreCase));

    private void EnsureTerminalCancelProofArtifacts(TaskProcessRecord cancelled, IReadOnlyList<int> cancelTimeOwnedProcessSet, DateTimeOffset now)
    {
        if (!_options.WriteTerminalCancelProofArtifacts)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(cancelled.ExitCodePath) ?? _workspace.LogDirectory);
        if (!File.Exists(cancelled.ExitCodePath))
            File.WriteAllText(cancelled.ExitCodePath, "1", Encoding.UTF8);

        var heartbeat = ProcessLogReader.ReadHeartbeat(cancelled, now);
        if (heartbeat.IsAvailable && IsTerminalHeartbeat(heartbeat))
            return;

        var heartbeatPath = BackgroundDispatchRunner.GetHeartbeatPath(cancelled);
        Directory.CreateDirectory(Path.GetDirectoryName(heartbeatPath) ?? _workspace.LogDirectory);
        var payload = new
        {
            pid = cancelled.ProcessId,
            childPid = (int?)null,
            ownedPids = cancelTimeOwnedProcessSet,
            startedAt = cancelled.StartedAt.ToString("O"),
            lastObservedAt = now.ToString("O"),
            lastProgressAt = now.ToString("O"),
            state = "cancelled",
            stdoutBytes = SafeLength(cancelled.StandardOutputPath),
            stderrBytes = SafeLength(cancelled.StandardErrorPath),
            ownedCpuMs = cancelled.ResourceAccounting?.CpuMilliseconds ?? 0L,
            providerSessionId = (string?)null,
            worktreeHeadSha = (string?)null,
            dirtyStateHash = (string?)null,
            exitFileExists = true
        };
        var tmp = heartbeatPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), new UTF8Encoding(false));
        File.Move(tmp, heartbeatPath, overwrite: true);
    }

    private static IReadOnlyList<int> GetLiveLineageDescendants(TaskProcessRecord process) =>
        process.ProcessId > 0 ? WorkerProcessJobs.ListLiveDescendantProcessIds(process.ProcessId) : [];

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

    private ProviderKind ResolveCurrentProviderKind(TaskSpec task)
    {
        var agent = ResolveAssignedAgent(task);
        var profileName = agent?.Subscription?.WorkerProfileName;
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            try
            {
                return WorkerProviderCatalog.Default().ResolveProfile(profileName).Identity.Kind;
            }
            catch
            {
                return ProviderKind.Unknown;
            }
        }

        return ProviderKind.Unknown;
    }

    private string? ResolveCurrentModelName(Goal goal, TaskSpec task)
    {
        var agent = ResolveAssignedAgent(task);
        if (agent is null)
            return null;

        if (!string.IsNullOrWhiteSpace(agent.Subscription?.ModelAlias))
            return agent.Subscription.ModelAlias;

        var complexity = task.LastDispatch?.TaskComplexity ??
            TaskComplexityEstimator.Estimate(task.Description, goal.Objective, task.RequiredRole);
        return complexity == TaskComplexity.Complex && agent.ComplexModel is not null
            ? agent.ComplexModel.ModelName
            : agent.Model.ModelName;
    }

    private AgentDefinition? ResolveAssignedAgent(TaskSpec task)
    {
        if (task.AssignedAgentId is not null)
        {
            var assigned = _agents.FirstOrDefault(agent => agent.Id == task.AssignedAgentId);
            if (assigned is not null)
                return assigned;
        }

        return _agents.FirstOrDefault(agent => agent.Role == task.RequiredRole);
    }

    private static DateTimeOffset? LatestIntegrationChangeAfter(string eventsDirectory, GoalId goalId, DateTimeOffset cutoff)
    {
        var path = Path.Combine(eventsDirectory, $"{goalId.Value}.jsonl");
        if (!File.Exists(path))
            return null;

        DateTimeOffset? latest = null;
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var eventType = root.TryGetProperty("eventType", out var eventTypeProperty)
                    ? eventTypeProperty.GetString()
                    : null;
                if (!IsIntegrationChangeEvent(eventType, root))
                    continue;

                if (root.TryGetProperty("timestamp", out var timestampProperty) &&
                    timestampProperty.TryGetDateTimeOffset(out var timestamp) &&
                    timestamp > cutoff &&
                    (latest is null || timestamp > latest))
                {
                    latest = timestamp;
                }
            }
            catch (JsonException)
            {
            }
        }

        return latest;
    }

    private static bool IsIntegrationChangeEvent(string? eventType, JsonElement root)
    {
        if (string.Equals(eventType, "GoalLanded", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(eventType, "GoalEscalated", StringComparison.OrdinalIgnoreCase))
            return false;

        return root.TryGetProperty("source", out var source) &&
            source.ValueKind == JsonValueKind.String &&
            source.GetString()?.Contains("integration", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static InquiryAdmissionCheck BuildAcceptanceCriteriaHashCheck(Goal goal, TaskDispatchRecord originalDispatch)
    {
        if (goal.RefinedSpec is null)
            return Pass(InquiryAdmissionCheckKind.AcceptanceCriteriaHash, "goal has no refined acceptance criteria hash to compare");

        if (string.IsNullOrWhiteSpace(originalDispatch.PromptPath) || !File.Exists(originalDispatch.PromptPath))
            return Fail(InquiryAdmissionCheckKind.AcceptanceCriteriaHash, "spawn prompt unavailable; cannot compare acceptance criteria hash");

        var currentCriteria = goal.RefinedSpec.AcceptanceCriteria
            .Select(criterion => criterion.Trim())
            .ToArray();
        var currentHash = HashText(string.Join("\n", currentCriteria));
        var prompt = File.ReadAllText(originalDispatch.PromptPath);
        if (prompt.Contains(currentHash, StringComparison.OrdinalIgnoreCase))
            return Pass(InquiryAdmissionCheckKind.AcceptanceCriteriaHash, $"acceptance criteria hash {currentHash[..16]} unchanged from spawn prompt");

        var spawnedCriteria = ExtractSpawnAcceptanceCriteria(prompt);
        return spawnedCriteria.SequenceEqual(currentCriteria, StringComparer.Ordinal)
            ? Pass(InquiryAdmissionCheckKind.AcceptanceCriteriaHash, $"acceptance criteria snapshot matches current hash {currentHash[..16]}")
            : Fail(InquiryAdmissionCheckKind.AcceptanceCriteriaHash, $"acceptance criteria snapshot differs from current hash {currentHash[..16]}");
    }

    private static IReadOnlyList<string> ExtractSpawnAcceptanceCriteria(string prompt)
    {
        var markerIndex = prompt.IndexOf("Acceptance criteria:", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return [];

        var afterMarker = prompt[(markerIndex + "Acceptance criteria:".Length)..];
        var criteria = new List<string>();
        foreach (var rawLine in afterMarker.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                if (criteria.Count > 0)
                    break;

                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal) ||
                line.EndsWith(":", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                criteria.Add(line[2..].Trim());
                continue;
            }

            if (criteria.Count > 0)
                break;
        }

        return criteria;
    }

    private static InquiryAdmissionCheck BuildBranchMovementCheck(TaskDispatchRecord originalDispatch, string? currentHead, bool capturedHeadIsAncestor)
    {
        if (string.IsNullOrWhiteSpace(originalDispatch.WorktreeHeadSha) || string.IsNullOrWhiteSpace(currentHead))
            return Fail(InquiryAdmissionCheckKind.BranchHeadMovement, "branch movement cannot be compared without captured and current HEAD");

        return capturedHeadIsAncestor
            ? Pass(InquiryAdmissionCheckKind.BranchHeadMovement, $"captured branch HEAD {originalDispatch.WorktreeHeadSha} remains an ancestor of current HEAD {currentHead}")
            : Fail(InquiryAdmissionCheckKind.BranchHeadMovement, $"captured branch HEAD {originalDispatch.WorktreeHeadSha} is not an ancestor of current HEAD {currentHead}");
    }

    private static InquiryAdmissionCheck BuildMainIntegrationMovementCheck(DateTimeOffset? latestIntegrationChange, DateTimeOffset dispatchedAt)
    {
        return latestIntegrationChange is null || latestIntegrationChange <= dispatchedAt
            ? Pass(InquiryAdmissionCheckKind.MainIntegrationMovement, "no branch/main integration movement recorded after spawn")
            : Fail(InquiryAdmissionCheckKind.MainIntegrationMovement, $"branch/main integration movement recorded after spawn: {latestIntegrationChange:u}");
    }

    private static InquiryAdmissionCheck Pass(InquiryAdmissionCheckKind kind, string reason) =>
        new(kind, InquiryAdmissionCheckStatus.Passed, reason);

    private static InquiryAdmissionCheck Fail(InquiryAdmissionCheckKind kind, string reason) =>
        new(kind, InquiryAdmissionCheckStatus.Failed, reason);

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

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch
        {
            return 0L;
        }
    }

    private static string RewritePromptPath(string command, string? oldPromptPath, string newPromptPath)
    {
        if (string.IsNullOrWhiteSpace(oldPromptPath))
            return command;

        return command.Replace(Quote(oldPromptPath), Quote(newPromptPath), StringComparison.OrdinalIgnoreCase)
            .Replace(oldPromptPath, newPromptPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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
