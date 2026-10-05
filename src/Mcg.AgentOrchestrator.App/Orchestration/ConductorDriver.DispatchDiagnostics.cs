using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private Goal GetCurrentGoal(Goal goal) =>
        (_cohortKernel ?? _conductorTickKernel)?.Goals.SingleOrDefault(candidate => candidate.Id == goal.Id) ?? goal;

    private bool TryRecoverSandboxPrep(DispatchStartOutcome outcome, string goalPrefix, out string failureReason)
    {
        if (outcome.SandboxPrepRecoveryAction is not { } action)
        {
            failureReason = outcome.Reason ?? "Low-IL sandbox prep recovery action was missing.";
            return false;
        }

        try
        {
            if (_recoverSandboxPrep(action))
            {
                failureReason = string.Empty;
                return true;
            }
        }
        catch (Exception ex)
        {
            failureReason = $"Low-IL sandbox prep recovery failed for goal {goalPrefix}: {ex.Message}";
            return false;
        }

        failureReason = $"Low-IL sandbox prep recovery failed for goal {goalPrefix}: {action.Reason}";
        return false;
    }

    private void EmitPhaseTiming(string phase, Goal goal, TimeSpan elapsed, string detail)
    {
        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned))
        {
            PhaseTimingSink?.Invoke(
                $"phase={phase} goal={goal.Id.Value[..8]} task={task.Id.Value[..8]} role={task.RequiredRole} elapsed_ms={(long)Math.Ceiling(elapsed.TotalMilliseconds)} {detail}");
        }
    }

    private void EmitPhaseTiming(string phase, Goal goal, DispatchStartOutcome outcome, TimeSpan elapsed, string detail)
    {
        var dispatched = outcome.DispatchedTasks ?? [];
        if (dispatched.Count == 0)
        {
            EmitGoalPhaseTiming(phase, goal, elapsed, detail);
            return;
        }

        foreach (var task in dispatched)
        {
            PhaseTimingSink?.Invoke(
                $"phase={phase} goal={goal.Id.Value[..8]} task={task.TaskId.Value[..8]} role={task.Role} elapsed_ms={(long)Math.Ceiling(elapsed.TotalMilliseconds)} {detail}");
        }
    }

    private void EmitGoalPhaseTiming(string phase, Goal goal, TimeSpan elapsed, string detail)
    {
        var elapsedMilliseconds = (long)Math.Ceiling(elapsed.TotalMilliseconds);
        PhaseTimingSink?.Invoke(
            $"phase={phase} goal={goal.Id.Value[..8]} elapsed_ms={elapsedMilliseconds} {detail}");
    }

    private string RunDispatchRemediation()
    {
        if (_buildServerShutdownRanThisTick)
        {
            return "skipped-tick-latch";
        }

        _buildServerShutdownRanThisTick = true;
        return _buildServerShutdown(_buildServerShutdownTimeout);
    }

    private static string RunBoundedBuildServerShutdown(Action shutdown, TimeSpan timeout)
    {
        try
        {
            var shutdownTask = Task.Run(shutdown);
            if (shutdownTask.Wait(timeout))
            {
                return "ran";
            }

            _ = shutdownTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return "timeout";
        }
        catch
        {
            return "error";
        }
    }

    internal static string RunBuildServerShutdown(string workingDirectory, TimeSpan timeout)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };
            startInfo.ArgumentList.Add("build-server");
            startInfo.ArgumentList.Add("shutdown");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "error";
            }

            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeoutMilliseconds = (int)Math.Clamp(
                Math.Ceiling(timeout.TotalMilliseconds),
                1,
                int.MaxValue);
            if (process.WaitForExit(timeoutMilliseconds))
            {
                return process.ExitCode == 0
                    ? "ran exit=0"
                    : $"error exit={process.ExitCode}";
            }

            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1_000);
            }
            catch
            {
                // The process may have exited between the timed wait and tree kill.
            }

            return "timeout";
        }
        catch
        {
            return "error";
        }
    }

    private static void AppendGateProgressEvent(
        ConductEventLogWriter writer,
        GoalId goalId,
        AcceptanceGateProgress progress)
    {
        if (!writer.AppendRequired(
                "gate-progress",
                goalId.Value[..8],
                FormatGateProgressConductEvent(progress)))
        {
            throw new IOException(
                $"Required gate progress event could not be appended for goal {goalId.Value[..8]}.");
        }
    }

    internal static void AppendCohortGateProgressEvents(
        ConductEventLogWriter writer,
        AcceptanceCohortIdentity identity,
        IReadOnlyList<AcceptanceCohortMemberBinding> bindings,
        AcceptanceGateProgress progress)
    {
        var members = string.Join(',', bindings.Select(member => member.GoalId.Value[..8]));
        foreach (var member in bindings)
        {
            var goalId = member.GoalId.Value[..8];
            var detail =
                $"PHASE_PROGRESS goal={goalId} cohort={identity.Value[..Math.Min(18, identity.Value.Length)]} " +
                $"member={goalId} members={members} phase={progress.Phase} " +
                $"elapsed_ms={(long)progress.Elapsed.TotalMilliseconds} target={FormatConductToken(progress.CurrentTarget)} " +
                $"child_pid={progress.ChildProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
                $"output_bytes={progress.OutputBytes} heartbeat={FormatConductToken(progress.HeartbeatPath)}";
            if (!writer.AppendRequired("gate-progress", goalId, detail))
            {
                throw new IOException(
                    $"Required cohort gate progress event could not be appended for goal {goalId}.");
            }
        }
    }

    private static string FormatGateProgressConductEvent(AcceptanceGateProgress progress) =>
        $"PHASE_PROGRESS goal={progress.GoalId?[..Math.Min(8, progress.GoalId.Length)] ?? "unknown"} phase={progress.Phase} " +
        $"elapsed_ms={(long)progress.Elapsed.TotalMilliseconds} target={FormatConductToken(progress.CurrentTarget)} " +
        $"child_pid={progress.ChildProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
        $"output_bytes={progress.OutputBytes} heartbeat={FormatConductToken(progress.HeartbeatPath)}";

    private static string FormatConductToken(string value) =>
        value.IndexOfAny([' ', '\t', '\r', '\n', '"']) < 0
            ? value
            : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static int CountAssignedTasks(Goal goal) =>
        goal.Tasks.Count(task => task.Status == WorkTaskStatus.Assigned);

    private static bool HasAssignedDeveloperReadyForDispatch(Goal goal) =>
        goal.Tasks.Any(task =>
            task.RequiredRole == AgentRole.Developer &&
            task.Status == WorkTaskStatus.Assigned &&
            !goal.Tasks.Any(candidate =>
                DispatchReadinessRules.IsEarlierSdlcStageOf(
                    candidate.RequiredRole,
                    task.RequiredRole) &&
                candidate.Status != WorkTaskStatus.Completed));

    private static string FormatPreparedDispatchWithoutStart(SubscriptionStartResult result)
    {
        const int maxPreparedDiagnostics = 8;
        var preparedState = result.Dispatches
            .Take(maxPreparedDiagnostics)
            .Select(dispatch =>
            {
                var task = dispatch.Task;
                var admission = task.RetryAdmissionHistory.LastOrDefault(receipt =>
                    receipt.LinkedDispatchAt == task.LastDispatch?.DispatchedAt);
                var admissionState = admission is null
                    ? "none"
                    : $"{admission.Decision}/{admission.Route}/{admission.Cause}";
                return $"{task.Id.Value[..8]}:status={task.Status}:admission={admissionState}";
            })
            .ToArray();
        var omitted = result.Dispatches.Count - preparedState.Length;
        var omittedSuffix = omitted > 0 ? $",...(+{omitted})" : string.Empty;
        return $"Prepared {result.Dispatches.Count} dispatch(es) but no process was startable; " +
               $"{DispatchStartRefusalReasonBuilder.Build(result.Processes, result.Dispatches)}; " +
               $"prepared=[{string.Join(',', preparedState)}{omittedSuffix}]";
    }

    private static string FormatSourceCleanupPaths(IReadOnlyList<string>? paths)
    {
        const int maxPaths = 20;
        var available = paths ?? [];
        var listed = string.Join(", ", available.Take(maxPaths).Select(path => $"'{path}'"));
        var omitted = available.Count - Math.Min(available.Count, maxPaths);
        return omitted > 0 ? $"[{listed}, ... (+{omitted})]" : $"[{listed}]";
    }

    // Turns an empty subscription dispatch batch into an ACTIONABLE escalation. When the parallel
    // planner held every ready task back for operator approval (e.g. a high-risk ownership write-set
    // like scripts/ or src/Infrastructure under a non-permissive policy), surface those reasons so
    // the operator knows what to approve — instead of the generic "no ready batch" that hides why
    // nothing dispatched and forces a manual dig (see conductor-high-risk-ownership-gap).
    internal static string DescribeEmptyBatch(
        ParallelExecutionPlan plan,
        IReadOnlyList<ReadyBlockedDiagnostic>? blockedDiagnostics = null)
    {
        var diagnosticReasons = blockedDiagnostics?
            .Select(FormatReadyBlockedDiagnostic)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        if (diagnosticReasons.Count > 0)
        {
            return "No tasks dispatched; assigned tasks were excluded from the ready batch: "
                + string.Join("; ", diagnosticReasons);
        }

        var approvalReasons = plan.Decisions
            .Where(decision => decision.Disposition == ParallelExecutionDisposition.RequiresOperatorApproval)
            .SelectMany(decision => decision.Reasons)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return approvalReasons.Count > 0
            ? "No tasks dispatched; all ready tasks require operator approval (run under a policy that "
                + "auto-approves high-risk ownership, or approve manually): "
                + string.Join("; ", approvalReasons)
            : "No tasks in ready batch; goal may have no assigned or ready tasks";
    }

    private static string FormatReadyBlockedDiagnostic(ReadyBlockedDiagnostic diagnostic)
    {
        var details = diagnostic.Details?
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray() ?? [];
        var detail = details.Length > 0
            ? $": {string.Join(", ", details)}"
            : string.Empty;
        return $"task {diagnostic.TaskNumber} {diagnostic.TaskId} provider={diagnostic.Provider} reason={diagnostic.Reason}{detail}";
    }

    private static string FormatAssignedTasksBlockedReason(
        Goal goal,
        DispatchReadinessVerdict readiness,
        string? emptyBatchReason)
    {
        var taskReasons = goal.Tasks
            .Where(task => task.Status == WorkTaskStatus.Assigned)
            .Select(task => FormatAssignedTaskBlocker(goal, task, readiness, emptyBatchReason))
            .ToArray();
        var blockers = taskReasons.Length == 0
            ? "no Assigned tasks remained when the batch was evaluated"
            : string.Join("; ", taskReasons);
        return $"{NoReadyBatchHoldPrefix} for goal {goal.Id.Value}; will retry next tick. Blockers: {blockers}.";
    }

    private static string FormatAssignedTaskBlocker(
        Goal goal,
        TaskSpec task,
        DispatchReadinessVerdict readiness,
        string? emptyBatchReason)
    {
        if (task.LastProcess is { IsRunning: true })
        {
            return $"task {task.Id.Value} ({task.RequiredRole}) blocked: task already has a running process";
        }

        var predecessor = goal.Tasks.FirstOrDefault(candidate =>
            DispatchReadinessRules.IsEarlierSdlcStageOf(candidate.RequiredRole, task.RequiredRole) &&
            candidate.Status != WorkTaskStatus.Completed);
        if (predecessor is not null)
        {
            return $"task {task.Id.Value} ({task.RequiredRole}) blocked: predecessor {predecessor.Id.Value} is {predecessor.Status}, not Completed";
        }

        var readinessReason = readiness switch
        {
            DispatchReadinessDeferred deferred => $"readiness gate returned false: {deferred.Reason}",
            DispatchReadinessBlocked blocked => $"readiness gate returned false: {blocked.Reason}",
            DispatchReadinessReady => string.IsNullOrWhiteSpace(emptyBatchReason)
                ? "batch formation returned no dispatch"
                : $"batch formation returned no dispatch: {emptyBatchReason}",
            _ => "batch formation returned no dispatch"
        };
        return $"task {task.Id.Value} ({task.RequiredRole}) blocked: {readinessReason}";
    }

    private static bool TryDescribeCancelledPredecessorBlocker(Goal goal, out string blocker)
    {
        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned))
        {
            var predecessor = goal.Tasks.FirstOrDefault(candidate =>
                DispatchReadinessRules.IsEarlierSdlcStageOf(candidate.RequiredRole, task.RequiredRole) &&
                candidate.Status == WorkTaskStatus.Cancelled);
            if (predecessor is null)
            {
                continue;
            }

            blocker =
                $"task {task.Id.Value} ({task.RequiredRole}) blocked: predecessor {predecessor.Id.Value} is Cancelled, not Completed";
            return true;
        }

        blocker = string.Empty;
        return false;
    }

    private static TimeSpan ComputeEmptyOutputBackoff(ConductorAutonomyPolicy policy, int retryCount)
    {
        if (policy.EmptyOutputRetryInitialDelaySeconds <= 0 ||
            policy.EmptyOutputRetryMaxDelaySeconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var exponent = Math.Max(0, retryCount - 1);
        var seconds = policy.EmptyOutputRetryInitialDelaySeconds *
            Math.Pow(policy.EmptyOutputRetryBackoffMultiplier, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, policy.EmptyOutputRetryMaxDelaySeconds));
    }

    private static bool TryGetDispatchRecoveryAction(TaskVerificationRecord? verification, out DispatchRecoveryAction action)
    {
        action = default;
        if (verification is null)
        {
            return false;
        }

        var diagnostic = ExtractDispatchRecoveryDiagnostic(verification);
        if (diagnostic.Length == 0)
        {
            return false;
        }

        foreach (var candidate in Enum.GetValues<DispatchRecoveryAction>())
        {
            if (diagnostic.Contains($"action='{DispatchRecoveryPolicy.ToActionName(candidate)}'", StringComparison.Ordinal))
            {
                action = candidate;
                return true;
            }
        }

        return false;
    }

    private static DispatchRecoveryAction GetDispatchRecoveryAction(TaskVerificationRecord verification) =>
        TryGetDispatchRecoveryAction(verification, out var action)
            ? action
            : throw new InvalidOperationException("Verification does not contain a dispatch recovery action.");

    private static bool IsRetryableStaleRecovery(TaskVerificationRecord verification)
    {
        if (!TryGetDispatchRecoveryAction(verification, out var action))
            return false;

        if (action == DispatchRecoveryAction.RetryStale)
            return true;

        var diagnostic = ExtractDispatchRecoveryDiagnostic(verification);
        return action == DispatchRecoveryAction.MarkStale &&
            diagnostic.Contains("stale retry budget remaining=", StringComparison.Ordinal) &&
            !diagnostic.Contains("blocker='", StringComparison.Ordinal);
    }

    private static string ExtractDispatchRecoveryDiagnostic(TaskVerificationRecord verification)
    {
        var lines = verification.StandardError.Split(
            ["\r\n", "\n"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault(line => line.Contains("Dispatch recovery policy action='", StringComparison.Ordinal)) ?? string.Empty;
    }

    // Appends a bounded tail of the acceptance build/test output to an escalation/journal line so an
    // operator (or the conductor's own retry diagnostics) can see WHY acceptance failed — the detail
    // was previously dropped, leaving only a generic "Acceptance verification failed".
    private static string FormatFailureTail(string? outputTail)
    {
        if (string.IsNullOrWhiteSpace(outputTail))
        {
            return string.Empty;
        }

        var trimmed = outputTail.Trim();
        const int maxChars = 600;
        var tail = trimmed.Length > maxChars ? "..." + trimmed[^maxChars..] : trimmed;
        return $" Acceptance output tail: {tail}";
    }

    private static bool IsBlockingTimeoutCheck(string checkName) =>
        checkName.StartsWith("acceptance-check-timeout:", StringComparison.OrdinalIgnoreCase);
}
