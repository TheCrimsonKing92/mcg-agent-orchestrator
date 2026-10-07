using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class DeveloperCompletionStructuralPreflightRetryFeedbackTestsClassCeiling
{
    [Fact]
    public void ClassTotalOverCeiling_ReachesNextDeveloperBrief()
    {
        var executionDirectory = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal("Deliver class-ceiling structural pre-check violation");
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != tester.Id))
            {
                PassVerification(kernel, goal, task);
            }

            var worktreePath = GoalWorktrees.WorktreePath(executionDirectory, goal.Id);
            var authorityPath = Path.Combine(worktreePath,
                SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
            File.WriteAllLines(authorityPath,
            [
                "new SourceSizeCeiling(\"alpha.cs\", 10)",
                "new SourceClassCeiling(\"Widget\", 4, 5)"
            ]);
            File.WriteAllLines(Path.Combine(worktreePath, "alpha.cs"), ["line"]);
            foreach (var name in new[] { "Widget.cs", "Widget.Other.cs" })
            {
                File.WriteAllLines(Path.Combine(worktreePath, "src", name),
                    ["internal sealed partial class Widget", "{", "}"]);
            }

            var findings = DeveloperCompletionStructuralPreflight.Evaluate(worktreePath);
            Assert.True(findings.HasViolation);
            Assert.Contains("class:Widget", findings.Message, StringComparison.Ordinal);
            Assert.Contains("class total-line ceiling of 4", findings.Message, StringComparison.Ordinal);
            Assert.Contains("Extract members into a separately owned type", findings.Message, StringComparison.Ordinal);

            var notes = new List<string>();
            var dispatchedRoles = new List<AgentRole>();
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
            Assert.Contains(findings.Message, feedback, StringComparison.Ordinal);
            var segment = Assert.Single(Assert.IsType<TaskBriefSource>(nextDeveloperBrief).Segments
                .Where(item => item.TypedProjectionIdentity == new LogicalArtifactIdentity("task/criterion-retry-feedback.json")));
            Assert.Contains(feedback, string.Join(Environment.NewLine, segment.Lines), StringComparison.Ordinal);
            var note = Assert.Single(notes);
            Assert.StartsWith("developer-completion structural pre-check failed:", note, StringComparison.Ordinal);
            Assert.Contains("class:Widget", note, StringComparison.Ordinal);
            Assert.Contains("separately owned type", note, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(executionDirectory, recursive: true);
        }
    }
}
