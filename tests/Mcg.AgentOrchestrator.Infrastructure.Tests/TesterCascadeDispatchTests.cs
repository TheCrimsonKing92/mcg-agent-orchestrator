using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: isolated temporary repositories, injected launcher probe, no environment mutation or worker launch.
public sealed class TesterCascadeDispatchTests : WorkerDispatchTestSupport
{
    private const string CatalogJson = """
        {"Agents":[{"Id":{"Value":"openai-tester"},"Name":"Codex GPT-6.1 Sol tester","Role":"Tester","Model":{"ProviderName":"OpenAI","ModelName":"gpt-6.1-sol","Capabilities":"Text, Code, ToolUse","SubscriptionMode":"ApiKey","ReasoningEffort":"medium","MaxOutputTokens":2048},"Status":"Available","ExecutionPolicy":"PreferSubscription","Subscription":{"WorkerProfileName":"codex-cli","ModelAlias":"gpt-6.1-sol","ReasoningEffort":"medium"},"ComplexModel":{"ProviderName":"OpenAI","ModelName":"gpt-6.1-sol","Capabilities":"Text, Code, ToolUse","SubscriptionMode":"ApiKey","ReasoningEffort":"high","MaxOutputTokens":4096},"ReasoningEffortPolicy":null,"IsProviderRoutingConstrained":true}]}
        """;
    private const string OffJson = """
        {"name":"PermissiveCap5Rollback","maxConcurrentPaidWorkers":4,"acceptanceWidth":1,"maxTotalBudget":20.00,"maxCriterionRetries":2,"plannerSampleCount":1,"perProviderBudgetCaps":null,"autoPromoteRiskThreshold":"Broad","cascadeTesterCheapFirst":false,"transitionMap":{"Created":"Auto","AwaitingClarification":"Auto","WorkspaceReady":"Auto","Dispatched":"Auto","Running":"Auto","AwaitingVerification":"Auto","Verifying":"Auto","Verified":"Auto","AcceptanceFailed":"Auto","Merged":"Auto","Recorded":"Auto","CleanedUp":"Auto","Failed":"Auto","Blocked":"Auto","AwaitingHumanInput":"Auto"}}
        """;
    private const string MissingTests = "WORKER_RESULT:\nfiles: none\ncommands: none\nblockers: none\nmodel_fit: OpenAI/gpt-6-luna - adequate - verification shape - ran the named classes\nskills: none\nconfidence: medium\nEND_WORKER_RESULT";
    private const string ValidResult = "WORKER_RESULT:\nfiles: none\ncommands: none\ntests: deferred - AlphaTests\ncommit: none\nblockers: none\nmodel_fit: OpenAI/gpt-6-luna - adequate - verification shape - named the classes\nskills: none\nconfidence: medium\nEND_WORKER_RESULT";
    private static readonly WorkerSandboxOptions Sandbox = new(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    [Fact]
    public void VerbatimConstrainedCatalog_PreparesLunaCommandAndPreflight()
    {
        using var f = new Fixture();
        Assert.True(f.Agents.Single().IsProviderRoutingConstrained);
        Assert.Equal(AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias, ConductorAutonomyPolicy.DefaultCascadeCheapModelAlias);
        f.Prepare();
        AssertRoute(f.Task, "cheap", "tester-cheap-first");
        Assert.Contains("--model 'gpt-6-luna'", f.Task.LastDispatch!.Command, StringComparison.Ordinal);
        Assert.Contains("model_reasoning_effort='medium'", f.Task.LastDispatch.Command, StringComparison.Ordinal);
        var preflightPath = Path.Combine(f.WorkingDirectory, ".orchestrator-context", f.Goal.Id.Value, "subscription-preflight.md");
        var preflight = File.ReadAllText(preflightPath);
        Assert.Contains("profile: codex-spark", preflight, StringComparison.Ordinal);
        Assert.Contains("model: OpenAI/gpt-6-luna", preflight, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshAndReadyBatch_PreserveCheapRoute()
    {
        using var refresh = new Fixture();
        refresh.Prepare();
        new GoalDispatchOperations().RefreshPreparedDispatchBeforeStart(refresh.Kernel, refresh.Workspace,
            refresh.Goal, refresh.Task, refresh.Agents, WorkerProfileCatalog.Default(), sandboxOptions: Sandbox);
        AssertRoute(refresh.Task, "cheap", "tester-cheap-first");

        using var batch = new Fixture();
        Assert.Single(new GoalDispatchOperations().SubscriptionDispatchReadyTasks(batch.Kernel, batch.Workspace,
            batch.Goal, batch.Agents, WorkerProfileCatalog.Default(), sandboxOptions: Sandbox));
        AssertRoute(batch.Task, "cheap", "tester-cheap-first");
    }

    [Theory]
    [InlineData("missing-tests")]
    [InlineData("invalid-findings")]
    [InlineData("rejected-note")]
    public void RejectedContract_EscalatesOnceThenStaysPrimary(string rejection)
    {
        using var f = new Fixture();
        f.Prepare();
        AssertRoute(f.Task, "cheap", "tester-cheap-first");
        if (rejection == "missing-tests") f.Verify(MissingTests, 1);
        if (rejection == "rejected-note") f.Kernel.RecordTaskNote(f.Goal.Id, f.Task.Id, "Rejected Tester structured finding result: invalid payload");
        f.Fail(rejection == "invalid-findings"
            ? "Tester WORKER_RESULT structured findings invalid: Finding 'alpha-receipt' changed identity."
            : "Tester round failed.");
        f.Retry();
        f.Prepare();
        AssertRoute(f.Task, "escalated", "tester-contract-rejected");
        // Refresh must not see this prepared escalation as a prior round.
        new GoalDispatchOperations().RefreshPreparedDispatchBeforeStart(f.Kernel, f.Workspace, f.Goal,
            f.Task, f.Agents, WorkerProfileCatalog.Default(), sandboxOptions: Sandbox);
        AssertRoute(f.Task, "escalated", "tester-contract-rejected");
        f.Fail("Recheck again.");
        f.Retry();
        f.Prepare();
        AssertRoute(f.Task, "primary", "prior-escalation");
    }

    [Theory]
    [InlineData("Recheck after the Developer rework.")]
    [InlineData("Tester WORKER_RESULT rejected: merged structured finding state still has open blocking")]
    [InlineData("Tester verification inconclusive; same Tester retry or operator escalation required.")]
    public void ValidContractAndNonContractFailures_StayCheap(string outcome)
    {
        using var f = new Fixture();
        f.Prepare();
        f.Verify(ValidResult);
        if (outcome.StartsWith("Recheck", StringComparison.Ordinal))
            f.Kernel.ReportTaskProgress(f.Goal.Id, f.Task.Id, WorkTaskStatus.Completed, "Complete cheap round.");
        else f.Fail(outcome);
        f.Retry("Recheck after the Developer rework.");
        f.Prepare();
        AssertRoute(f.Task, "cheap", "tester-cheap-first");
    }

    [Theory]
    [InlineData(FindingCategory.TestEvidence, "escalated")]
    [InlineData(FindingCategory.Correctness, "cheap")]
    public void ReviewerFinding_OnlyTestEvidenceEscalates(FindingCategory category, string decision)
    {
        using var f = new Fixture(withReviewer: true);
        f.Prepare();
        f.Verify(ValidResult);
        f.Kernel.ReportTaskProgress(f.Goal.Id, f.Task.Id, WorkTaskStatus.Completed, "Complete cheap round.");
        var finding = new ReviewFinding("alpha-receipt-missing", ReviewFindingState.Open,
            new ReviewFindingLocation("tests/Alpha.Tests/AlphaTests.cs", "AlphaTests"),
            "No executed receipt for AlphaTests on the candidate.", FindingSeverity.Blocking, category);
        var reviewer = f.Goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        f.Kernel.RecordTaskVerification(f.Goal.Id, reviewer.Id,
            new TaskVerificationRecord("review", f.Root, 1, "review finding", "", f.At.AddMinutes(2), MergedReviewFindings: [finding]));
        Assert.Equal(finding, Assert.Single(RetryContextFingerprintFactory.GetOpenBlockingFindings(f.Goal)));
        f.Retry();
        f.Prepare();
        AssertRoute(f.Task, decision, category == FindingCategory.TestEvidence ? "tester-evidence-finding" : "tester-cheap-first");
    }

    [Fact]
    public void ProviderModelRejection_UsesPositiveVerdictRule()
    {
        using var f = new Fixture();
        f.Prepare();
        f.Verify(ValidResult, 1, "provider-model-rejection");
        Assert.Equal("provider-model-rejection", f.Task.VerificationHistory.Last().CompletionVerdictRule);
        f.Fail("Provider rejected the model.");
        f.Retry();
        f.Prepare();
        AssertRoute(f.Task, "escalated", "cheap-model-rejected");
    }

    [Fact]
    public void OpenTestEvidenceBeforeFirstRound_UsesPrimary()
    {
        using var f = new Fixture(withReviewer: true);
        var reviewer = f.Goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        f.Kernel.RecordTaskVerification(f.Goal.Id, reviewer.Id, new TaskVerificationRecord("review", f.Root, 1,
            "review", "", f.At, MergedReviewFindings:
            [new("alpha", ReviewFindingState.Open, new("tests/Alpha.Tests/AlphaTests.cs", "AlphaTests"),
                "Missing evidence", FindingSeverity.Blocking, FindingCategory.TestEvidence)]));
        Assert.Single(RetryContextFingerprintFactory.GetOpenBlockingFindings(f.Goal));
        f.Prepare();
        AssertRoute(f.Task, "primary", "tester-evidence-finding");
    }

    [Theory]
    [InlineData(false, "gpt-6-luna")]
    [InlineData(true, "gpt-6-sol")]
    public void PolicyFile_ControlsSingleTaskBatchAndRefresh(bool enabled, string alias)
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.Workspace.OrchestratorDirectory);
        var policy = enabled ? (ConductorAutonomyPolicy.ParseJson(OffJson) with
            { CascadeTesterCheapFirst = true, CascadeCheapModelAlias = alias }).ToJson() : OffJson;
        File.WriteAllText(Path.Combine(f.Workspace.OrchestratorDirectory, "conductor-policy.json"), policy);
        var operations = new GoalDispatchOperations();
        operations.SubscriptionDispatchTask(f.Kernel, f.Workspace, f.Goal, f.Task, f.Agents, WorkerProfileCatalog.Default());
        AssertRoute(f.Task, enabled ? "cheap" : "primary", enabled ? "tester-cheap-first" : "switched-off", alias);
        Assert.StartsWith("provider-constrained: Tester remains on OpenAI", f.Task.LastDispatch!.ModelSelectionReason);
        operations.RefreshPreparedDispatchBeforeStart(f.Kernel, f.Workspace, f.Goal, f.Task, f.Agents,
            WorkerProfileCatalog.Default(), sandboxOptions: Sandbox);
        AssertRoute(f.Task, enabled ? "cheap" : "primary", enabled ? "tester-cheap-first" : "switched-off", alias);
        f.Fail("Prepare batch next.");
        f.Retry();
        Assert.Single(operations.SubscriptionDispatchReadyTasks(f.Kernel, f.Workspace, f.Goal, f.Agents,
            WorkerProfileCatalog.Default(), sandboxOptions: Sandbox));
        AssertRoute(f.Task, enabled ? "cheap" : "primary", enabled ? "tester-cheap-first" : "switched-off", alias);
    }

    [Fact]
    public void ExplicitSettingsOverridePassedPolicyWhichOverridesFile()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.Workspace.OrchestratorDirectory);
        File.WriteAllText(Path.Combine(f.Workspace.OrchestratorDirectory, "conductor-policy.json"), OffJson);
        var on = ConductorAutonomyPolicy.ParseJson(OffJson) with { CascadeTesterCheapFirst = true, CascadeCheapModelAlias = "gpt-6-sol" };
        var operations = new GoalDispatchOperations();
        operations.SubscriptionDispatchTask(f.Kernel, f.Workspace, f.Goal, f.Task, f.Agents,
            WorkerProfileCatalog.Default(), conductorPolicy: on);
        AssertRoute(f.Task, "cheap", "tester-cheap-first", "gpt-6-sol");
        operations.RefreshPreparedDispatchBeforeStart(f.Kernel, f.Workspace, f.Goal, f.Task, f.Agents,
            WorkerProfileCatalog.Default(), sandboxOptions: Sandbox, conductorPolicy: on,
            cascadeTesterCheapFirst: false, cascadeCheapModelAlias: "gpt-6-luna");
        AssertRoute(f.Task, "primary", "switched-off");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("echo")]
    [InlineData("no-model")]
    [InlineData("no-effort")]
    public void UnavailableCheapProfile_FallsBackToPrimary(string defect)
    {
        using var f = new Fixture();
        var profiles = WorkerProfileCatalog.Default().Profiles.Where(p => p.Name != "codex-spark").ToList();
        if (defect != "missing")
        {
            var spark = WorkerProfileCatalog.Default().GetRequired("codex-spark");
            profiles.Add(spark with { CommandTemplate = defect switch
            {
                "echo" => "Write-Output {promptPath}",
                "no-model" => spark.CommandTemplate.Replace("{subscriptionModelName}", "gpt-6-luna"),
                _ => spark.CommandTemplate.Replace("{subscriptionReasoningEffort}", "medium")
            } });
        }
        f.Prepare(new WorkerProfileCatalog(profiles));
        AssertRoute(f.Task, "primary", "cheap-profile-unavailable");
    }

    [Fact]
    public void OtherRolesAndUnconstrainedMechanicalTester_KeepExistingRoutes()
    {
        using var f = new Fixture();
        foreach (var role in new[] { AgentRole.Developer, AgentRole.Reviewer, AgentRole.Tester })
        {
            var agent = AgentCatalog.Default().GetRequired(role) with { IsProviderRoutingConstrained = role != AgentRole.Tester };
            var task = new TaskSpec(TaskId.New(), "Inspect focused verification.", role);
            var goal = f.Kernel.CreateGoal("Focused worker check.", [task]);
            f.Kernel.ActivateGoal(goal.Id, [agent]);
            if (role == AgentRole.Tester)
                f.Kernel.RetryTask(goal.Id, task.Id, "Mechanical recheck.", RetryCause.NewSourceFinding, retryRoundKind: RetryRoundKind.Mechanical);
            var workingDirectory = GoalWorktrees.Ensure(f.Root, goal.Id);
            WorkerProfileDispatcher.PrepareSubscriptionTask(f.Kernel, goal, task, [agent], WorkerProfileCatalog.Default(),
                f.Workspace.PromptDirectory, workingDirectory, f.At, sandboxOptions: Sandbox, commandExists: _ => true);
            var dispatch = task.LastDispatch!;
            Assert.Equal(role == AgentRole.Tester ? "codex-spark" : agent.Subscription!.WorkerProfileName, dispatch.WorkerName);
            Assert.Equal(role == AgentRole.Tester ? WorkerProfileDispatcher.OpenAiSparkSubscriptionModelName : agent.Subscription!.ModelAlias, dispatch.ModelName);
            Assert.Equal(dispatch.WorkerName, dispatch.DispatchLane);
            Assert.StartsWith(role == AgentRole.Tester ? "cheap-lane: Tester mechanical-retry uses codex-spark/" :
                $"provider-constrained: {role} remains on {agent.Model.ProviderName}", dispatch.ModelSelectionReason);
            if (role != AgentRole.Developer) Assert.DoesNotContain("cascade=", dispatch.ModelSelectionReason!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Scorecard_GroupsRealCheapAndEscalatedPreparationsByLane()
    {
        using var cheap = new Fixture();
        using var escalated = new Fixture();
        cheap.Prepare();
        cheap.Verify(ValidResult);
        cheap.Kernel.ReportTaskProgress(cheap.Goal.Id, cheap.Task.Id, WorkTaskStatus.Completed, "done");
        escalated.Prepare();
        escalated.Verify(MissingTests, 1);
        escalated.Fail("Missing test contract.");
        escalated.Retry();
        escalated.Prepare();
        escalated.Verify(ValidResult);
        escalated.Kernel.ReportTaskProgress(escalated.Goal.Id, escalated.Task.Id, WorkTaskStatus.Completed, "done");
        var records = ModelOutcomeScorecard.Build([cheap.Goal, escalated.Goal]);
        Assert.Contains(records, r => r.ModelName == "gpt-6-luna" && r.DispatchLane == "codex-spark:cascade-cheap");
        Assert.Contains(records, r => r.ModelName == "gpt-6.1-sol" && r.DispatchLane == "codex-cli:cascade-escalated");
    }

    private static void AssertRoute(TaskSpec task, string decision, string rule, string alias = "gpt-6-luna")
    {
        var dispatch = task.LastDispatch!;
        Assert.NotNull(dispatch);
        Assert.Equal(decision == "cheap" ? "codex-spark" : "codex-cli", dispatch.WorkerName);
        Assert.Equal(decision == "cheap" ? alias : "gpt-6.1-sol", dispatch.ModelName);
        Assert.Equal(decision switch { "cheap" => "codex-spark:cascade-cheap", "escalated" => "codex-cli:cascade-escalated", _ => "codex-cli" }, dispatch.DispatchLane);
        Assert.Contains($"cascade={decision} rule={rule}", dispatch.ModelSelectionReason!, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = CreateSeededDispatchRepository();
        internal string WorkingDirectory { get; }
        internal OrchestratorWorkspace Workspace { get; }
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal TaskSpec Task { get; } = new(TaskId.New(), "Verify focused classes.", AgentRole.Tester);
        internal Goal Goal { get; }
        internal IReadOnlyList<AgentDefinition> Agents { get; }
        internal DateTimeOffset At { get; } = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
        private int _round;

        internal Fixture(bool withReviewer = false)
        {
            // App dispatch requires a migrated store and an already-refined goal;
            // routing preparation must also exercise a real linked goal worktree.
            Workspace = InfrastructureTestSupport.CreateRefinedWorkspace(Root);
            var catalogPath = Path.Combine(Root, "catalog.json");
            File.WriteAllText(catalogPath, CatalogJson);
            // The Tester always comes from the verbatim catalog; Reviewer is appended only for finding rows.
            Agents = withReviewer ? [.. AgentCatalogStore.Load(catalogPath).Agents, AgentCatalog.Default().GetRequired(AgentRole.Reviewer)]
                : AgentCatalogStore.Load(catalogPath).Agents;
            Goal = Kernel.CreateGoal("Focused verification.", withReviewer ? [Task, new(TaskId.New(), "Review evidence.", AgentRole.Reviewer)] : [Task]);
            InfrastructureTestSupport.MarkGoalRefined(Kernel, Goal);
            Kernel.ActivateGoal(Goal.Id, Agents);
            WorkingDirectory = GoalWorktrees.Ensure(Root, Goal.Id);
        }

        internal void Prepare(WorkerProfileCatalog? profiles = null) => WorkerProfileDispatcher.PrepareSubscriptionTask(
            Kernel, Goal, Task, Agents, profiles ?? WorkerProfileCatalog.Default(), Workspace.PromptDirectory, WorkingDirectory,
            At.AddMinutes(_round++ * 10), sandboxOptions: Sandbox, commandExists: _ => true);
        internal void Verify(string output, int exit = 0, string? rule = null) => Kernel.RecordTaskVerification(Goal.Id, Task.Id,
            new TaskVerificationRecord("verify", Root, exit, output, "", At.AddMinutes(_round * 10 - 5), CompletionVerdictRule: rule));
        internal void Fail(string message) => Kernel.ReportTaskProgress(Goal.Id, Task.Id, WorkTaskStatus.Failed, message);
        internal void Retry(string message = "Recheck after the Developer rework.") => Kernel.RetryTask(Goal.Id, Task.Id, message);
        public void Dispose() => GoalWorktrees.DeleteDirectoryWithRetry(Root);
    }
}
