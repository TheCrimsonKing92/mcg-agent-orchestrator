using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class DeveloperCompletionStructuralPreflightRetryFeedbackTests
{
    [Xunit.Fact]
    public void TwoCeilingViolations_ReplaceStaleFeedbackInNextDeveloperBrief()
    {
        var executionDirectory = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal("Deliver structural pre-check violations");
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != tester.Id))
            {
                PassVerification(kernel, goal, task);
            }

            const string staleFeedback = "stale earlier-round feedback: rename Foo";
            kernel.RecordCriterionRetryFeedback(goal.Id, developer.Id, [staleFeedback]);
            Assert.Equal([staleFeedback], kernel.GetTask(goal.Id, developer.Id).CriterionRetryFeedback);

            var worktreePath = GoalWorktrees.WorktreePath(executionDirectory, goal.Id);
            Directory.CreateDirectory(worktreePath);
            var authorityPath = Path.Combine(worktreePath,
                SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
            File.WriteAllLines(authorityPath,
            [
                "new SourceSizeCeiling(\"alpha.cs\", 2)",
                "new SourceSizeCeiling(\"beta.cs\", 4)"
            ]);
            File.WriteAllLines(Path.Combine(worktreePath, "alpha.cs"), Enumerable.Repeat("line", 3));
            File.WriteAllLines(Path.Combine(worktreePath, "beta.cs"), Enumerable.Repeat("line", 6));
            var expectedViolations = SourceSizeRatchetPreflight.BlockingViolationMessages(
                SourceSizeRatchetPreflight.Evaluate(worktreePath));
            Assert.Equal(2, expectedViolations.Count);

            var dispatchedRoles = new List<AgentRole>();
            var notes = new List<string>();
            TaskBriefSource? nextDeveloperBrief = null;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: candidate =>
                {
                    dispatchedRoles.Add(candidate.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    nextDeveloperBrief = kernel.BuildTaskBriefSource(goal.Id, developer.Id);
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                    kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: roundKind),
                recordTaskNote: (_, _, message) => notes.Add(message),
                executionDirectory: executionDirectory);
            driver.ConfigureDeveloperCompletionStructuralPreflightRetry(kernel);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.Equal([AgentRole.Developer], dispatchedRoles);
            var feedback = Assert.Single(kernel.GetTask(goal.Id, developer.Id).CriterionRetryFeedback);
            foreach (var violation in expectedViolations)
            {
                Assert.Contains(violation, feedback, StringComparison.Ordinal);
            }
            Assert.Contains("alpha.cs", feedback, StringComparison.Ordinal);
            Assert.Contains("3 lines", feedback, StringComparison.Ordinal);
            Assert.Contains("ceiling of 2", feedback, StringComparison.Ordinal);
            Assert.Contains("beta.cs", feedback, StringComparison.Ordinal);
            Assert.Contains("6 lines", feedback, StringComparison.Ordinal);
            Assert.Contains("ceiling of 4", feedback, StringComparison.Ordinal);
            Assert.DoesNotContain(staleFeedback, feedback, StringComparison.Ordinal);

            var segment = Assert.Single(Assert.IsType<TaskBriefSource>(nextDeveloperBrief).Segments
                .Where(item => item.TypedProjectionIdentity == new LogicalArtifactIdentity("task/criterion-retry-feedback.json")));
            var renderedFeedback = string.Join(Environment.NewLine, segment.Lines);
            Assert.Contains(feedback, renderedFeedback, StringComparison.Ordinal);
            Assert.DoesNotContain(staleFeedback, renderedFeedback, StringComparison.Ordinal);
            Assert.Equal(feedback, kernel.GetTask(goal.Id, developer.Id).AcceptedRetryFeedback?.Message);
            Assert.Equal(RetryCause.NewSourceFinding, developer.PendingRetryCause);
            Assert.Equal(RetryRoundKind.Mechanical, developer.PendingRetryRoundKind);
            Assert.StartsWith("developer-completion structural pre-check failed:", Assert.Single(notes), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(executionDirectory, recursive: true);
        }
    }
}
