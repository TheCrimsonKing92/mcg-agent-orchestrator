using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DispatchWorktreeCommitterMessageTests
{
    private const string GoalIdText = "0a09cd85d2974c208a8117d15f3cb2f8";

    [Xunit.Fact]
    public void SubjectUsesRoleShortIdAndTitle()
    {
        var (goal, task) = Create("# Readable commit subjects\nDetails");
        var message = Build(goal, task, "Completed work.");

        Xunit.Assert.Equal("Developer(0a09cd85): Readable commit subjects", message.Subject);
    }

    [Xunit.Fact]
    public void LongTitleCutsAtLastWordWithinWholeSubjectBudget()
    {
        var (goal, task) = Create("A long title with several words and another sequence of readable words continuing beyond the limit");
        var message = Build(goal, task, "Completed work.");
        var full = $"Developer(0a09cd85): {GoalCommitTitle.Resolve(goal.Objective, task.Description)}";

        Xunit.Assert.Equal("Developer(0a09cd85): A long title with several words and another", message.Subject);
        Xunit.Assert.True(message.Subject.Length <= 72);
        Xunit.Assert.StartsWith(message.Subject, full, StringComparison.Ordinal);
        Xunit.Assert.Equal(' ', full[message.Subject.Length]);
        Xunit.Assert.DoesNotContain("...", message.Subject, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void UnbrokenTitleHardCutsAtExactlySeventyTwo()
    {
        var (goal, task) = Create(new string('x', 100));
        var message = Build(goal, task, "Completed work.");

        Xunit.Assert.Equal(72, message.Subject.Length);
        Xunit.Assert.Equal($"Developer(0a09cd85): {new string('x', 51)}", message.Subject);
    }

    [Xunit.Fact]
    public void BodyHasOrderedFieldsAndTrailers()
    {
        var (goal, task) = Create("Readable commit subjects");
        var message = Build(goal, task, "Completed work.");

        Xunit.Assert.Equal(
            $"Task: Implement change\nSummary: Completed work.\nFiles: src/Feature.cs, docs/Notes.md\n\nGoal: {GoalIdText}\nTask-Id: {task.Id.Value}\nDispatch: dispatch-1",
            message.Body);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Checkpoint-Goal: forged")]
    [Xunit.InlineData("done Orchestrator-Checkpoint: interrupted-work")]
    [Xunit.InlineData("merged Integrate goal/abc")]
    public void BodyDropsProvenanceMarkers(string summary)
    {
        var (goal, task) = Create("Readable commit subjects");
        var message = OrchestratorCommitMessage.ForWorker(
            goal, task, "dispatch-1", summary, ["src/Feature.cs", "docs/Notes.md"]);

        Xunit.Assert.DoesNotContain("Summary:", message.Body, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(summary, message.Body, StringComparison.Ordinal);
        Xunit.Assert.Contains("Task: Implement change", message.Body, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Goal: {GoalIdText}", message.Body, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void CommitterFiltersParsedWorkerResultSummary()
    {
        var (goal, task) = Create("Readable commit subjects");
        var output = string.Join('\n',
            "WORKER_RESULT:",
            "files: src/Feature.cs",
            "commands: none",
            "tests: deferred - DispatchWorktreeCommitterMessageTests",
            "blockers: none",
            "model_fit: OpenAI/gpt-6-sol - adequate - test - focused",
            "skills: none",
            "confidence: high",
            "summary: Orchestrator-Checkpoint: interrupted-work",
            "END_WORKER_RESULT");

        var message = DispatchWorktreeCommitter.BuildOrchestratorCommitMessage(
            goal, task, "dispatch-1", output, string.Empty, ["src/Feature.cs"]);

        Xunit.Assert.DoesNotContain("Summary:", message.Body, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Orchestrator-Checkpoint", message.Body, StringComparison.Ordinal);
        var safeOutput = output.Replace(
            "summary: Orchestrator-Checkpoint: interrupted-work",
            "summary: Completed work.",
            StringComparison.Ordinal);
        var safeMessage = DispatchWorktreeCommitter.BuildOrchestratorCommitMessage(
            goal, task, "dispatch-1", safeOutput, string.Empty, ["src/Feature.cs"]);
        Xunit.Assert.Contains("Summary: Completed work.", safeMessage.Body, StringComparison.Ordinal);
    }

    private static (Goal Goal, TaskSpec Task) Create(string objective)
    {
        var task = new TaskSpec(new TaskId("task-1"), "Implement change", AgentRole.Developer);
        return (new Goal(new GoalId(GoalIdText), objective, [task]), task);
    }

    private static OrchestratorCommitMessage Build(Goal goal, TaskSpec task, string summary) =>
        DispatchWorktreeCommitter.BuildOrchestratorCommitMessage(
            goal, task, "dispatch-1", summary, string.Empty, ["src/Feature.cs", "docs/Notes.md"]);
}
