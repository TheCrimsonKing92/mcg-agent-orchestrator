using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliSingleGoalReportReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("subscription-plan", "abc10000")]
    [Xunit.InlineData("subscription-plan", "abc10000", "extra")]
    [Xunit.InlineData("goal-changes", "abc10000")]
    [Xunit.InlineData("goal-changes", "abc10000", "--json")]
    [Xunit.InlineData("goal-changes", "abc10000", "--role", "Developer", "--flat")]
    [Xunit.InlineData("goal-changes", "abc10000", "--task", "2", "--committed")]
    [Xunit.InlineData("SUBSCRIPTION-PLAN", "ABC10000")]
    [Xunit.InlineData("GOAL-CHANGES", "ABC10000", "--JSON")]
    public void ExplicitPrefix_ClassifiesAndUsesOnlyTargetedReads(params string[] args)
    {
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var root = CreateTempDirectory();
        try
        {
            var kernel = CreateReportSeed(root);
            var target = kernel.Goals.Single(goal => goal.Id.Value.StartsWith("abc10000"));
            var other = kernel.Goals.Single(goal => goal.Id != target.Id);
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = ReportAgents();
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

            // Flat reports print only changed paths; this seed has no worktree or recorded files.
            if (args.Contains("--flat") && !args.Contains("--json"))
                Xunit.Assert.Equal(string.Empty, output);
            else
                Xunit.Assert.NotEmpty(output);
            Xunit.Assert.Equal(target.Id, currentGoal!.Id);
            Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.Equal([target.Id.Value], repository.LoadedGoalIds);
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
    [Xunit.InlineData("subscription-plan")]
    [Xunit.InlineData("goal-changes")]
    [Xunit.InlineData("subscription-plan", " ")]
    [Xunit.InlineData("goal-changes", " ")]
    [Xunit.InlineData("goal-changes", "--all", "--json")]
    [Xunit.InlineData("goal-changes", "--role", "Developer")]
    [Xunit.InlineData("goal-changes", "--task", "2")]
    [Xunit.InlineData("subscription-plan", "--help")]
    [Xunit.InlineData("goal-changes", "abc10000", "-h")]
    [Xunit.InlineData("goal-diagnostics", "abc10000")]
    [Xunit.InlineData("failure-triage", "abc10000")]
    [Xunit.InlineData("goal-timing", "abc10000")]
    public void ImplicitPrefixHelpAndClockReports_KeepTheirExistingRoute(params string[] args)
    {
        Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
        IReadOnlyList<AgentDefinition> agents = ReportAgents();
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

    [Xunit.Theory]
    [Xunit.InlineData("subscription-plan")]
    [Xunit.InlineData("goal-changes")]
    public void HelpToken_IsAnExplicitGoalPrefix(string verb)
    {
        string[] args = [verb, "help"];
        Xunit.Assert.False(CliCommandHelp.IsCommandSpecificHelp(args));
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
    }

    internal static string[][] ExplicitForms(string prefix) =>
    [
        ["subscription-plan", prefix], ["goal-changes", prefix], ["goal-changes", prefix, "--json"],
        ["goal-changes", prefix, "--flat", "--committed"], ["goal-changes", prefix, "--role", "Developer"],
        ["goal-changes", prefix, "--working"], ["goal-changes", prefix, "--all", "--flat", "--json"]
    ];

    internal static IReadOnlyList<AgentDefinition> ReportAgents() =>
    [
        new(new AgentId("report-developer"), "Report Developer", AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias,
                ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli"))
    ];

    internal static AgentOrchestratorKernel CreateReportSeed(
        string root, bool includeOther = true, bool budgetHold = false,
        string? baseCommit = null, string? resultCommit = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var recordedTask = new TaskSpec(TaskId.New(), "Recorded report change", AgentRole.Developer);
        var workingTask = new TaskSpec(TaskId.New(), "Working report change", AgentRole.Developer);
        var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"),
            "Read-only single-goal reports", [recordedTask, workingTask]);
        kernel.ActivateGoal(goal.Id, ReportAgents());
        var recordedAt = DateTimeOffset.Parse("2026-09-24T00:00:00+00:00");
        kernel.RecordTaskDispatch(goal.Id, recordedTask.Id, new TaskDispatchRecord(
            "codex-cli", "seeded-report-dispatch", root, recordedAt,
            ProviderName: "OpenAI", BaseCommit: baseCommit, ResultCommit: resultCommit));
        kernel.RecordTaskVerification(goal.Id, recordedTask.Id, new TaskVerificationRecord(
            "seeded-report-dispatch", root, 0, "seeded successful verification", string.Empty, recordedAt));
        if (baseCommit is not null)
            kernel.RecordTaskDispatch(goal.Id, workingTask.Id, new TaskDispatchRecord(
                "codex-cli", "seeded-working-dispatch", root, recordedAt.AddMinutes(1),
                ProviderName: "OpenAI", BaseCommit: resultCommit));

        if (includeOther)
        {
            var heldTask = new TaskSpec(TaskId.New(), "Other provider work", AgentRole.Developer);
            var other = kernel.CreateGoal(new GoalId("abc20000bbbbbbbbbbbbbbbbbbbbbbbb"),
                "Other provider goal", [heldTask]);
            if (budgetHold)
            {
                kernel.ActivateGoal(other.Id, ReportAgents());
                kernel.RecordTaskDispatch(other.Id, heldTask.Id, new TaskDispatchRecord(
                    "codex-cli", "seeded-held-dispatch", root, recordedAt, ProviderName: "OpenAI"));
                kernel.RecordTaskVerification(other.Id, heldTask.Id, new TaskVerificationRecord(
                    "seeded-held-dispatch", root, 1, string.Empty,
                    "API error (status 402 Payment Required): usage balance exhausted", recordedAt.AddMinutes(1),
                    StandardErrorPath: "provider.err.log", ProviderFailureKind: ProviderFailureKind.BudgetExhausted,
                    DispatchStartedAt: recordedAt));
            }
        }
        return kernel;
    }
}
