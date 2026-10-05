using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliSingleGoalReportRouteParityTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ExplicitForms_PreserveWriterOutputAndErrors_WithOrWithoutWorktree(bool withWorktree)
    {
        var root = CreateTempDirectory();
        try
        {
            string? baseCommit = null;
            string? resultCommit = null;
            if (withWorktree)
                (baseCommit, resultCommit) = CreateGitWorktree(root);
            var kernel = CliSingleGoalReportReadOnlyRouteTests.CreateReportSeed(
                root, budgetHold: true, baseCommit: baseCommit, resultCommit: resultCommit);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var target = kernel.Goals.Single(goal => goal.Id.Value.StartsWith("abc10000"));
            if (withWorktree)
            {
                Xunit.Assert.NotNull(GoalWorktreeLayout.TryResolve(root, target.Id));
                Xunit.Assert.Contains(target.Tasks, task => task.Status == WorkTaskStatus.Running &&
                    task.LastDispatch?.ResultCommit is null);
            }
            else
                Xunit.Assert.Null(GoalWorktreeLayout.TryResolve(root, target.Id));

            // Prove the bystander's hold discriminates an accidental multi-goal hydration.
            var agents = CliSingleGoalReportReadOnlyRouteTests.ReportAgents();
            var profiles = WorkerProfileCatalog.Default();
            var targetPlan = SubscriptionPlanBuilder.Build(target, agents, profiles, providerHoldScope: [target]);
            var fullPlan = SubscriptionPlanBuilder.Build(target, agents, profiles, providerHoldScope: kernel.Goals);
            Xunit.Assert.All(targetPlan.Items, item => Xunit.Assert.Null(item.ProviderBudgetHold));
            Xunit.Assert.Contains(fullPlan.Items, item => item.ProviderBudgetHold is not null);
            var targetPlanText = CaptureConsole(() => ConsoleViews.PrintSubscriptionPlan(targetPlan));
            var fullPlanText = CaptureConsole(() => ConsoleViews.PrintSubscriptionPlan(fullPlan));
            Xunit.Assert.NotEqual(targetPlanText, fullPlanText);

            foreach (var args in CliSingleGoalReportReadOnlyRouteTests.ExplicitForms("abc10000"))
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
                var expected = Execute(args, kernel, workspace, skipReadOnlyRoute: true);
                var actual = Execute(args, kernel, workspace, skipReadOnlyRoute: false);
                if (!withWorktree && args.Contains("--flat") && !args.Contains("--json"))
                    Xunit.Assert.Equal(string.Empty, expected);
                else
                    Xunit.Assert.NotEmpty(expected);
                if (withWorktree && args[0] == "goal-changes" && args[1] == "abc10000")
                {
                    if (!args.Contains("--working"))
                        Xunit.Assert.Contains("committed-change.txt", expected);
                    if (!args.Contains("--committed"))
                        Xunit.Assert.Contains("working-change.txt", expected);
                }
                Xunit.Assert.Equal(expected, actual);
            }

            foreach (var prefix in new[] { "missing", "abc", "help" })
            foreach (var args in CliSingleGoalReportReadOnlyRouteTests.ExplicitForms(prefix))
                AssertErrorParity(args, kernel, workspace);

            foreach (var args in CliSingleGoalReportReadOnlyRouteTests.ExplicitForms("abc10000"))
                AssertErrorParity(args, kernel, workspace, target.Id.Value);

            // Resolution precedes handler flag validation on the writer path.
            AssertErrorParity(["goal-changes", "missing", "--bogus"], kernel, workspace);
            AssertErrorParity(["goal-changes", "abc10000", "--bogus"], kernel, workspace);
            // The writer selector skips flag values, but the unchanged handler treats them as prefixes.
            foreach (var args in new string[][]
            {
                ["goal-changes", "--role", "Developer", "abc10000"],
                ["goal-changes", "--task", "2", "abc10000"]
            })
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
                AssertErrorParity(args, kernel, workspace);
            }
        }
        finally
        {
            // Git object files are read-only on Windows; preserve other attributes during cleanup.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertErrorParity(
        string[] args, AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        string? unavailableGoalId = null)
    {
        var expected = Xunit.Record.Exception(() => Execute(args, kernel, workspace, true, unavailableGoalId));
        var actual = Xunit.Record.Exception(() => Execute(args, kernel, workspace, false, unavailableGoalId));
        Xunit.Assert.NotNull(expected);
        Xunit.Assert.NotNull(actual);
        Xunit.Assert.Equal(expected.GetType(), actual.GetType());
        Xunit.Assert.Equal(expected.Message, actual.Message);
        if (unavailableGoalId is not null)
        {
            Xunit.Assert.IsType<KeyNotFoundException>(actual);
            Xunit.Assert.Equal($"Goal '{unavailableGoalId}' was not found.", actual.Message);
        }
        else if (args.Contains("missing") || args.Contains("help"))
            Xunit.Assert.IsType<KeyNotFoundException>(actual);
        else if (args.Contains("abc"))
            Xunit.Assert.IsType<InvalidOperationException>(actual);
    }

    private static string Execute(
        string[] args, AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        bool skipReadOnlyRoute, string? unavailableGoalId = null)
    {
        var repository = new ProbeStateRepository(kernel);
        if (unavailableGoalId is not null)
            repository.UnavailableGoalIds.Add(unavailableGoalId);
        IReadOnlyList<AgentDefinition> agents = CliSingleGoalReportReadOnlyRouteTests.ReportAgents();
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => Xunit.Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]),
            ref profiles, ref currentGoal, skipReadOnlyRoute: skipReadOnlyRoute)));
    }

    private static (string BaseCommit, string ResultCommit) CreateGitWorktree(string root)
    {
        RunGit(root, "init");
        File.WriteAllText(Path.Combine(root, "base.txt"), "base\n");
        RunGit(root, "add", "base.txt");
        RunGit(root, "commit", "-m", "base");
        var baseCommit = RunGit(root, "rev-parse", "HEAD").Trim();
        var worktree = GoalWorktreeLayout.WorktreePath(root, new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"));
        RunGit(root, "worktree", "add", "-b", "report-changes", worktree);
        File.WriteAllText(Path.Combine(worktree, "committed-change.txt"), "committed\n");
        RunGit(worktree, "add", "committed-change.txt");
        RunGit(worktree, "commit", "-m", "report change");
        var resultCommit = RunGit(worktree, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(worktree, "working-change.txt"), "working\n");
        return (baseCommit, resultCommit);
    }

    private static string RunGit(string root, params string[] args)
    {
        var result = GitCli.Run(root,
            ["-c", "user.name=Report Tests", "-c", "user.email=report-tests@example.invalid",
             "-c", "commit.gpgsign=false", .. args]);
        Xunit.Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result.Error}");
        return result.Output;
    }
}
