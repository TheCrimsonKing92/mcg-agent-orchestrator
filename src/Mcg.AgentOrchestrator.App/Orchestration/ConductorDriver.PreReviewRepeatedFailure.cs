using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private bool TryHoldRepeatedPreReviewFailure(
        Goal goal,
        TaskSpec reviewerTask,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        out string statement,
        out ConductorAdvanceResult result)
    {
        statement = string.Empty;
        result = default!;
        var currentReviewer = GetCurrentGoal(goal).Tasks.Single(task => task.Id == reviewerTask.Id);
        var summary = PreReviewRepeatedFailureSet.Evaluate(currentReviewer.PreReviewEvidenceHistory);
        if (summary.HoldRequired)
        {
            var hold = new PreReviewRepeatedFailureHold(summary.RepeatedTests, summary.ConsecutiveRounds);
            result = Escalate(goal, goalPrefix, policy, fromState, hold.Reason);
            return true;
        }
        if (summary.HasRepeat)
            statement = PreReviewRepeatedFailureStatement.Format(
                summary,
                ResolveRepeatedTestDetails(currentReviewer.PreReviewEvidenceReceipt!, summary.RepeatedTests));
        return false;
    }

    private static IReadOnlyDictionary<string, PreReviewRepeatedTestDetail> ResolveRepeatedTestDetails(
        PreReviewEvidenceReceipt receipt,
        IReadOnlyList<string> testNames)
    {
        var details = new Dictionary<string, PreReviewRepeatedTestDetail>(StringComparer.Ordinal);
        var paths = receipt.Checks.SelectMany(check => check.TestResultPaths ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string? unavailable = null;
        foreach (var path in paths)
        {
            var read = AcceptanceTrxFailureReader.Read(path);
            if (read.Status != AcceptanceTrxReadStatus.Readable)
            {
                unavailable ??= read.Status.ToString().ToLowerInvariant();
                continue;
            }
            foreach (var name in testNames.Where(name => !details.ContainsKey(name)))
            {
                var failure = read.Failures.FirstOrDefault(failure =>
                    string.Equals(failure.TestName, name, StringComparison.Ordinal) ||
                    string.Equals(failure.TestName, name.Split('(')[0], StringComparison.Ordinal));
                if (failure is not null)
                    details[name] = new(failure.Message, failure.StackTrace);
            }
        }
        foreach (var name in testNames.Where(name => !details.ContainsKey(name)))
            details[name] = new(null, null, unavailable ?? (paths.Length == 0 ? "result file absent" : "no matching result"));
        return details;
    }

    private static PreReviewRepeatedFailureSummary SummarizeRepeatedFailingSet(Goal goal)
    {
        var reviewer = goal.Tasks.FirstOrDefault(task => task.RequiredRole == AgentRole.Reviewer);
        return PreReviewRepeatedFailureSet.Evaluate(reviewer?.PreReviewEvidenceHistory ?? []);
    }
}
