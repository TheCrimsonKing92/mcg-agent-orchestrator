using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the support fixture owns a unique repository, worktree, and log directory.
public sealed class PlannerDispatchCompletionTestsContractDiagnostic : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("*.cs")]
    [Xunit.InlineData("src/Missing?.cs")]
    public void MarkedWildcardCompletion_RecordsContractFailure(string citation)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));
        var plan = PlanWithNewTarget(citation);
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Planner, plan, string.Empty, clock, includePlannerContract: false);
        Xunit.Assert.Equal("0", File.ReadAllText(process.ExitCodePath).Trim());
        Xunit.Assert.Equal(plan, File.ReadAllText(process.StandardOutputPath));
        Xunit.Assert.Empty(ReadGit(process.WorkingDirectory, ["status", "--porcelain"]));

        new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.Equal(1, task.LastVerification!.ExitCode);
        var failure = Xunit.Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.TaskFailed));
        Xunit.Assert.StartsWith(
            "Dispatch failed: rule=planner-output-contract-rejected: Planner output contract failed.",
            failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("path contains a wildcard", failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Offending citation: '{citation}'", failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Offending plan line ", failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Empty(ReadGit(process.WorkingDirectory, ["status", "--porcelain"]));
    }

    [Xunit.Fact]
    public void ConcreteNewFileCompletion_RecordsSuccess()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));
        var plan = PlanWithNewTarget("src/Missing.cs");
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Planner, plan, string.Empty, clock, includePlannerContract: false);
        Xunit.Assert.False(File.Exists(Path.Combine(process.WorkingDirectory, "src", "Missing.cs")));
        Xunit.Assert.Equal(plan, File.ReadAllText(process.StandardOutputPath));
        Xunit.Assert.Empty(ReadGit(process.WorkingDirectory, ["status", "--porcelain"]));

        new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        Xunit.Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.TaskFailed);
        Xunit.Assert.Empty(ReadGit(process.WorkingDirectory, ["status", "--porcelain"]));
    }

    [Xunit.Fact]
    public void MissingTargetCitationAppearsInTaskFailedMessageBeforeCommand()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero));
        var plan = PlannerContractPlanFixture().Replace(
            "`seed.txt`", "`src/Missing.cs`", StringComparison.Ordinal);
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Planner, plan, string.Empty, clock);
        Xunit.Assert.True(File.Exists(process.ExitCodePath));
        Xunit.Assert.Contains("`src/Missing.cs`", File.ReadAllText(process.StandardOutputPath), StringComparison.Ordinal);

        new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        var failure = Xunit.Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.TaskFailed));
        Xunit.Assert.StartsWith(
            "Dispatch failed: rule=planner-output-contract-rejected: Planner output contract failed.",
            failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Offending plan line ", failure.Message, StringComparison.Ordinal);
        Xunit.Assert.EndsWith(" Command: " + task.LastDispatch!.Command, failure.Message, StringComparison.Ordinal);
    }

    private static string PlanWithNewTarget(string citation)
    {
        var plan = PlannerContractPlanFixture().ReplaceLineEndings("\n");
        const string heading = "## Target seams and symbols";
        var bodyStart = plan.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var bodyEnd = plan.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        return plan[..bodyStart] +
            $"\n\nValidate new file targets against the cited implementation path.\n- Inspect `{citation}` — new class.\n" +
            plan[bodyEnd..];
    }
}
