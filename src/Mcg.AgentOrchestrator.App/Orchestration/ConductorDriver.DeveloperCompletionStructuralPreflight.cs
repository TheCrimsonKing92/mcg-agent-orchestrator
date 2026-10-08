using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private static readonly TimeSpan DefaultDeveloperCompletionStructuralPreflightBound = TimeSpan.FromSeconds(10);

    private Func<string, DeveloperCompletionStructuralFindings> _runDeveloperCompletionStructuralPreflight =
        path => DeveloperCompletionStructuralPreflight.Evaluate(path);
    private Func<GoalId, TaskId, string, RetryRoundKind?, RetryCause, TaskSpec>? _retryDeveloperAfterStructuralPreflight;
    private TimeSpan _developerCompletionStructuralPreflightBound =
        DefaultDeveloperCompletionStructuralPreflightBound;

    internal void ConfigureDeveloperCompletionStructuralPreflightRetry(AgentOrchestratorKernel kernel)
    {
        _retryDeveloperAfterStructuralPreflight = (goalId, taskId, message, retryRoundKind, cause) =>
            kernel.RetryTaskAutomatically(goalId, taskId, message, cause,
                retryRoundKind: retryRoundKind, authoritativeRetryFeedback: true);
    }

    internal void OverrideDeveloperCompletionStructuralPreflightForTests(
        Func<string, DeveloperCompletionStructuralFindings> run,
        TimeSpan bound)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (bound <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bound), bound, "The pre-check bound must be positive.");
        }

        _runDeveloperCompletionStructuralPreflight = run;
        _developerCompletionStructuralPreflightBound = bound;
    }

    private bool TryRunDeveloperCompletionStructuralPreflight(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        out ConductorAdvanceResult result)
    {
        result = default!;
        if (fromState != GoalLifecycleState.WorkspaceReady || _executionDirectory is null)
        {
            return false;
        }

        var testerTask = goal.Tasks.FirstOrDefault(task =>
            task.RequiredRole == AgentRole.Tester &&
            task.Status == WorkTaskStatus.Assigned &&
            !goal.Tasks.Any(candidate =>
                DispatchReadinessRules.IsEarlierSdlcStageOf(candidate.RequiredRole, task.RequiredRole) &&
                candidate.Status != WorkTaskStatus.Completed));
        var developerTask = testerTask is null
            ? null
            : TasksBefore(goal, testerTask)
                .LastOrDefault(task =>
                    task.RequiredRole == AgentRole.Developer &&
                    task.Status == WorkTaskStatus.Completed);
        if (developerTask is null)
        {
            return false;
        }

        var worktreePath = GoalWorktrees.WorktreePath(_executionDirectory, goal.Id);
        if (!Directory.Exists(worktreePath))
        {
            return false;
        }

        var candidateSha = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (PreTesterEvidenceIndexLines.Latest(goal, testerTask!.Id, candidateSha)?.Outcome == "started")
        {
            return TryRunPreTesterDeferredEvidence(
                goal, developerTask, testerTask, worktreePath, candidateSha!, goalPrefix, policy, fromState, out result);
        }

        var work = Task.Run(() => _runDeveloperCompletionStructuralPreflight(worktreePath));
        DeveloperCompletionStructuralFindings findings;
        try
        {
            findings = work.WaitAsync(_developerCompletionStructuralPreflightBound).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            ObserveAbandonedStructuralPreflight(work);
            _recordTaskNote(
                goal.Id,
                developerTask.Id,
                $"developer-completion structural pre-check skipped: exceeded " +
                $"{_developerCompletionStructuralPreflightBound.TotalMilliseconds:0} ms bound");
            return false;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _recordTaskNote(
                goal.Id,
                developerTask.Id,
                $"developer-completion structural pre-check skipped: {exception.GetType().Name}: {exception.Message}");
            return false;
        }

        var notePrefix = findings.HasViolation
            ? "developer-completion structural pre-check failed: "
            : "developer-completion structural pre-check passed: ";
        var note = notePrefix + findings.Message;
        _recordTaskNote(goal.Id, developerTask.Id, note);
        if (!findings.HasViolation)
        {
            return TryRunPreTesterDeferredEvidence(
                goal, developerTask, testerTask!, worktreePath, candidateSha,
                goalPrefix, policy, fromState, out result);
        }

        (_retryDeveloperAfterStructuralPreflight ?? _retryTask)(
            goal.Id,
            developerTask.Id,
            note,
            RetryRoundKind.Mechanical,
            RetryCause.NewSourceFinding);
        var refreshedGoal = GetCurrentGoal(goal);
        var retryState = GoalLifecycle.ResolveState(refreshedGoal, GetFacts(refreshedGoal));
        result = ExecuteDispatchAndStart(refreshedGoal, goalPrefix, policy, retryState);
        return true;
    }

    private static void ObserveAbandonedStructuralPreflight(Task work)
    {
        _ = work.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
