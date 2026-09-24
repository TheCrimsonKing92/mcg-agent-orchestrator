using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

public sealed class ConductorDriverTestsGoalAddedTestFileInsideChanges
{
    [Xunit.Theory]
    [Xunit.InlineData(true, 1)]
    [Xunit.InlineData(false, 2)]
    public void DataCaseInGoalAddedFileIsCandidateChangeOnlyWithKnownScope(
        bool knownScope, int expectedRuns)
    {
        const string candidateSha = "abc1234";
        const string addedPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SomeNewTests.cs";
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            {
                PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
            }
            FailReviewerNeedsWork(kernel, goal, reviewer, "A new data case failed",
                findings: [EvidenceFindingWithRequest("The new test is RED", id: "new-test-red",
                    classes: ["SomeNewTests"])]);

            var worktree = GoalWorktrees.WorktreePath(root, goal.Id);
            var source = Path.Combine(worktree, addedPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            File.WriteAllText(source, "namespace Ns; public sealed class SomeNewTests { public void Method(bool passed) { } }");

            var focusedRuns = 0;
            TaskId? retried = null;
            var driver = MakeDriver(
                executionDirectory: root,
                getLandingFileScopes: _ => knownScope ? [addedPath] : [],
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                runFocusedEvidence: (_, request) =>
                {
                    focusedRuns++;
                    var red = CandidateRedFindingEvidence(
                        request, candidateSha, "Ns.SomeNewTests.Method(passed: True)");
                    return red with { Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray() };
                },
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                {
                    retried = taskId;
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(expectedRuns, focusedRuns);
            Assert.Equal(developer.Id, retried);
            Assert.Equal(!knownScope, goal.Timeline.Any(evt =>
                evt.Message.Contains("disposition=baseline-arm-absent", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
