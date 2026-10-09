using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public enum FailureTriageCause
{
    SubscriptionRetryAfter,
    SubscriptionLimitReview,
    ProviderConnectivity,
    ProviderModelRejected,
    ProgressStall,
    MissingWorkerPermissions,
    LargePromptGuard,
    BuildFileLock,
    DirtyWorktree,
    StaleBranch,
    FailedVerification,
    MissingVerification,
    NoFileChangeCompletion,
    None,
    ProviderBudgetExhausted
}

public enum FailureTriageAction
{
    Wait,
    ReRoute,
    RunBuildServerShutdown,
    RequestHumanInput,
    RetryWithNote,
    Verify,
    Accept,
    ParkGoal,
    Monitor
}

public sealed record FailureTriageReport(
    GoalId GoalId,
    string GoalPrefix,
    string PolicyName,
    IReadOnlyList<FailureTriageItem> Items);

public sealed record FailureTriageItem(
    int? TaskNumber,
    TaskId? TaskId,
    FailureTriageCause Cause,
    FailureTriageAction Action,
    AutonomyAction? PolicyAction,
    bool PolicyAllows,
    bool CanAutoApply,
    bool RequiresOperatorGate,
    string Explanation,
    string SuggestedCommand);

public static class FailureTriagePlanner
{
    public static FailureTriageReport Build(
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        string executionDirectory,
        string integrationBranch,
        AutonomyPolicy policy,
        DateTimeOffset? now = null)
    {
        var observedAt = now ?? DateTimeOffset.UtcNow;
        var items = new List<FailureTriageItem>();
        var recovery = GoalRecoveryPlanner.Build(kernel, goal, executionDirectory, integrationBranch);

        for (var index = 0; index < goal.Tasks.Count; index++)
        {
            var task = goal.Tasks[index];
            AddTaskItems(items, task, index + 1, agents, policy, observedAt);
        }

        if (recovery.WorktreeDirty == true)
        {
            items.Add(new FailureTriageItem(
                null,
                null,
                FailureTriageCause.DirtyWorktree,
                FailureTriageAction.ParkGoal,
                null,
                PolicyAllows: false,
                CanAutoApply: false,
                RequiresOperatorGate: true,
                "Goal worktree has uncommitted changes; inspect or commit them before more automation.",
                "goal-recovery"));
        }

        var acceptance = AcceptanceQueuePlanner.Build(kernel, executionDirectory, policy, integrationBranch)
            .Items
            .FirstOrDefault(item => item.GoalId == goal.Id);
        if (acceptance?.Disposition == AcceptanceQueueDisposition.Held &&
            acceptance.Reason.Contains("cannot fast-forward", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new FailureTriageItem(
                null,
                null,
                FailureTriageCause.StaleBranch,
                FailureTriageAction.RequestHumanInput,
                AutonomyAction.Acceptance,
                policy.Allows(AutonomyAction.Acceptance),
                CanAutoApply: false,
                RequiresOperatorGate: true,
                acceptance.Reason,
                acceptance.SuggestedCommand));
        }

        if (items.Count == 0)
        {
            items.Add(new FailureTriageItem(
                null,
                null,
                FailureTriageCause.None,
                FailureTriageAction.Monitor,
                AutonomyAction.Refresh,
                policy.Allows(AutonomyAction.Refresh),
                CanAutoApply: false,
                RequiresOperatorGate: false,
                "No known failure condition is currently present.",
                "monitor"));
        }

        return new FailureTriageReport(goal.Id, goal.Id.Value[..8], policy.Name, items);
    }

    private static void AddTaskItems(
        List<FailureTriageItem> items,
        TaskSpec task,
        int taskNumber,
        IReadOnlyList<AgentDefinition> agents,
        AutonomyPolicy policy,
        DateTimeOffset now)
    {
        if (DispatchFailureClassifier.HasRecoverableProviderConnectivityFailure(task) &&
            agents.Any(agent =>
                agent.Role == task.RequiredRole &&
                agent.Status == AgentStatus.Available &&
                agent.Id != task.AssignedAgentId))
        {
            AddFailoverItem(items, task, taskNumber, agents, policy, FailureTriageCause.ProviderConnectivity);
            return;
        }

        if (DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, now, out var retryAfter))
        {
            items.Add(Item(
                task,
                taskNumber,
                FailureTriageCause.SubscriptionRetryAfter,
                FailureTriageAction.Wait,
                null,
                policyAllows: false,
                canAutoApply: false,
                gate: false,
                $"Subscription retry-after is active until {retryAfter:u}.",
                $"wait until {retryAfter:u}"));
            return;
        }

        if (DispatchFailureClassifier.RequiresSubscriptionLimitReview(task))
        {
            items.Add(Item(
                task,
                taskNumber,
                FailureTriageCause.SubscriptionLimitReview,
                FailureTriageAction.RequestHumanInput,
                null,
                policyAllows: false,
                canAutoApply: false,
                gate: true,
                "Repeated subscription usage-limit failures require an operator review note before another dispatch.",
                $"subscription-dispatch {taskNumber} --confirm-limit-review --text-file <path>"));
            return;
        }

        if (DispatchFailureClassifier.HasProviderNeutralProgressStallFailure(task))
        {
            AddFailoverItem(items, task, taskNumber, agents, policy, FailureTriageCause.ProgressStall);
            return;
        }

        if (DispatchFailureClassifier.HasRecoverableProviderConnectivityFailure(task))
        {
            AddFailoverItem(items, task, taskNumber, agents, policy, FailureTriageCause.ProviderConnectivity);
            return;
        }

        if (DispatchFailureClassifier.HasRecoverableProviderModelRejectionFailure(task))
        {
            AddFailoverItem(items, task, taskNumber, agents, policy, FailureTriageCause.ProviderModelRejected);
            return;
        }

        if (task.LastVerification is { Succeeded: false } verification)
        {
            var dispatchOutcome = DispatchFailureClassifier.Classify(task, verification);
            var output = $"{verification.StandardOutput}{Environment.NewLine}{verification.StandardError}";
            if (dispatchOutcome.Kind == DispatchOutcomeKind.ProviderBudgetExhausted)
            {
                items.Add(Item(
                    task,
                    taskNumber,
                    FailureTriageCause.ProviderBudgetExhausted,
                    FailureTriageAction.RequestHumanInput,
                    null,
                    policyAllows: false,
                    canAutoApply: false,
                    gate: true,
                    $"Provider budget exhausted for {task.RequiredRole}; {dispatchOutcome.EvidenceSummary}",
                    $"replenish the named provider binding, then retry {taskNumber} with an operator recovery note"));
                return;
            }

            if (dispatchOutcome.Kind == DispatchOutcomeKind.DirtyWorktreeRecoverable)
            {
                items.Add(Item(
                    task,
                    taskNumber,
                    FailureTriageCause.DirtyWorktree,
                    FailureTriageAction.RequestHumanInput,
                    null,
                    policyAllows: false,
                    canAutoApply: false,
                    gate: true,
                    $"Worker produced file changes but left the worktree dirty; classifier evidence: {dispatchOutcome.EvidenceSummary}",
                    "goal-recovery"));
                return;
            }

            if (dispatchOutcome.Kind is DispatchOutcomeKind.PreflightFailure or DispatchOutcomeKind.SandboxCommitBlocked)
            {
                AddMissingPermissionsItem(items, task, taskNumber, dispatchOutcome.EvidenceSummary);
                return;
            }

            if (dispatchOutcome.Kind == DispatchOutcomeKind.UnknownFailure &&
                output.Contains("CS2012", StringComparison.OrdinalIgnoreCase))
            {
                var allows = policy.Allows(AutonomyAction.BuildTest);
                items.Add(Item(
                    task,
                    taskNumber,
                    FailureTriageCause.BuildFileLock,
                    FailureTriageAction.RunBuildServerShutdown,
                    AutonomyAction.BuildTest,
                    allows,
                    canAutoApply: allows,
                    gate: false,
                    "Verification failed with CS2012 file-lock evidence; shut down build servers and rerun verification.",
                    "dotnet build-server shutdown"));
                return;
            }

            if (dispatchOutcome.Kind == DispatchOutcomeKind.UnknownFailure &&
                (output.Contains("missing WORKER_RESULT", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("no relevant source file changes", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("no requested source change", StringComparison.OrdinalIgnoreCase)))
            {
                var allows = policy.Allows(AutonomyAction.Retry);
                items.Add(Item(
                    task,
                    taskNumber,
                    FailureTriageCause.NoFileChangeCompletion,
                    FailureTriageAction.RetryWithNote,
                    AutonomyAction.Retry,
                    allows,
                    canAutoApply: false,
                    gate: true,
                    "Worker completion lacked the required result contract or relevant file-change evidence.",
                    $"retry {taskNumber} <note> --autonomy {policy.Name}"));
                return;
            }

            // Last-resort arm for legacy receipts that predate typed provider/preflight classification.
            // It deliberately accepts only a structured worker blocker or an OS exception/errno on stderr;
            // arbitrary test names and assertion text containing "permission" must stay UnknownFailure.
            if (dispatchOutcome.Kind == DispatchOutcomeKind.UnknownFailure &&
                TryGetLastResortPermissionEvidence(verification, out var permissionEvidence))
            {
                AddMissingPermissionsItem(items, task, taskNumber, permissionEvidence);
                return;
            }

            items.Add(Item(
                task,
                taskNumber,
                FailureTriageCause.FailedVerification,
                FailureTriageAction.RetryWithNote,
                AutonomyAction.Retry,
                policy.Allows(AutonomyAction.Retry),
                canAutoApply: false,
                gate: true,
                $"Latest verification failed with exit {verification.ExitCode}: {verification.Command}",
                $"retry {taskNumber} <note> --autonomy {policy.Name}"));
            return;
        }

        if (task.Status == WorkTaskStatus.Completed && task.LastVerification is null)
        {
            var allows = policy.Allows(AutonomyAction.BuildTest);
            items.Add(Item(
                task,
                taskNumber,
                FailureTriageCause.MissingVerification,
                FailureTriageAction.Verify,
                AutonomyAction.BuildTest,
                allows,
                canAutoApply: false,
                gate: true,
                "Task is completed without verification evidence.",
                $"verify {taskNumber} <command> --autonomy {policy.Name}"));
            return;
        }

        if (task.LastDispatch is { PromptCharacterCount: { } promptCharacterCount } dispatch &&
            promptCharacterCount > PaidPromptThresholds.PromptThreshold(dispatch.TaskComplexity, dispatch.UsesComplexModel))
        {
            items.Add(Item(
                task,
                taskNumber,
                FailureTriageCause.LargePromptGuard,
                FailureTriageAction.RequestHumanInput,
                null,
                policyAllows: false,
                canAutoApply: false,
                gate: true,
                $"Prepared subscription prompt is large ({promptCharacterCount} chars); operator confirmation is required before paid start.",
                $"start-dispatch {taskNumber} --confirm-dispatch-start {SubscriptionPromptCostGuard.CliConfirmationFlag}"));
        }
    }

    private static void AddMissingPermissionsItem(
        List<FailureTriageItem> items,
        TaskSpec task,
        int taskNumber,
        string evidence)
    {
        items.Add(Item(
            task,
            taskNumber,
            FailureTriageCause.MissingWorkerPermissions,
            FailureTriageAction.RequestHumanInput,
            null,
            policyAllows: false,
            canAutoApply: false,
            gate: true,
            $"Worker permission apparatus is unavailable; evidence: {BoundEvidence(evidence)}",
            "worker-profile-check"));
    }

    private static bool TryGetLastResortPermissionEvidence(
        TaskVerificationRecord verification,
        out string evidence)
    {
        if (WorkerResultBlockers.TryFindBlocker(verification, out var blocker) &&
            ContainsPermissionText(blocker))
        {
            evidence = $"structured blocker={blocker}";
            return true;
        }

        foreach (var line in verification.StandardError.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Contains("UnauthorizedAccessException", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("EACCES", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("EPERM", StringComparison.OrdinalIgnoreCase))
            {
                evidence = $"legacy stderr={line}";
                return true;
            }
        }

        evidence = string.Empty;
        return false;
    }

    private static bool ContainsPermissionText(string text) =>
        text.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("not writable", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("read-only", StringComparison.OrdinalIgnoreCase);

    private static string BoundEvidence(string evidence)
    {
        const int maxLength = 256;
        var normalized = evidence.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private static void AddFailoverItem(
        List<FailureTriageItem> items,
        TaskSpec task,
        int taskNumber,
        IReadOnlyList<AgentDefinition> agents,
        AutonomyPolicy policy,
        FailureTriageCause cause)
    {
        var hasAlternate = agents.Any(agent =>
            agent.Role == task.RequiredRole &&
            agent.Status == AgentStatus.Available &&
            agent.Id != task.AssignedAgentId);
        var allows = policy.Allows(AutonomyAction.ProviderFailover);
        items.Add(Item(
            task,
            taskNumber,
            cause,
            FailureTriageAction.ReRoute,
            AutonomyAction.ProviderFailover,
            allows,
            canAutoApply: allows && hasAlternate,
            gate: !hasAlternate,
            hasAlternate
                ? "Recoverable provider failure has an unused alternate route available."
                : "Recoverable provider failure has no unused alternate route; add or repair an agent first.",
            hasAlternate ? $"re-delegate {taskNumber} --autonomy {policy.Name}" : $"agent-add {task.RequiredRole} <provider> <model>"));
    }

    private static FailureTriageItem Item(
        TaskSpec task,
        int taskNumber,
        FailureTriageCause cause,
        FailureTriageAction action,
        AutonomyAction? policyAction,
        bool policyAllows,
        bool canAutoApply,
        bool gate,
        string explanation,
        string command)
    {
        return new FailureTriageItem(
            taskNumber,
            task.Id,
            cause,
            action,
            policyAction,
            policyAllows,
            canAutoApply,
            gate,
            explanation,
            command);
    }
}
