using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: isolated temporary repositories, injected launcher probe, no worker launch or environment mutation.
public sealed class DeveloperCascadeDispatchTests : WorkerDispatchTestSupport
{
    private const string CatalogJson = """
        {"Agents":[{"Id":{"Value":"openai-developer"},"Name":"Codex GPT-6.1 Sol developer","Role":"Developer","Model":{"ProviderName":"OpenAI","ModelName":"gpt-6.1-sol","Capabilities":"Text, Code, ToolUse","SubscriptionMode":"ApiKey","ReasoningEffort":"medium","MaxOutputTokens":2048},"Status":"Available","ExecutionPolicy":"PreferSubscription","Subscription":{"WorkerProfileName":"codex-cli","ModelAlias":"gpt-6.1-sol","ReasoningEffort":"high"},"ComplexModel":{"ProviderName":"OpenAI","ModelName":"gpt-6.1-sol","Capabilities":"Text, Code, ToolUse","SubscriptionMode":"ApiKey","ReasoningEffort":"high","MaxOutputTokens":4096},"ReasoningEffortPolicy":null,"IsProviderRoutingConstrained":true}]}
        """;
    private const string OffJson = """
        {"name":"PermissiveCap5Rollback","maxConcurrentPaidWorkers":4,"acceptanceWidth":1,"maxTotalBudget":20.00,"maxCriterionRetries":2,"plannerSampleCount":1,"perProviderBudgetCaps":null,"autoPromoteRiskThreshold":"Broad","cascadeMechanicalReworkCheap":false,"transitionMap":{"Created":"Auto","AwaitingClarification":"Auto","WorkspaceReady":"Auto","Dispatched":"Auto","Running":"Auto","AwaitingVerification":"Auto","Verifying":"Auto","Verified":"Auto","AcceptanceFailed":"Auto","Merged":"Auto","Recorded":"Auto","CleanedUp":"Auto","Failed":"Auto","Blocked":"Auto","AwaitingHumanInput":"Auto"}}
        """;
    private const string Prefix = "developer-completion structural pre-check failed: ";
    private const string FirstPath = "src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs";
    private const string SecondPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsGoalIntakeAndReplacement.cs";
    private const string OtherPath = "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs";
    private const string FirstLine = FirstPath + " has 4931 lines, exceeding the recorded ceiling of 4863. Extract behavior to a collaborator and lower the ceiling for this entry, or raise the recorded ceiling for this entry deliberately with justification in the same change by editing SourceSizeRatchet.SeededCeilings. See docs/god-class-decomposition-plan.md.";
    private const string SecondLine = SecondPath + " has 2825 lines, exceeding the recorded ceiling of 2780. Extract behavior to a collaborator and lower the ceiling for this entry, or raise the recorded ceiling for this entry deliberately with justification in the same change by editing SourceSizeRatchet.SeededCeilings. See docs/god-class-decomposition-plan.md.";
    private const string OtherLine = OtherPath + " has 5313 lines, exceeding the recorded ceiling of 5310. Extract behavior to a collaborator and lower the ceiling for this entry, or raise the recorded ceiling for this entry deliberately with justification in the same change by editing SourceSizeRatchet.SeededCeilings. See docs/god-class-decomposition-plan.md.";
    private const string RatchetRetry = Prefix + FirstLine + "\n" + SecondLine;
    private const string ReviewRetry = "auto-review-retry round 1 convergence brief: Reviewer task 0a1b2c3d blocking findings open; retry upstream Developer task.";
    private static readonly WorkerSandboxOptions Sandbox = new(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    [Fact]
    public void VerbatimCatalog_FirstRoundPrimaryThenStructuralRetryCheap()
    {
        using var f = new Fixture();
        Assert.True(Assert.Single(f.Agents).IsProviderRoutingConstrained);
        f.Prepare();
        AssertRoute(f.Task, "primary", "first-round");
        Assert.Contains("provider-constrained: Developer remains on OpenAI", f.Task.LastDispatch!.ModelSelectionReason);
        f.Retry(RatchetRetry);
        f.Prepare();
        AssertRoute(f.Task, "cheap", "structural-ratchet", FirstPath + "," + SecondPath);
        Assert.Contains("--model 'gpt-6-luna'", f.Task.LastDispatch!.Command);
    }

    [Fact]
    public void PersistedPath_EscalatesOnceThenStaysPrimary()
    {
        using var f = new Fixture();
        f.PrepareCheap();
        f.Retry(Prefix + FirstLine);
        f.Prepare();
        AssertRoute(f.Task, "escalated", "mechanical-finding-persisted");
        f.Retry(RatchetRetry);
        f.Prepare();
        AssertRoute(f.Task, "primary", "prior-escalation");
    }

    [Fact]
    public void DisjointPaths_StayCheap()
    {
        using var f = new Fixture();
        f.PrepareCheap();
        f.Retry(Prefix + OtherLine);
        f.Prepare();
        AssertRoute(f.Task, "cheap", "structural-ratchet", OtherPath);
    }

    [Theory]
    [InlineData("ratchet", "cheap")]
    [InlineData("mixed", "primary")]
    [InlineData("test-evidence", "primary")]
    public void ReviewerFindings_RequireEveryFindingToMatch(string row, string decision)
    {
        using var f = new Fixture(withReviewer: true);
        f.Prepare();
        f.Complete();
        var finding = new ReviewFinding("dispatcher-over-ceiling", ReviewFindingState.Open,
            new ReviewFindingLocation("src/Mcg.AgentOrchestrator.Execution/Workers/WorkerProfileDispatcher.cs", "file"),
            "WorkerProfileDispatcher.cs has 2611 lines, exceeding the recorded ceiling of 2607 in SourceSizeRatchet.cs.",
            FindingSeverity.Blocking, row == "test-evidence" ? FindingCategory.TestEvidence : FindingCategory.SpecCompliance);
        var findings = new List<ReviewFinding> { finding };
        if (row == "mixed") findings.Add(new("retry-drops-cause", ReviewFindingState.Open,
            new("src/Alpha/Retry.cs", "Retry"), "The retry path drops the recorded cause.", FindingSeverity.Blocking, FindingCategory.Correctness));
        var reviewer = f.Goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        f.Kernel.RecordTaskVerification(f.Goal.Id, reviewer.Id,
            new TaskVerificationRecord("review", f.Root, 1, "findings", "", f.At.AddMinutes(2), MergedReviewFindings: findings));
        Assert.Equal(findings.Count, RetryContextFingerprintFactory.GetOpenBlockingFindings(f.Goal).Count);
        f.Retry(ReviewRetry);
        f.Prepare();
        AssertRoute(f.Task, decision, decision == "cheap" ? "review-ratchet" : "non-mechanical",
            decision == "cheap" ? "dispatcher-over-ceiling" : null);
    }

    [Theory]
    [InlineData("Acceptance criteria unmet; retrying task with feedback: AlphaTests failed.", "primary", "non-mechanical", null)]
    [InlineData("pre-review build repair: candidate 0123abcd; diagnostic: CS1002; evidence pointer: none", "primary", "non-mechanical", null)]
    [InlineData("pre-review focused-test repair: candidate 0123abcd; exact failing tests: AlphaTests.Works; evidence pointer: none", "primary", "non-mechanical", null)]
    [InlineData("Recheck every call site.", "primary", "non-mechanical", null)]
    [InlineData(Prefix + "config/acceptance-manifest.json: Acceptance manifest collection 'Alpha' has multiple owners.", "cheap", "structural-manifest", "config/acceptance-manifest.json")]
    public void RetryText_RecognizesOnlyMechanicalPrefixes(string message, string decision, string rule, string? ids)
    {
        using var f = new Fixture();
        f.Prepare();
        f.Complete();
        f.Retry(message);
        f.Prepare();
        AssertRoute(f.Task, decision, rule, ids);
    }

    [Fact]
    public void PolicyFile_DeveloperOffLeavesTesterCheap()
    {
        using var f = new Fixture();
        f.Prepare();
        f.Retry(RatchetRetry);
        Directory.CreateDirectory(f.Workspace.OrchestratorDirectory);
        File.WriteAllText(Path.Combine(f.Workspace.OrchestratorDirectory, "conductor-policy.json"), OffJson);
        var operations = new GoalDispatchOperations();
        operations.SubscriptionDispatchTask(f.Kernel, f.Workspace, f.Goal, f.Task, f.Agents, WorkerProfileCatalog.Default());
        AssertRoute(f.Task, "primary", "switched-off");

        var tester = AgentCatalog.Default().GetRequired(AgentRole.Tester) with
        {
            Model = f.Agents.Single().Model, ComplexModel = f.Agents.Single().ComplexModel,
            Subscription = f.Agents.Single().Subscription, IsProviderRoutingConstrained = true
        };
        var task = new TaskSpec(TaskId.New(), "Verify focused classes.", AgentRole.Tester);
        var goal = f.Kernel.CreateGoal("Focused verification.", [task]);
        InfrastructureTestSupport.MarkGoalRefined(f.Kernel, goal);
        f.Kernel.ActivateGoal(goal.Id, [tester]);
        GoalWorktrees.Ensure(f.Root, goal.Id);
        operations.SubscriptionDispatchTask(f.Kernel, f.Workspace, goal, task, [tester], WorkerProfileCatalog.Default());
        AssertRoute(task, "cheap", "tester-cheap-first");
    }

    [Fact]
    public void Refresh_PreservesFullPreparedMarker()
    {
        using var f = new Fixture();
        f.PrepareCheap();
        var marker = f.Task.LastDispatch!.ModelSelectionReason;
        new GoalDispatchOperations().RefreshPreparedDispatchBeforeStart(f.Kernel, f.Workspace, f.Goal, f.Task,
            f.Agents, WorkerProfileCatalog.Default(), sandboxOptions: Sandbox);
        AssertRoute(f.Task, "cheap", "structural-ratchet", FirstPath + "," + SecondPath);
        Assert.Equal(marker, f.Task.LastDispatch!.ModelSelectionReason);
    }

    [Theory]
    [InlineData(true, "escalated", "cheap-model-rejected")]
    [InlineData(false, "cheap", "structural-ratchet")]
    public void ProviderRejection_RequiresPositiveVerdictRule(bool rejected, string decision, string rule)
    {
        using var f = new Fixture();
        f.PrepareCheap();
        f.Kernel.RecordTaskVerification(f.Goal.Id, f.Task.Id,
            new TaskVerificationRecord("verify", f.Root, 1, "Provider rejected model", "", f.At.AddMinutes(15),
                CompletionVerdictRule: rejected ? "provider-model-rejection" : null));
        f.Retry(Prefix + OtherLine);
        f.Prepare();
        AssertRoute(f.Task, decision, rule, rejected ? null : OtherPath);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("echo")]
    [InlineData("no-model")]
    [InlineData("no-effort")]
    public void UnavailableCheapProfile_FallsBackToPrimary(string defect)
    {
        using var f = new Fixture();
        f.Prepare();
        f.Retry(RatchetRetry);
        var profiles = WorkerProfileCatalog.Default().Profiles.Where(p => p.Name != "codex-luna").ToList();
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
    public void NonMechanicalRetry_PrecedesProviderRejectionEscalation()
    {
        using var f = new Fixture();
        f.PrepareCheap();
        f.Kernel.RecordTaskVerification(f.Goal.Id, f.Task.Id,
            new TaskVerificationRecord("verify", f.Root, 1, "rejection", "", f.At.AddMinutes(15),
                CompletionVerdictRule: "provider-model-rejection"));
        f.Retry("pre-review focused-test repair: candidate 0123abcd; exact failing tests: AlphaTests.Works; evidence pointer: none");
        f.Prepare();
        AssertRoute(f.Task, "primary", "non-mechanical");
        // The most recent dispatch is primary; a historical cheap round is not the current cheap round.
        f.Retry(Prefix + FirstLine);
        f.Prepare();
        AssertRoute(f.Task, "cheap", "structural-ratchet", FirstPath);
    }

    [Fact]
    public void HistoricalRejection_WithoutLatestCheapVerificationDoesNotEscalate()
    {
        using var f = new Fixture();
        f.Prepare();
        f.Kernel.RecordTaskVerification(f.Goal.Id, f.Task.Id,
            new TaskVerificationRecord("verify", f.Root, 1, "rejection", "", f.At.AddMinutes(5),
                CompletionVerdictRule: "provider-model-rejection"));
        f.Retry(RatchetRetry);
        f.Prepare();
        AssertRoute(f.Task, "cheap", "structural-ratchet", FirstPath + "," + SecondPath);
        f.Retry(Prefix + OtherLine);
        f.Prepare();
        AssertRoute(f.Task, "cheap", "structural-ratchet", OtherPath);
    }

    [Fact]
    public void ExplicitSwitch_OverridesPassedPolicyAndFileDuringBatchAndRefresh()
    {
        using var f = new Fixture();
        f.Prepare();
        f.Retry(RatchetRetry);
        Directory.CreateDirectory(f.Workspace.OrchestratorDirectory);
        File.WriteAllText(Path.Combine(f.Workspace.OrchestratorDirectory, "conductor-policy.json"), OffJson);
        var operations = new GoalDispatchOperations();
        var on = ConductorAutonomyPolicy.ParseJson(OffJson) with { CascadeMechanicalReworkCheap = true };
        Assert.Single(operations.SubscriptionDispatchReadyTasks(f.Kernel, f.Workspace, f.Goal, f.Agents,
            WorkerProfileCatalog.Default(), sandboxOptions: Sandbox, conductorPolicy: on));
        AssertRoute(f.Task, "cheap", "structural-ratchet", FirstPath + "," + SecondPath);
        operations.RefreshPreparedDispatchBeforeStart(f.Kernel, f.Workspace, f.Goal, f.Task, f.Agents,
            WorkerProfileCatalog.Default(), sandboxOptions: Sandbox, conductorPolicy: on, cascadeMechanicalReworkCheap: false);
        AssertRoute(f.Task, "primary", "switched-off");
    }

    private static void AssertRoute(TaskSpec task, string decision, string rule, string? ids = null)
    {
        var dispatch = task.LastDispatch!;
        Assert.NotNull(dispatch);
        Assert.Equal(decision == "cheap" ? "codex-luna" : "codex-cli", dispatch.WorkerName);
        Assert.Equal(decision == "cheap" ? "gpt-6-luna" : "gpt-6.1-sol", dispatch.ModelName);
        Assert.Equal(decision switch { "cheap" => "codex-luna:cascade-cheap", "escalated" => "codex-cli:cascade-escalated", _ => "codex-cli" }, dispatch.DispatchLane);
        Assert.EndsWith($"cascade={decision} rule={rule}" + (ids is null ? "" : " ids=" + ids), dispatch.ModelSelectionReason);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = CreateSeededDispatchRepository();
        internal string WorkingDirectory { get; }
        internal OrchestratorWorkspace Workspace { get; }
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal TaskSpec Task { get; } = new(TaskId.New(), "Implement focused routing.", AgentRole.Developer);
        internal Goal Goal { get; }
        internal IReadOnlyList<AgentDefinition> Agents { get; }
        internal DateTimeOffset At { get; } = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
        private int _round;

        internal Fixture(bool withReviewer = false)
        {
            Workspace = InfrastructureTestSupport.CreateRefinedWorkspace(Root);
            var path = Path.Combine(Root, "catalog.json");
            File.WriteAllText(path, CatalogJson);
            Agents = withReviewer ? [.. AgentCatalogStore.Load(path).Agents, AgentCatalog.Default().GetRequired(AgentRole.Reviewer)]
                : AgentCatalogStore.Load(path).Agents;
            Goal = Kernel.CreateGoal("Focused implementation.", withReviewer ? [Task, new(TaskId.New(), "Review changes.", AgentRole.Reviewer)] : [Task]);
            InfrastructureTestSupport.MarkGoalRefined(Kernel, Goal);
            Kernel.ActivateGoal(Goal.Id, Agents);
            WorkingDirectory = GoalWorktrees.Ensure(Root, Goal.Id);
        }

        internal void Prepare(WorkerProfileCatalog? profiles = null) => WorkerProfileDispatcher.PrepareSubscriptionTask(
            Kernel, Goal, Task, Agents, profiles ?? WorkerProfileCatalog.Default(), Workspace.PromptDirectory, WorkingDirectory,
            At.AddMinutes(_round++ * 10), sandboxOptions: Sandbox, commandExists: _ => true,
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli);
        internal void PrepareCheap() { Prepare(); Retry(RatchetRetry); Prepare(); }
        internal void Complete()
        {
            Kernel.RecordTaskVerification(Goal.Id, Task.Id, new("verify", Root, 0, "done", "", At.AddMinutes(_round * 10 - 5)));
            Kernel.ReportTaskProgress(Goal.Id, Task.Id, WorkTaskStatus.Completed, "Done.");
        }
        internal void Retry(string message)
        {
            if (Task.Status == WorkTaskStatus.Running)
                Kernel.ReportTaskProgress(Goal.Id, Task.Id, WorkTaskStatus.Failed, "Round requires rework.");
            Kernel.RetryTask(Goal.Id, Task.Id, message);
        }
        public void Dispose() => GoalWorktrees.DeleteDirectoryWithRetry(Root);
    }
}
