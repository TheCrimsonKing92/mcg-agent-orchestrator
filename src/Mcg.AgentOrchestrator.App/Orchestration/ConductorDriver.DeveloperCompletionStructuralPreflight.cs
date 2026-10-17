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
    private Func<string, string, string, TestFirstScopeResult> _readTestFirstScope = TestFirstScopeCheck.Read;
    internal void OverrideTestFirstScopeReaderForTests(Func<string, string, string, TestFirstScopeResult> read) =>
        _readTestFirstScope = read ?? throw new ArgumentNullException(nameof(read));
    private TimeSpan _developerCompletionStructuralPreflightBound = DefaultDeveloperCompletionStructuralPreflightBound;

    internal void ConfigureDeveloperCompletionStructuralPreflightRetry(AgentOrchestratorKernel kernel, ConductorConflictIntegration? integration = null)
    {
        _retryDeveloperAfterStructuralPreflight = (goalId, taskId, message, retryRoundKind, cause) =>
            kernel.RetryTaskAutomatically(goalId, taskId, message, cause,
                retryRoundKind: retryRoundKind, authoritativeRetryFeedback: true);
        if (integration is null) return;
        _conflictIntegration = integration;
        _integrateMainBeforeDeveloperDispatch = integration.IntegrateMainBeforeDeveloperDispatch;
    }

    internal void OverrideDeveloperCompletionStructuralPreflightForTests(
        Func<string, DeveloperCompletionStructuralFindings> run,
        TimeSpan bound)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (bound <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(bound), bound, "The pre-check bound must be positive.");

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
        if (fromState != GoalLifecycleState.WorkspaceReady || _executionDirectory is null) return false;

        if (TestFirstScopeCheck.SelectCheckpoint(goal) is { } checkpoint)
        {
            var author = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
            var scope = _readTestFirstScope(GoalWorktrees.WorktreePath(_executionDirectory, goal.Id), _integrationBranch, author.Id.Value);
            if (scope.Outcome != TestFirstScopeCheck.ScopeOutcome.Skipped)
                scope = TestFirstScopeCheck.Evaluate(checkpoint.Kind, scope.AuthorChanges, scope.ImplementerTouchedAuthoredPaths);
            var repeat = TestFirstScopeCheck.IsRepeat(goal, checkpoint.Developer.Id);
            var scopeNote = $"test-first scope check {scope.Outcome.ToString().ToLowerInvariant()}: {scope.Message}";
            _recordTaskNote(goal.Id, checkpoint.Developer.Id, scopeNote);
            if (scope.Outcome == TestFirstScopeCheck.ScopeOutcome.Failed)
            {
                if (!repeat) return RetryDeveloperAfterPreflight(goal, checkpoint.Developer, scopeNote, goalPrefix, policy, out result);
                var decision = scope.RepeatedDecision(checkpoint.Developer.Id);
                var escalated = Escalate(goal, goalPrefix, policy, fromState, decision.Reason);
                result = escalated with { Outcome = ((ConductorAdvanceOutcome.Escalated)escalated.Outcome) with { Decision = decision } };
                return true;
            }
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
        if (developerTask is null) return false;
        if (_conflictIntegration?.Validate(goal) is { } conflictReason)
        { result = Escalate(goal, goalPrefix, policy, fromState, conflictReason); return true; }

        var worktreePath = GoalWorktrees.WorktreePath(_executionDirectory, goal.Id);
        if (!Directory.Exists(worktreePath)) return false;

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
            _recordTaskNote(goal.Id, developerTask.Id,
                $"developer-completion structural pre-check skipped: exceeded {_developerCompletionStructuralPreflightBound.TotalMilliseconds:0} ms bound");
            return false;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _recordTaskNote(goal.Id, developerTask.Id,
                $"developer-completion structural pre-check skipped: {exception.GetType().Name}: {exception.Message}");
            return false;
        }

        var note = (findings.HasViolation ? "developer-completion structural pre-check failed: "
            : "developer-completion structural pre-check passed: ") + findings.Message;
        _recordTaskNote(goal.Id, developerTask.Id, note);
        if (!findings.HasViolation)
        {
            return TryRunPreTesterDeferredEvidence(
                goal, developerTask, testerTask!, worktreePath, candidateSha,
                goalPrefix, policy, fromState, out result);
        }

        return RetryDeveloperAfterPreflight(goal, developerTask, note, goalPrefix, policy, out result);
    }

    private bool RetryDeveloperAfterPreflight(Goal goal, TaskSpec developerTask, string note,
        string goalPrefix, ConductorAutonomyPolicy policy, out ConductorAdvanceResult result)
    {
        (_retryDeveloperAfterStructuralPreflight ?? _retryTask)(goal.Id, developerTask.Id, note,
            RetryRoundKind.Mechanical, RetryCause.NewSourceFinding);
        var refreshedGoal = GetCurrentGoal(goal);
        var retryState = GoalLifecycle.ResolveState(refreshedGoal, GetFacts(refreshedGoal));
        result = ExecuteDispatchAndStart(refreshedGoal, goalPrefix, policy, retryState);
        return true;
    }

    private static void ObserveAbandonedStructuralPreflight(Task work) =>
        _ = work.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
