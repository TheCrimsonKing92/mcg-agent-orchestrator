using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its kernel, repository and temporary workspace.
public sealed class CliGoalReportReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("evidence", "abc10000")]
    [Xunit.InlineData("stages", "abc10000")]
    [Xunit.InlineData("gates", "abc10000")]
    [Xunit.InlineData("verify-needed", "abc10000")]
    [Xunit.InlineData("input-needed", "abc10000")]
    [Xunit.InlineData("revise", "abc10000", "--history")]
    [Xunit.InlineData("EVIDENCE", "ABC10000")]
    [Xunit.InlineData("REVISE", "ABC10000", "--HISTORY")]
    public void ExplicitPrefix_ClassifiesAndUsesOnlyTargetedReads(params string[] args)
    {
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var root = CreateTempDirectory();
        try
        {
            var kernel = CreateGoalReportSeed(root);
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

            Xunit.Assert.NotEmpty(output);
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
    [Xunit.InlineData("evidence")]
    [Xunit.InlineData("stages")]
    [Xunit.InlineData("gates")]
    [Xunit.InlineData("verify-needed")]
    [Xunit.InlineData("input-needed")]
    [Xunit.InlineData("revise", "abc10000")]
    [Xunit.InlineData("revise", "abc10000", "new", "brief", "text", "--history")]
    [Xunit.InlineData("revise", "--history")]
    [Xunit.InlineData("revise", "abc10000", "--histroy")]
    [Xunit.InlineData("revise", "abc10000", "--history", "extra")]
    [Xunit.InlineData("revise", "--goal", "--history")]
    [Xunit.InlineData("revise", "help", "--history")]
    public void OtherRevisionAndCurrentGoalShapes_KeepTheirExistingRoute(params string[] args)
    {
        AssertDeclined(args);
    }

    [Xunit.Theory]
    [Xunit.InlineData("evidence")]
    [Xunit.InlineData("stages")]
    [Xunit.InlineData("gates")]
    [Xunit.InlineData("verify-needed")]
    [Xunit.InlineData("input-needed")]
    public void ReportHelpFlagsAndExtraTokens_KeepTheirExistingRoute(string verb)
    {
        foreach (var args in new string[][]
        {
            [verb, "--help"], [verb, "-h"], [verb, "help"], [verb, "HELP"],
            [verb, "--unknown"], [verb, "-unknown"], [verb, " "],
            [verb, "abc10000", "extra"], [verb, "abc10000", "--history"]
        })
            AssertDeclined(args);
        AssertDeclined(["revise", "abc10000", "--help"]);
        AssertDeclined(["revise", "abc10000", "-h"]);
    }

    private static void AssertDeclined(string[] args)
    {
        Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        Xunit.Assert.False(CliReadOnlyCommandRunner.TryExecute(
            args, repository, OrchestratorWorkspace.ForDirectory(Path.GetTempPath()),
            new InMemoryModelProviderRegistry([]), null,
            ref agents, ref profiles, ref currentGoal, out var changed));
        Xunit.Assert.False(changed);
        Xunit.Assert.Equal(0, repository.ListGoalMetadataCount);
        Xunit.Assert.Equal(0, repository.LoadGoalsCount);
        Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
    }

    internal static string[][] ExplicitForms(string prefix) =>
    [
        ["evidence", prefix], ["stages", prefix], ["gates", prefix],
        ["verify-needed", prefix], ["input-needed", prefix], ["revise", prefix, "--history"]
    ];

    internal static AgentOrchestratorKernel CreateGoalReportSeed(string root)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Report dispatch evidence", AgentRole.Developer);
        var waitingTask = new TaskSpec(TaskId.New(), "Await report clarification", AgentRole.Tester);
        var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"),
            "Initial report brief", [task, waitingTask]);
        kernel.CreateGoal(new GoalId("abc20000bbbbbbbbbbbbbbbbbbbbbbbb"), "Other goal");
        kernel.ReviseGoalBrief(goal.Id, "Revised report brief", "seeded revision reason");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var recordedAt = DateTimeOffset.Parse("2026-09-24T00:00:00+00:00");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "seeded-report-dispatch", root, recordedAt));
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "seeded-report-dispatch", root, 0, "seeded verification evidence", string.Empty, recordedAt));
        kernel.RequestHumanInput(goal.Id, waitingTask.Id, "seeded report question");
        return kernel;
    }
}
