using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliTimelineReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("timeline", "abc10000")]
    [Xunit.InlineData("task-timeline", "abc10000", "1")]
    [Xunit.InlineData("task-timeline", "--goal", "abc10000", "1")]
    public void ExplicitPrefix_ClassifiesAndUsesOnlyTargetedReads(params string[] args)
    {
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var root = CreateTempDirectory();
        try
        {
            var kernel = CreateTimelineSeed(root);
            var target = kernel.Goals.Single(goal => goal.Id.Value.StartsWith("abc10000"));
            var other = kernel.Goals.Single(goal => goal.Id != target.Id);
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = other;
            var output = CaptureConsole(() =>
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(
                    args, repository, OrchestratorWorkspace.ForDirectory(root),
                    new InMemoryModelProviderRegistry([]), null,
                    ref agents, ref profiles, ref currentGoal, out var changed));
                Xunit.Assert.False(changed);
            });

            Xunit.Assert.Contains("seeded timeline progress", output);
            Xunit.Assert.Equal(target.Id, currentGoal!.Id);
            Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.Equal([target.Id.Value], repository.LoadedGoalIds);
            Xunit.Assert.DoesNotContain(other.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.Equal(0, repository.MutationAttempts);
            Xunit.Assert.Equal(0, repository.SaveAttempts);
            Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("timeline")]
    [Xunit.InlineData("task-timeline", "1")]
    [Xunit.InlineData("timeline", "--goal", "abc10000")]
    [Xunit.InlineData("timeline", "abc10000", "extra")]
    [Xunit.InlineData("task-timeline", "1", "--goal", "abc10000")]
    [Xunit.InlineData("task-timeline", "--goal=abc10000", "1")]
    [Xunit.InlineData("task-timeline", "12345678", "1")]
    [Xunit.InlineData("task-timeline", "--goal", "abc10000")]
    [Xunit.InlineData("timeline", "--help")]
    [Xunit.InlineData("task-timeline", "abc10000", "-h")]
    [Xunit.InlineData("timeline", " ")]
    public void OtherShapes_KeepTheirExistingRoute(params string[] args) =>
        Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));

    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Completed)]
    [Xunit.InlineData(GoalStatus.Cancelled)]
    public void TerminalGoal_UsesTargetedReadAndUpdatesCurrentGoal(GoalStatus status)
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = CreateTimelineSeed(root).ExportSnapshot();
            var targetId = seed.Goals.Single(goal => goal.Id.StartsWith("abc10000")).Id;
            var kernel = AgentOrchestratorKernel.FromSnapshot(seed with
            {
                Goals = seed.Goals.Select(goal => goal.Id == targetId ? goal with { Status = status } : goal).ToArray()
            });
            foreach (var args in ExplicitForms(targetId[..8]))
            {
                var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
                IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
                var profiles = WorkerProfileCatalog.Default();
                Goal? currentGoal = null;
                CaptureConsole(() =>
                {
                    Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(
                        args, repository, OrchestratorWorkspace.ForDirectory(root),
                        new InMemoryModelProviderRegistry([]), null,
                        ref agents, ref profiles, ref currentGoal, out var changed));
                    Xunit.Assert.False(changed);
                });
                Xunit.Assert.Equal(targetId, currentGoal!.Id.Value);
                Xunit.Assert.Equal(status, currentGoal.Status);
                Xunit.Assert.Equal(1, repository.LoadGoalsCount);
                Xunit.Assert.Equal([targetId], repository.LoadedGoalIds);
                Xunit.Assert.Equal(0, repository.FullLoadAttempts);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static string[][] ExplicitForms(string prefix) =>
    [
        ["timeline", prefix],
        ["task-timeline", prefix, "1"],
        ["task-timeline", "--goal", prefix, "1"]
    ];

    internal static AgentOrchestratorKernel CreateTimelineSeed(string root)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Timeline task", AgentRole.Developer);
        var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"),
            "Timeline target", [task]);
        kernel.CreateGoal(new GoalId("abc20000bbbbbbbbbbbbbbbbbbbbbbbb"), "Other goal");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "seeded-dispatch-command", root, DateTimeOffset.Parse("2026-09-24T00:00:00+00:00")));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "seeded timeline progress");
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "seeded timeline failure");
        return kernel;
    }
}
