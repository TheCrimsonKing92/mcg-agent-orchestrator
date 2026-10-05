using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private bool TryStopRepeatedPreReviewMappingRetry(
        Goal goal,
        TaskSpec reviewerTask,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        PreReviewEvidenceContext context,
        PreReviewEvidenceReceipt? priorReceipt,
        out ConductorAdvanceResult result)
    {
        if (priorReceipt is not { Disposition: PreReviewEvidenceDisposition.MappingNeedsInput } ||
            !priorReceipt.MatchesCurrentCandidate(
                goal.Id.Value,
                context.CandidateSha!,
                context.SelectedFocusedTests))
        {
            result = default!;
            return false;
        }

        var requiredRole = context.RequiresSourceCleanup ? AgentRole.Developer : AgentRole.Tester;
        var repairTask = TasksBefore(goal, reviewerTask)
            .LastOrDefault(task => task.RequiredRole == requiredRole);
        if (repairTask is not null)
        {
            var remedy = context.RequiresSourceCleanup
                ? $" Remove and commit these paths: {FormatSourceCleanupPaths(context.SourceCleanupPaths)}."
                : " Correct the test-impact mapping or provide an explicit focused-test selection before retrying.";
            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                $"PRE_REVIEW_MAPPING_RETRY_DID_NOT_CONVERGE: automatic {requiredRole} retry left candidate " +
                $"{context.CandidateSha} with the same mapping-needed disposition; no further paid retry was started.{remedy}");
            return true;
        }

        var suppressedCount = 1 + goal.Timeline.Count(evt =>
            evt.Kind == ProgressKind.PreReviewMappingEscalationSuppressed &&
            evt.TaskId == reviewerTask.Id &&
            evt.Message.Contains($"candidate_sha={context.CandidateSha}", StringComparison.OrdinalIgnoreCase) &&
            evt.Message.Contains(
                $"disposition={PreReviewEvidenceDisposition.MappingNeedsInput}",
                StringComparison.Ordinal));
        _recordPreReviewMappingEscalationSuppressed(
            goal.Id,
            reviewerTask.Id,
            context.CandidateSha!,
            suppressedCount);
        result = MakeResult(
            goal.Id.Value,
            goalPrefix,
            policy,
            new ConductorAdvanceOutcome.Held(
                fromState,
                $"PRE_REVIEW_MAPPING_NEEDS_INPUT repeat suppressed for candidate {context.CandidateSha}; " +
                $"suppressed_count={suppressedCount}."));
        return true;
    }

    private bool TryRoutePreReviewEvidenceToDeveloper(
        Goal goal,
        TaskSpec reviewerTask,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        string reason,
        out ConductorAdvanceResult result)
    {
        var developerTask = TasksBefore(goal, reviewerTask)
            .LastOrDefault(task => task.RequiredRole == AgentRole.Developer);
        if (developerTask is null)
        {
            result = default!;
            return false;
        }

        _retryTask(
            goal.Id,
            developerTask.Id,
            reason,
            RetryRoundKind.Mechanical,
            RetryCause.NewSourceFinding);
        var refreshedGoal = GetCurrentGoal(goal);
        var retryState = GoalLifecycle.ResolveState(refreshedGoal, GetFacts(refreshedGoal));
        result = ExecuteDispatchAndStart(refreshedGoal, goalPrefix, policy, retryState);
        return true;
    }

    private bool TryRoutePreReviewEvidenceToTester(
        Goal goal,
        TaskSpec reviewerTask,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        string reason,
        out ConductorAdvanceResult result)
    {
        var testerTask = TasksBefore(goal, reviewerTask)
            .LastOrDefault(task => task.RequiredRole == AgentRole.Tester);
        if (testerTask is null)
        {
            result = default!;
            return false;
        }

        _retryTask(
            goal.Id,
            testerTask.Id,
            reason,
            RetryRoundKind.Mechanical,
            RetryCause.NewTestFinding);
        var refreshedGoal = GetCurrentGoal(goal);
        var retryState = GoalLifecycle.ResolveState(refreshedGoal, GetFacts(refreshedGoal));
        result = ExecuteDispatchAndStart(refreshedGoal, goalPrefix, policy, retryState);
        return true;
    }

    internal static PreReviewEvidenceContext BuildPreReviewEvidenceContext(Goal goal, string executionDirectory)
    {
        var worktreePath = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        var candidateSha = worktreePath is null ? null : TryResolveGitHead(worktreePath);
        var changedFiles = worktreePath is null
            ? Array.Empty<string>()
            : GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath);
        return BuildPreReviewEvidenceContext(
            candidateSha,
            changedFiles,
            worktreePath);
    }

    internal PreReviewEvidenceContext GetPreReviewEvidenceContext(Goal goal) =>
        _getPreReviewEvidenceContext(goal);

    internal static PreReviewEvidenceContext BuildPreReviewEvidenceContext(
        string? candidateSha, IReadOnlyList<string> changedFiles, string? repositoryRoot = null)
    {
        var changeSummary = RepositoryChangeClassifier.Classify(changedFiles);
        var plan = string.IsNullOrWhiteSpace(repositoryRoot)
            ? RepositoryTestImpactPlanner.Plan(changeSummary)
            : RepositoryTestImpactPlanner.Plan(changeSummary, repositoryRoot);
        return BuildPreReviewEvidenceContext(candidateSha, changeSummary, plan);
    }

    internal static PreReviewEvidenceContext BuildPreReviewEvidenceContext(
        string? candidateSha, RepositoryChangeSummary changeSummary, RepositoryTestImpactPlan plan) =>
        PreReviewEvidenceContextBuilder.Build(candidateSha, changeSummary, plan);

    private static string BuildAddTesterCommand(string goalPrefix, string candidateSha) =>
        $"add-task --goal {goalPrefix} Tester Resolve pre-review mapping for candidate {candidateSha} --before-role Reviewer";

    private static IReadOnlyList<TaskSpec> TasksBefore(Goal goal, TaskSpec task)
        => goal.Tasks.TakeWhile(candidate => candidate.Id != task.Id).ToArray();

    private static int GetCurrentReviewerRoundNumber(Goal goal, TaskSpec reviewerTask) =>
        1 + goal.Timeline.Count(evt =>
            evt.Kind == ProgressKind.TaskRetried &&
            evt.TaskId is not null &&
            (evt.TaskId != reviewerTask.Id ||
                !MechanicalReviewerRetryMessagePrefixes.Any(prefix =>
                    evt.Message.StartsWith(prefix, StringComparison.Ordinal))));

    private static IReadOnlyList<string> ExtractFailingTestIdentities(
        IEnumerable<AcceptanceCheckResult> checks) =>
        checks
            .SelectMany(check => check.FailingTestIdentities ?? [])
            .Where(identity => identity.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string? BuildPreReviewEvidencePointer(FocusedEvidenceRunResult evidence) =>
        evidence.Checks
            .SelectMany(check =>
                (check.TestResultPaths ?? [])
                    .Concat(string.IsNullOrWhiteSpace(check.ArtifactsPath) ? [] : [check.ArtifactsPath!]))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
}
