using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private ConductEventLogWriter? _candidateIdentityEventWriter;

    private Func<Goal, (CandidateIdentity? Identity, string Failure)> _resolveCandidateIdentity =
        _ => (null, "resolver-unavailable");

    internal void OverrideCandidateIdentityResolverForTests(Func<Goal, CandidateIdentity?> resolver) =>
        _resolveCandidateIdentity = goal => (resolver(goal), "test-resolver-unavailable");

    private void ConfigureCandidateIdentity(AgentOrchestratorKernel kernel, string conductEventsLogPath)
    {
        _candidateIdentityEventWriter = new ConductEventLogWriter(conductEventsLogPath);
        _resolveCandidateIdentity = goal =>
        {
            var path = _executionDirectory is null ? null : GoalWorktrees.TryResolve(_executionDirectory, goal.Id);
            if (path is null) return (null, "missing-worktree");
            return GoalWorktrees.TryComputeCandidateIdentity(path, out var identity, out var failure)
                ? (identity, string.Empty)
                : (null, failure);
        };
        kernel.ConfigureCandidateIdentityResolver(goal => _resolveCandidateIdentity(goal).Identity);
    }

    private IReadOnlySet<TaskId> GetUnchangedCandidateExclusions(Goal goal)
    {
        var current = _resolveCandidateIdentity(goal).Identity;
        if (current is null) return new HashSet<TaskId>();
        return ReadyCandidateTasks(goal)
            .Where(task => UnchangedCandidateRule.Evaluate(goal, task, current) is not null)
            .Select(task => task.Id).ToHashSet();
    }

    private bool TryRefuseUnchangedCandidateDispatch(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState, out ConductorAdvanceResult result)
    {
        result = null!;
        if (fromState != GoalLifecycleState.WorkspaceReady) { ResetUnchangedCandidateHold(goal.Id); return false; }
        var ready = ReadyCandidateTasks(goal);
        if (ready.Length == 0) { ResetUnchangedCandidateHold(goal.Id); return false; }
        var (identity, failure) = _resolveCandidateIdentity(goal);
        if (identity is null)
        {
            foreach (var task in ready.DistinctBy(candidate => candidate.RequiredRole))
                TryRecordCandidateIdentityFailure(goal.Id, task.RequiredRole, failure);
            ResetUnchangedCandidateHold(goal.Id);
            return false;
        }
        var reasons = ready.Select(task => UnchangedCandidateRule.Evaluate(goal, task, identity)).ToArray();
        if (reasons.Any(reason => reason is null)) { ResetUnchangedCandidateHold(goal.Id); return false; }
        var typed = reasons[0]!;
        TrackUnchangedCandidateHold(goal, ready[0], typed);
        var hold = new ConductorAdvanceOutcome.Held(fromState, typed.Render(), identity.Canonical)
        {
            TypedReason = typed
        };
        result = MakeResult(goal.Id.Value, goalPrefix, policy, hold);
        return true;
    }

    private void TryRecordCandidateIdentityFailure(GoalId goalId, AgentRole role, string failure)
    {
        try
        {
            _candidateIdentityEventWriter?.Append(
                "candidate-identity-unavailable", goalId.Value,
                $"CANDIDATE_IDENTITY_UNAVAILABLE goal={goalId.Value} role={role} reason={failure}");
        }
        catch (Exception)
        {
            // A diagnostic write cannot block a fail-open dispatch.
        }
    }

    private static TaskSpec[] ReadyCandidateTasks(Goal goal) => goal.Tasks
        .Where(DispatchReadinessRules.IsSubscriptionStartCandidate)
        .Where(task => DispatchReadinessRules.IsRefinementEligible(goal, task))
        .Where(task => !goal.Tasks.Any(other =>
            DispatchReadinessRules.IsEarlierSdlcStageOf(other.RequiredRole, task.RequiredRole) &&
            other.Status != WorkTaskStatus.Completed))
        .ToArray();
}
