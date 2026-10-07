using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its temporary root, repository probe and console capture.
public sealed class CliPlanReportReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("retention-plan", "abc10000", "Retention plan goal: abc10000")]
    [Xunit.InlineData("supervisor", "abc10000", "Supervisor goal: abc10000")]
    [Xunit.InlineData("SUPERVISOR", "ABC10000", "Supervisor goal: abc10000")]
    public void ExplicitPrefix_ClassifiesAndUsesOnlyTargetedReads(string verb, string prefix, string header)
    {
        string[] args = [verb, prefix];
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var root = CreateTempDirectory();
        try
        {
            var kernel = CliSingleGoalReportReadOnlyRouteTests.CreateReportSeed(root);
            var target = kernel.Goals.Single(goal => goal.Id.Value.StartsWith("abc10000"));
            var other = kernel.Goals.Single(goal => goal.Id != target.Id);
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = CliSingleGoalReportReadOnlyRouteTests.ReportAgents();
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

            Xunit.Assert.Contains(header, output);
            Xunit.Assert.Equal(target.Id, currentGoal!.Id);
            Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.Equal([target.Id.Value], repository.LoadedGoalIds);
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
            Xunit.Assert.Equal(0, repository.MutationAttempts);
            Xunit.Assert.Equal(0, repository.SaveAttempts);
            Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("retention-plan")]
    [Xunit.InlineData("supervisor")]
    [Xunit.InlineData("retention-plan", "--help")]
    [Xunit.InlineData("supervisor", "--help")]
    [Xunit.InlineData("supervisor", "--apply-safe")]
    [Xunit.InlineData("supervisor", "abc10000", "--apply-safe")]
    [Xunit.InlineData("supervisor", "abc10000", "--autonomy", "safe-auto")]
    [Xunit.InlineData("supervisor", "abc10000", "--policy", "safe-auto")]
    [Xunit.InlineData("retention-plan", "abc10000", "extra")]
    [Xunit.InlineData("retention-plan", "abc10000", "--help")]
    [Xunit.InlineData("supervisor", "abc10000", "-h")]
    [Xunit.InlineData("retention-plan", "")]
    [Xunit.InlineData("supervisor", " ")]
    [Xunit.InlineData("retention-plan", " ")]
    [Xunit.InlineData("supervisor", "")]
    [Xunit.InlineData("retention-plan", "-prefix")]
    [Xunit.InlineData("supervisor", "-prefix")]
    [Xunit.InlineData("dogfood-eval", "abc10000")]
    [Xunit.InlineData("goal-diagnostics", "abc10000", "extra")]
    [Xunit.InlineData("failure-triage", "abc10000", "extra")]
    [Xunit.InlineData("goal-timing", "abc10000", "extra")]
    [Xunit.InlineData("readiness", "abc10000")]
    [Xunit.InlineData("goal-recovery", "abc10000")]
    public void OtherForms_KeepTheirExistingRoute(params string[] args)
    {
        Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
        IReadOnlyList<AgentDefinition> agents = CliSingleGoalReportReadOnlyRouteTests.ReportAgents();
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        Xunit.Assert.False(CliReadOnlyCommandRunner.TryExecute(
            args, repository, OrchestratorWorkspace.ForDirectory(Path.GetTempPath()),
            new InMemoryModelProviderRegistry([]), null,
            ref agents, ref profiles, ref currentGoal, out var changed));
        Xunit.Assert.False(changed);
        Xunit.Assert.Null(currentGoal);
        Xunit.Assert.Equal(0, repository.ListGoalMetadataCount);
        Xunit.Assert.Equal(0, repository.LoadGoalsCount);
        Xunit.Assert.Equal(0, repository.FullLoadAttempts);
        Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
        Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
        Xunit.Assert.Equal(0, repository.MutationAttempts);
        Xunit.Assert.Equal(0, repository.SaveAttempts);
        Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
    }

    [Xunit.Theory]
    [Xunit.InlineData("retention-plan")]
    [Xunit.InlineData("supervisor")]
    public void HelpToken_IsAnExplicitGoalPrefix(string verb)
    {
        string[] args = [verb, "help"];
        Xunit.Assert.False(CliCommandHelp.IsCommandSpecificHelp(args));
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
    }
}
