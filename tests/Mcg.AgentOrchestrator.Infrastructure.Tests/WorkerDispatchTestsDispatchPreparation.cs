using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

public sealed class WorkerDispatchTestsDispatchPreparation : WorkerDispatchTestSupport
{
    private static readonly WorkerSandboxOptions DisabledSandbox = new(
        Enabled: false,
        WorkerSandboxOptions.DefaultAccount,
        WorkerSandboxOptions.DefaultCredentialTarget);

    [Xunit.Fact]
    public async Task GoalReplacement_PreflightAndTransfer_DoNotStartPaidWorker()
    {
        var root = CreateSeededDispatchRepository();
        var workspace = InfrastructureTestSupport.CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("replacement-preflight-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Replacement preflight source");
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Cancelled zero-work predecessor");
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.CancelGoal(predecessor.Id, "No worker was dispatched.");
        await repository.SaveAsync(initial);
        var briefPath = Path.Combine(root, "replacement-preflight-brief.md");
        var reasonPath = Path.Combine(root, "replacement-preflight-reason.md");
        await File.WriteAllTextAsync(briefPath, "Create a corrected FiveRole successor.");
        await File.WriteAllTextAsync(reasonPath, "Correct the malformed zero-work goal.");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var refiner = new ReplacementPreflightRefinerProvider();
        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            [
                "goal-replace", predecessor.Id.Value, "--brief-file", briefPath,
                "--reason-file", reasonPath, "--request-id", Guid.NewGuid().ToString(),
                "--disposition", "zero-work-correction", "--confirm-goal-replace",
                "--pipeline", "five-role"
            ],
            repository,
            workspace,
            ref agents,
            new InMemoryModelProviderRegistry([refiner]),
            ref profiles,
            ref currentGoal));

        var restored = await repository.LoadAsync();
        var successor = Xunit.Assert.Single(restored.Goals, goal => goal.Id != predecessor.Id);
        Xunit.Assert.Equal(0, refiner.InvocationCount);
        Xunit.Assert.Contains("GOAL_REPLACE_SUCCEEDED", output, StringComparison.Ordinal);
        var claim = new SourceBacklogClaimStore(workspace.SqliteStatePath).ResolveClaim(restored, item.Id);
        Xunit.Assert.Equal(successor.Id.Value, claim!.OwnerGoalId);
        Xunit.Assert.Equal(
            [AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            successor.Tasks.Select(task => task.RequiredRole));
        Xunit.Assert.Null(successor.RefinedSpec);
        Xunit.Assert.Contains(successor.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith("spec_refinement outcome=pending", StringComparison.Ordinal));
        var refinementMessage = Xunit.Assert.Single(
            await repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind));
        Xunit.Assert.Equal(GoalRefinementWorkCoordinator.MessageId(successor.Id), refinementMessage.Id);
        Xunit.Assert.All(successor.Tasks, task =>
        {
            Xunit.Assert.Null(task.LastDispatch);
            Xunit.Assert.Null(task.LastProcess);
        });
        Xunit.Assert.False(Directory.Exists(GoalWorktrees.WorktreePath(workspace.ExecutionDirectory, successor.Id)));
        Xunit.Assert.Empty(Directory.Exists(workspace.LogDirectory)
            ? Directory.EnumerateFiles(workspace.LogDirectory, "*.dispatch.json", SearchOption.TopDirectoryOnly)
            : []);
    }

    [Xunit.Fact]
    public async Task GoalReplacement_ProtectedOwner_DoesNotStartPaidWorker()
    {
        var root = CreateSeededDispatchRepository();
        var workspace = InfrastructureTestSupport.CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("replacement-preflight-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Protected replacement source");
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal(
            "Protected predecessor",
            [new TaskSpec(TaskId.New(), "Keep the active owner protected.", AgentRole.Developer)]);
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.ActivateGoal(predecessor.Id, AgentCatalog.Default().Agents);
        await repository.SaveAsync(initial);
        var briefPath = Path.Combine(root, "replacement-rejection-brief.md");
        var reasonPath = Path.Combine(root, "replacement-rejection-reason.md");
        await File.WriteAllTextAsync(briefPath, "Prepare a successor that must remain blocked.");
        await File.WriteAllTextAsync(reasonPath, "Confirm protected owners cannot be replaced.");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var refiner = new ReplacementPreflightRefinerProvider();

        var exception = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
            CliPersistentStateRunner.ExecuteCommand(
                [
                    "goal-replace", predecessor.Id.Value, "--brief-file", briefPath,
                    "--reason-file", reasonPath, "--request-id", Guid.NewGuid().ToString(),
                    "--disposition", "zero-work-correction", "--confirm-goal-replace",
                    "--pipeline", "five-role"
                ],
                repository,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([refiner]),
                ref profiles,
                ref currentGoal)));

        var restored = await repository.LoadAsync();
        Xunit.Assert.Contains("GOAL_REPLACE_PROTECTED_OWNER", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(0, refiner.InvocationCount);
        var restoredPredecessor = Xunit.Assert.Single(restored.Goals);
        Xunit.Assert.Equal(predecessor.Id, restoredPredecessor.Id);
        var restoredTask = Xunit.Assert.Single(restoredPredecessor.Tasks);
        Xunit.Assert.Null(restoredTask.LastDispatch);
        Xunit.Assert.Null(restoredTask.LastProcess);
        Xunit.Assert.False(Directory.Exists(GoalWorktrees.WorktreePath(workspace.ExecutionDirectory, predecessor.Id)));
        Xunit.Assert.Empty(Directory.Exists(workspace.LogDirectory)
            ? Directory.EnumerateFiles(workspace.LogDirectory, "*.dispatch.json", SearchOption.TopDirectoryOnly)
            : []);
    }

    [Xunit.Fact(DisplayName = "Worker_preflight_terminal_sweep_cleans_cancelled_worktree_without_starting_paid_process")]
    public void WorkerPreflightTerminalSweepCleansCancelledWorktreeWithoutStartingPaidProcess()
    {
        var root = CreateSeededDispatchRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do not dispatch after cancellation.", AgentRole.Developer);
            var goal = kernel.CreateGoal("Cancelled preflight cleanup", [task]);
            var agent = TestSubscriptionAgent("developer", "Developer", AgentRole.Developer);
            kernel.ActivateGoal(goal.Id, [agent]);
            _ = GoalWorktrees.Ensure(root, goal.Id);
            kernel.CancelGoal(goal.Id, "Cancelled before paid dispatch.");

            var sweep = TerminalGoalSweep.Run(kernel, root, goal.Id);
            var readiness = GoalReadinessPreflight.Build(
                kernel.GetGoal(goal.Id),
                [agent],
                root,
                WorkerProfileCatalog.Default(),
                GoalWorktrees.TryResolve);

            Assert.Contains(sweep.Goals.Single().Repairs, repair => repair.Kind == "terminal-worktree-cleanup");
            Assert.Null(GoalWorktrees.TryResolve(root, goal.Id));
            Assert.True(readiness.HasHardBlockers);
            Assert.All(kernel.GetGoal(goal.Id).Tasks, currentTask => Assert.Null(currentTask.LastProcess));
        }
        finally
        {
            _ = GoalWorktrees.DeleteDirectory(root);
        }
    }

    private sealed class ReplacementPreflightRefinerProvider : IModelProvider
    {
        public string ProviderName => "replacement-preflight-refiner";

        public int InvocationCount { get; private set; }

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            InvocationCount++;
            const string response = """
                ```json
                {
                  "behavioralContract": "Create a corrected FiveRole successor without dispatching it.",
                  "acceptanceCriteria": ["The successor remains prepared for ordinary dispatch."],
                  "verificationClass": "TestVerifiable",
                  "decisions": [],
                  "forks": []
                }
                ```
                """;
            return Task.FromResult(new ModelResponse(response, new ModelUsage(1, 1), "stop"));
        }
    }

    [Xunit.Fact(DisplayName = "Reviewer_round_diff_resolves_only_touched_structural_anchor")]
    public void ReviewerRoundDiffResolvesOnlyTouchedStructuralAnchor()
    {
        var root = CreateSeededDispatchRepository();
        try
        {
            var sourcePath = Path.Combine(root, "src", "Example.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(
                sourcePath,
                """
                public sealed class A
                {
                    public void Run()
                    {
                        var guard = false;
                    }
                }

                public sealed class B
                {
                    public void Run()
                    {
                        var guard = false;
                    }
                }
                """);
            RunGit(root, ["add", "-A"], DateTimeOffset.Parse("2026-01-01T00:01:00Z"));
            RunGit(root, ["commit", "-m", "Add example"], DateTimeOffset.Parse("2026-01-01T00:01:00Z"));
            var previous = ReadGit(root, ["rev-parse", "HEAD"]).Trim();

            File.WriteAllText(
                sourcePath,
                """
                public sealed class A
                {
                    public void Run()
                    {
                        var guard = false;
                    }
                }

                public sealed class B
                {
                    public void Run()
                    {
                        var guard = true;
                    }
                }
                """);
            RunGit(root, ["add", "-A"], DateTimeOffset.Parse("2026-01-01T00:02:00Z"));
            RunGit(root, ["commit", "-m", "Fix B"], DateTimeOffset.Parse("2026-01-01T00:02:00Z"));
            var current = ReadGit(root, ["rev-parse", "HEAD"]).Trim();
            var anchorA = new ReviewFindingLocation("src/Example.cs", "A.Run", "guard");
            var anchorB = new ReviewFindingLocation("src/Example.cs", "B.Run", "guard");

            var touched = new WorkerGitContext().ReadReviewerRoundTouchedAnchors(
                root,
                previous,
                current,
                [anchorA, anchorB]);

            Assert.Equal(anchorB, Assert.Single(touched));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "Reviewer_round_diff_does_not_touch_unrelated_hunk_in_same_region")]
    public void ReviewerRoundDiffDoesNotTouchUnrelatedHunkInSameRegion()
    {
        var root = CreateSeededDispatchRepository();
        try
        {
            var sourcePath = Path.Combine(root, "src", "Example.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(
                sourcePath,
                """
                public sealed class Example
                {
                    public void Run()
                    {
                        var acceptedGuard = false;
                        var residualGuard = false;
                    }
                }
                """);
            RunGit(root, ["add", "-A"], DateTimeOffset.Parse("2026-01-01T00:01:00Z"));
            RunGit(root, ["commit", "-m", "Add example"], DateTimeOffset.Parse("2026-01-01T00:01:00Z"));
            var previous = ReadGit(root, ["rev-parse", "HEAD"]).Trim();

            File.WriteAllText(
                sourcePath,
                """
                public sealed class Example
                {
                    public void Run()
                    {
                        var acceptedGuard = false;
                        var residualGuard = true;
                    }
                }
                """);
            RunGit(root, ["add", "-A"], DateTimeOffset.Parse("2026-01-01T00:02:00Z"));
            RunGit(root, ["commit", "-m", "Fix residual guard"], DateTimeOffset.Parse("2026-01-01T00:02:00Z"));
            var current = ReadGit(root, ["rev-parse", "HEAD"]).Trim();
            var acceptedAnchor = new ReviewFindingLocation(
                "src/Example.cs",
                "Example.Run",
                "acceptedGuard");

            var touched = new WorkerGitContext().ReadReviewerRoundTouchedAnchors(
                root,
                previous,
                current,
                [acceptedAnchor]);

            Assert.Empty(touched);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "Reviewer_round_diff_failure_is_loud")]
    public void ReviewerRoundDiffFailureIsLoud()
    {
        var root = CreateSeededDispatchRepository();
        try
        {
            var current = ReadGit(root, ["rev-parse", "HEAD"]).Trim();
            var anchor = new ReviewFindingLocation("README.md", "README", "heading");

            var ex = Assert.Throws<ReviewerRoundTouchScopeException>(() =>
                new WorkerGitContext().ReadReviewerRoundTouchedAnchors(
                    root,
                    "not-a-commit",
                    current,
                    [anchor]));

            Assert.Equal(WorkerGitContext.ReviewerRoundTouchScopeUnavailableErrorCode, ex.ErrorCode);
            Assert.Contains("git diff failed", ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "Unavailable inputs degrade while equal SHAs compute empty proof")]
    public void ReviewerRoundUnavailableInputsDegradeWhileIdenticalCommitsComputeEmpty()
    {
        // A task's FIRST reviewer round records no reviewed commit, so the baseline is legitimately absent.
        // Throwing here aborted the reviewer DISPATCH BEFORE IT STARTED and wedged the goal permanently:
        // retrying the Developer worked, the Reviewer threw every time, and no operator verb could
        // repopulate the baseline. Goal 5f59b0d6 sat in that state, and it is reachable from the ordinary
        // review-retry cycle.
        //
        // The list is only used to PROVE a resolved anchor was touched again. A typed diagnostic keeps
        // "cannot prove" distinct from a successful diff that proved the anchor was untouched.
        var root = CreateSeededDispatchRepository();
        try
        {
            var current = ReadGit(root, ["rev-parse", "HEAD"]).Trim();
            var anchor = new ReviewFindingLocation("src/Example.cs", "Example.Run", "guard");

            var missingPrevious = new WorkerGitContext().ReadReviewerRoundTouchScope(
                root,
                previousReviewedCommit: null,
                currentCommit: current,
                [anchor]);

            var missingCurrent = new WorkerGitContext().ReadReviewerRoundTouchScope(
                root,
                previousReviewedCommit: current,
                currentCommit: "   ",
                [anchor]);

            var equalCommits = new WorkerGitContext().ReadReviewerRoundTouchScope(
                root,
                previousReviewedCommit: current,
                currentCommit: current,
                [anchor]);

            var noAnchors = new WorkerGitContext().ReadReviewerRoundTouchScope(
                root,
                previousReviewedCommit: current,
                currentCommit: current,
                []);

            Assert.Empty(missingPrevious.TouchedAnchors);
            Assert.Contains("no reviewed-commit baseline", missingPrevious.Diagnostic, StringComparison.Ordinal);
            Assert.Empty(missingCurrent.TouchedAnchors);
            Assert.Contains("current target commit is missing", missingCurrent.Diagnostic, StringComparison.Ordinal);
            Assert.Empty(equalCommits.TouchedAnchors);
            Assert.Null(equalCommits.Diagnostic);
            Assert.Empty(noAnchors.TouchedAnchors);
            Assert.Contains("ledger has no structural anchors", noAnchors.Diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "StartDispatches_refreshes_recorded_prompt_before_worker_start")]
    public void StartDispatchesRefreshesRecordedPromptBeforeWorkerStart()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel(new TestClock(DateTimeOffset.Parse("2026-06-28T14:00:00Z")));
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt regeneration.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Fix stale retry dispatch prompts", [developer]);
    kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
        "Fix stale retry dispatch prompts",
        ["Recorded worker starts launch a prompt rendered from current task state."],
        VerificationClass.TestVerifiable,
        [],
        []));
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox {sandboxMode} --cd {workingDirectory}")
    ]);
    var prepared = GoalManagementCommandService.ProfileDispatchTask(
        kernel,
        workspace,
        goal,
        developer,
        profiles.GetRequired("codex-cli"),
        [agent],
        sandboxOptions: DisabledSandbox);
    var lateState = "late operator note that must appear in the prompt started by the worker";
    kernel.RecordTaskNote(goal.Id, developer.Id, lateState);

    Assert.ThrowsAny<InvalidOperationException>(() =>
        GoalManagementCommandService.StartDispatches(
            kernel,
            workspace,
            goal,
            [agent],
            profiles,
            runner: new BackgroundDispatchRunner(disableProcessStart: true),
            sandboxOptions: DisabledSandbox));

    var refreshedPromptPath = developer.LastDispatch!.PromptPath!;
    Assert.NotEqual(prepared.PromptPath, refreshedPromptPath);
    Assert.Contains(lateState, File.ReadAllText(refreshedPromptPath), StringComparison.Ordinal);
    Assert.DoesNotContain(refreshedPromptPath, developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("Get-Content -Raw", developer.LastDispatch.Command, StringComparison.Ordinal);
    Xunit.Assert.Null(developer.LastProcess);
}

    [Xunit.Fact(DisplayName = "StartSubscriptionReadyTasks_checkpoints_dispatch_record_before_process_start")]
    public void StartSubscriptionReadyTasksCheckpointsDispatchRecordBeforeProcessStart()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var workspace = OrchestratorWorkspace.ForDirectory(root, workingDirectory);
    WriteSkill(workingDirectory, "orchestrator-dogfood");
    var kernel = new AgentOrchestratorKernel(new TestClock(DateTimeOffset.Parse("2026-07-07T12:00:00Z")));
    var planner = new TaskSpec(TaskId.New(), "Plan the dispatch checkpoint.", AgentRole.Planner);
    var goal = kernel.CreateGoal("Checkpoint dispatch record before start", [planner]);
    kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
        "Checkpoint dispatch record before start",
        ["The dispatch record is persisted before worker start is attempted."],
        VerificationClass.TestVerifiable,
        [],
        []));
    var agent = SubscriptionPlannerAgent("planner", "Planner");
    kernel.ActivateGoal(goal.Id, [agent]);
    var checkpointCalls = 0;

    var ex = Assert.Throws<InvalidOperationException>(() =>
        GoalManagementCommandService.StartSubscriptionReadyTasks(
            kernel,
            workspace,
            goal,
            [agent],
            DispatchTestProfiles(),
            checkpointBeforeWorkerStart: (_, checkpointGoalId, checkpointTaskId, _) =>
            {
                checkpointCalls++;
                Assert.Equal(goal.Id, checkpointGoalId);
                Assert.Equal(planner.Id, checkpointTaskId);
                throw new InvalidOperationException("dispatch checkpoint failed");
            },
            runner: new BackgroundDispatchRunner(disableProcessStart: true),
            sandboxOptions: DisabledSandbox));

    Assert.Contains("dispatch checkpoint failed", ex.Message, StringComparison.Ordinal);
    Assert.Equal(1, checkpointCalls);
    Assert.True(planner.LastDispatch is not null);
    Assert.True(planner.LastProcess is null);
    var dispatchJsonFiles = Directory.Exists(workspace.LogDirectory)
        ? Directory.EnumerateFiles(workspace.LogDirectory, "*.dispatch.json", SearchOption.TopDirectoryOnly)
        : Array.Empty<string>();
    Assert.Empty(dispatchJsonFiles);
}

    [Xunit.Fact(DisplayName = "StartDispatches_checkpoint_phase_stays_post_process_after_first_spawn")]
    public void StartDispatchesCheckpointPhaseStaysPostProcessAfterFirstSpawn()
    {
        var processMayHaveStarted = false;

        Assert.Equal(
            DispatchRecordCheckpointPhase.BeforeProcessStart,
            GoalManagementCommandService.ResolveBatchCheckpointPhase(
                ref processMayHaveStarted,
                DispatchRecordCheckpointPhase.BeforeProcessStart));
        Assert.Equal(
            DispatchRecordCheckpointPhase.ProcessMayHaveStarted,
            GoalManagementCommandService.ResolveBatchCheckpointPhase(
                ref processMayHaveStarted,
                DispatchRecordCheckpointPhase.ProcessMayHaveStarted));
        Assert.Equal(
            DispatchRecordCheckpointPhase.ProcessMayHaveStarted,
            GoalManagementCommandService.ResolveBatchCheckpointPhase(
                ref processMayHaveStarted,
                DispatchRecordCheckpointPhase.BeforeProcessStart));
    }

    [Xunit.Fact(DisplayName = "Within-budget worker context remains byte-identical with no dropped sections")]
    public void WorkerPromptInputBudgetKeepsWithinBudgetPromptUnchanged()
{
    var brief = CreateBudgetBrief("brief instructions only");

    var result = WorkerPromptInputBudget.Apply(brief, "Anthropic", "claude-sonnet-4-6");

    Assert.False(result.Trimmed);
    Assert.Equal(brief.Content, result.Brief.Content);
    Assert.Empty(result.DroppedSections);
}

    [Xunit.Fact(DisplayName = "Over-budget required context fails instead of dropping prior evidence")]
    public void WorkerPromptInputBudgetRejectsOverBudgetWithoutDroppingEvidence()
{
    var brief = CreateBudgetBrief(
        "## Worker Context Package",
        "brief instructions",
        "## Prior Task Evidence",
        new string('e', 240),
        "## Source Survey",
        "source-survey-kept",
        "## Worker Context Digest",
        "digest-kept");
    var budget = WorkerPromptInputBudget.CountTokens(brief.Content.Replace(new string('e', 240), string.Empty, StringComparison.Ordinal));

    var error = Assert.Throws<WorkerPromptInputBudgetExceededException>(() =>
        WorkerPromptInputBudget.Apply(brief, "Anthropic", "claude-sonnet-4-6", budget));

    Assert.Equal(WorkerPromptInputBudget.CountTokens(brief.Content), error.TokenCount);
    Assert.Contains("required context is lossless", error.Message, StringComparison.Ordinal);
    Assert.Contains("Prior Task Evidence", brief.Content, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "Over-budget required context preserves durable research and planner bytes")]
    public void WorkerPromptInputBudgetNeverTrimsDurableResearchOrPlanSections()
    {
        var research = "RESEARCH-PIN-" + new string('r', 240);
        var plan = "PLAN-PIN-" + new string('p', 240);
        var noise = new string('n', 400);
        var brief = CreateBudgetBrief(
            "## Worker Context Package",
            "brief instructions",
            "## Durable Research Notes",
            research,
            "## Durable Planner Plan",
            plan,
            "## Prior Task Evidence",
            noise);
        var budget = WorkerPromptInputBudget.CountTokens(
            CreateBudgetBrief(
                "brief instructions",
                "## Durable Research Notes",
                research,
                "## Durable Planner Plan",
                plan).Content);

        Assert.Throws<WorkerPromptInputBudgetExceededException>(() => WorkerPromptInputBudget.Apply(
            brief,
            "Anthropic",
            "claude-sonnet-4-6",
            budget));

        Assert.Contains("RESEARCH-PIN-", brief.Content, StringComparison.Ordinal);
        Assert.Contains("PLAN-PIN-", brief.Content, StringComparison.Ordinal);
        Assert.Contains(noise, brief.Content, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Over-budget required context preserves every context section")]
    public void WorkerPromptInputBudgetNeverDropsContextSections()
{
    var brief = CreateBudgetBrief(
        "## Worker Context Package",
        "brief instructions",
        "## Prior Task Evidence",
        new string('e', 240),
        "## Source Survey",
        new string('s', 240),
        "## Worker Context Digest",
        new string('d', 240));
    var budget = WorkerPromptInputBudget.CountTokens("brief instructions") + 2;

    Assert.Throws<WorkerPromptInputBudgetExceededException>(() =>
        WorkerPromptInputBudget.Apply(brief, "Anthropic", "claude-sonnet-4-6", budget));

    Assert.Contains("Prior Task Evidence", brief.Content, StringComparison.Ordinal);
    Assert.Contains("Source Survey", brief.Content, StringComparison.Ordinal);
    Assert.Contains("Worker Context Digest", brief.Content, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "Irreducible worker prompt fails with provider and model diagnostics")]
    public void WorkerPromptInputBudgetRejectsIrreducibleOverBudgetPrompt()
{
    var brief = CreateBudgetBrief(
        "brief instructions must remain intact " + new string('b', 120),
        "## Prior Task Evidence",
        new string('e', 240));

    var ex = Assert.ThrowsAny<WorkerPromptInputBudgetExceededException>(
        () => WorkerPromptInputBudget.Apply(brief, "Ollama", "qwen3:8b", inputTokenBudgetOverride: 5));

    Assert.Equal(brief.GoalId, ex.GoalId);
    Assert.Equal(brief.TaskId, ex.TaskId);
    Assert.Equal("Ollama", ex.ProviderName);
    Assert.Equal("qwen3:8b", ex.ModelName);
    Assert.True(ex.TokenCount > ex.TokenBudget);
}

    [Xunit.Fact(DisplayName = "WorkerCommandTemplate_rejects_unresolved_variables_before_worker_dispatch_mutation")]
    public void WorkerCommandTemplateRejectsUnresolvedVariablesBeforeWorkerDispatchMutation()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Do not record unresolved worker dispatches");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var previousDispatch = ReopenTaskWithRecoverableDispatchLimit(kernel, goal, task);
    var brief = kernel.BuildTaskBrief(goal.Id, task.Id, workingDirectory: workingDirectory);

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerCommandTemplate.Prepare(
        brief,
        "manual-codex",
        "codex exec --model {subscriptionModelName} --cd {workingDirectory} (Get-Content -Raw {promptPath})",
        promptRoot,
        WorkerProfileDispatcher.BuildDispatchVariables(task.RequiredRole, workingDirectory, null, DisabledSandbox)));

    Assert.Contains("{subscriptionModelName}", ex.Message, StringComparison.Ordinal);
    Assert.Contains("subscription-dispatch", ex.Message, StringComparison.Ordinal);
    Assert.False(Directory.Exists(promptRoot));
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Equal(previousDispatch, task.LastDispatch);
}

    [Xunit.Fact(DisplayName = "DispatchStateSurface_reports_active_test_child_with_command_line")]
    public void DispatchStateSurfaceReportsActiveTestChildWithCommandLine()
{
    var root = CreateTempDirectory();
    var now = DateTimeOffset.Parse("2026-07-03T06:00:00Z");
    var clock = new TestClock(now);
    var (kernel, goal, task, process) = CreateRecordedDispatch(root, clock);
    WriteHeartbeat(
        process,
        now.AddSeconds(-20),
        now.AddSeconds(-20),
        "running",
        stdoutBytes: 12,
        stderrBytes: 0,
        childPid: 222,
        ownedCpuMs: 100,
        ownedPids: [111, 222]);

    var state = CreateStateSurface(clock, livePids: [111, 222], commandLines: new Dictionary<int, string>
        {
            [111] = "pwsh -File worker-wrapper.ps1",
            [222] = "dotnet test --filter WorkerDispatchTests"
        })
        .Evaluate(goal.Id, task);

    Assert.Equal(DispatchStateKind.ActiveTestChild, state.Kind);
    Assert.Equal("hold", state.RecommendedAction);
    Assert.True(state.ProcessTree.HasLiveChild);
    Assert.Equal("dotnet test --filter WorkerDispatchTests", state.ProcessTree.ChildCommandLine);
    Assert.Equal(DispatchRecoveryAction.Hold, state.RecoveryDecision.Action);
    _ = kernel;
}

    [Xunit.Fact(DisplayName = "DispatchStateSurface_reports_exited_worker_awaiting_reconcile")]
    public void DispatchStateSurfaceReportsExitedWorkerAwaitingReconcile()
{
    var root = CreateTempDirectory();
    var now = DateTimeOffset.Parse("2026-07-03T06:05:00Z");
    var clock = new TestClock(now);
    var (_, goal, task, process) = CreateRecordedDispatch(root, clock);
    File.WriteAllText(process.ExitCodePath, "0");
    WriteHeartbeat(
        process,
        now.AddSeconds(-10),
        now.AddSeconds(-10),
        "exiting",
        stdoutBytes: 20,
        stderrBytes: 0,
        childPid: null,
        ownedPids: [111]);

    var state = CreateStateSurface(clock, livePids: [], commandLines: new Dictionary<int, string>())
        .Evaluate(goal.Id, task);

    Assert.Equal(DispatchStateKind.ExitedAwaitingReconcile, state.Kind);
    Assert.Equal("refresh-dispatch", state.RecommendedAction);
    Assert.True(state.Artifacts.ExitCodeExists);
    Assert.Equal(DispatchRecoveryAction.ReconcileFromExit, state.RecoveryDecision.Action);
}

    [Xunit.Fact(DisplayName = "DispatchStateSurface_reports_synthetic_exit_as_interrupted_work")]
    public void DispatchStateSurfaceReportsSyntheticExitAsInterruptedWork()
{
    var root = CreateTempDirectory();
    var now = DateTimeOffset.Parse("2026-07-03T06:07:00Z");
    var clock = new TestClock(now);
    var (_, goal, task, process) = CreateRecordedDispatch(root, clock);
    DispatchExitArtifacts.Write(
        process.ExitCodePath,
        DispatchExitArtifacts.Synthetic(1, "worker host disappeared", now));

    var state = CreateStateSurface(clock, livePids: [], commandLines: new Dictionary<int, string>())
        .Evaluate(goal.Id, task);

    Assert.Equal(DispatchStateKind.InterruptedWork, state.Kind);
    Assert.Equal("refresh-dispatch", state.RecommendedAction);
    Assert.Equal(DispatchExitArtifactOrigin.Synthetic, state.Artifacts.ExitArtifactOrigin);
    Assert.Equal("worker host disappeared", state.Artifacts.ExitArtifactReason);
    Assert.Equal(DispatchRecoveryAction.PreserveInterruptedWork, state.RecoveryDecision.Action);
}

    [Xunit.Fact(DisplayName = "GoalOperatorDisposition_recovers_interrupted_work_through_refresh")]
    public void GoalOperatorDispositionRecoversInterruptedWorkThroughRefresh()
{
    var root = CreateTempDirectory();
    var now = DateTimeOffset.Parse("2026-07-03T06:08:00Z");
    var clock = new TestClock(now);
    var (_, goal, task, process) = CreateRecordedDispatch(root, clock);
    DispatchExitArtifacts.Write(
        process.ExitCodePath,
        DispatchExitArtifacts.Synthetic(1, "worker host disappeared", now));

    var disposition = new GoalOperatorDispositionSurface(
        clock,
        CreateStateSurface(clock, livePids: [], commandLines: new Dictionary<int, string>()))
        .Evaluate(goal, pendingHumanInputCount: 0, verificationSatisfied: false);

    Assert.Equal(OperatorDispositionState.Recover, disposition.State);
    Assert.Equal("refresh-dispatch 1", disposition.NextSafeCommand);
    var dispatch = Assert.Single(disposition.Dispatches, candidate => candidate.TaskId == task.Id);
    Assert.Equal(DispatchStateKind.InterruptedWork, dispatch.DispatchState!.Kind);
}

    [Xunit.Fact(DisplayName = "DispatchStateSurface_reports_stale_cleanup_when_process_and_exit_are_absent")]
    public void DispatchStateSurfaceReportsStaleCleanupWhenProcessAndExitAreAbsent()
{
    var root = CreateTempDirectory();
    var clock = new TestClock(DateTimeOffset.Parse("2026-07-03T06:10:00Z"));
    var (_, goal, task, _) = CreateRecordedDispatch(root, clock, AgentRole.Researcher);

    var state = CreateStateSurface(clock, livePids: [], commandLines: new Dictionary<int, string>())
        .Evaluate(goal.Id, task);

    Assert.Equal(DispatchStateKind.StaleCleanup, state.Kind);
    Assert.Equal("mark-stale", state.RecommendedAction);
    Assert.False(state.Artifacts.ExitCodeExists);
    Assert.False(state.Heartbeat.IsAvailable);
    Assert.Equal(DispatchRecoveryAction.MarkStale, state.RecoveryDecision.Action);
}

    [Xunit.Fact(DisplayName = "DispatchStateSurface_reports_wedged_live_process_after_idle_timeout")]
    public void DispatchStateSurfaceReportsWedgedLiveProcessAfterIdleTimeout()
{
    var root = CreateTempDirectory();
    var now = DateTimeOffset.Parse("2026-07-03T06:20:00Z");
    var clock = new TestClock(now);
    var (_, goal, task, process) = CreateRecordedDispatch(root, clock);
    WriteHeartbeat(
        process,
        now.AddMinutes(-40),
        now.AddMinutes(-40),
        "running",
        stdoutBytes: 0,
        stderrBytes: 0,
        childPid: null,
        ownedCpuMs: 0,
        ownedPids: [111]);

    var state = CreateStateSurface(clock, livePids: [111], commandLines: new Dictionary<int, string>
        {
            [111] = "codex exec prompt"
        })
        .Evaluate(goal.Id, task);

    Assert.Equal(DispatchStateKind.WedgedProcess, state.Kind);
    Assert.Equal("classify-blocker", state.RecommendedAction);
    Assert.Equal(DispatchRecoveryAction.ClassifyBlocker, state.RecoveryDecision.Action);
    Assert.True(state.RecoveryDecision.Reason.Contains("idle", StringComparison.OrdinalIgnoreCase), state.RecoveryDecision.Reason);
}

    [Xunit.Fact(DisplayName = "DispatchStateSurface_reports_dirty_worktree_and_commit_state")]
    public void DispatchStateSurfaceReportsDirtyWorktreeAndCommitState()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-07-03T06:30:00Z"));
    var (_, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "WORKER_RESULT:",
        string.Empty,
        clock);
    File.WriteAllText(Path.Combine(process.WorkingDirectory, "operator-state-surface.txt"), "dirty evidence");

    var state = CreateStateSurface(clock, livePids: [], commandLines: new Dictionary<int, string>())
        .Evaluate(goal.Id, task);

    Assert.True(state.Worktree.Exists);
    Assert.True(state.Worktree.IsGitWorktree);
    Assert.True(state.Worktree.IsDirty == true);
    Assert.False(string.IsNullOrWhiteSpace(state.Worktree.HeadCommit));
    Assert.NotNull(state.Worktree.CommitsAfterDispatch);
    Assert.Contains(state.Worktree.StatusEntries, entry => entry.Contains("operator-state-surface.txt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "Dashboard_work_summary_surfaces_authoritative_dispatch_state")]
    public void DashboardWorkSummarySurfacesAuthoritativeDispatchState()
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-07-03T06:35:00Z");
    var clock = new TestClock(now);
    var (_, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "worker output",
        string.Empty,
        clock);
    File.WriteAllText(Path.Combine(process.WorkingDirectory, "dispatch-state-evidence.txt"), "dirty evidence");
    WriteHeartbeat(
        process,
        now.AddSeconds(-12),
        now.AddSeconds(-12),
        "exiting",
        stdoutBytes: 13,
        stderrBytes: 0,
        childPid: 222,
        ownedPids: [process.ProcessId, 222],
        exitFileExists: true);

    var summary = DashboardResponseMapper.ToTaskWorkSummaryDto(goal, task);
    var state = Assert.IsType<DispatchAuthoritativeStateDto>(summary.DispatchState);

    Assert.Equal(DispatchStateKind.ExitedAwaitingReconcile, state.Kind);
    Assert.Equal("refresh-dispatch", state.RecommendedAction);
    Assert.Equal(DispatchRecoveryAction.ReconcileFromExit, state.RecoveryDecision.Action);
    Assert.Equal(process.ProcessId, state.ProcessTree.WrapperProcessId);
    Assert.Equal(222, state.ProcessTree.ChildProcessId);
    Assert.Contains(state.ProcessTree.Processes, node => node.ProcessId == process.ProcessId);
    Assert.Contains(state.ProcessTree.Processes, node => node.ProcessId == 222);
    Assert.True(state.Artifacts.StandardOutputExists);
    Assert.Equal(13, state.Artifacts.StandardOutputBytes);
    Assert.True(state.Artifacts.ExitCodeExists);
    Assert.True(state.Artifacts.HeartbeatExists);
    Assert.True(state.Worktree.IsDirty == true);
    Assert.False(string.IsNullOrWhiteSpace(state.Worktree.HeadCommit));
    Assert.NotNull(state.Worktree.CommitsAfterDispatch);
    Assert.Contains(state.Worktree.StatusEntries, entry => entry.Contains("dispatch-state-evidence.txt", StringComparison.Ordinal));
    Assert.True(state.StaleThresholds.RecentHeartbeatGraceSeconds > 0);
    Assert.True(state.StaleThresholds.LiveIdleTimeoutSeconds > state.StaleThresholds.RecentHeartbeatGraceSeconds);
    Assert.Contains("dirty_worktree=True", state.Summary);
}

    [Xunit.Fact(DisplayName = "GoalOperatorDisposition_waits_for_quiet_live_worker")]
    public void GoalOperatorDispositionWaitsForQuietLiveWorker()
{
    var root = CreateTempDirectory();
    var now = DateTimeOffset.Parse("2026-07-03T07:00:00Z");
    var clock = new TestClock(now);
    var (_, goal, task, process) = CreateRecordedDispatch(root, clock);
    WriteHeartbeat(
        process,
        now.AddSeconds(-20),
        now.AddSeconds(-20),
        "running",
        stdoutBytes: 0,
        stderrBytes: 0,
        childPid: 222,
        ownedCpuMs: 25,
        ownedPids: [111, 222]);

    var disposition = new GoalOperatorDispositionSurface(
        clock,
        CreateStateSurface(clock, livePids: [111, 222], commandLines: new Dictionary<int, string>()))
        .Evaluate(goal, pendingHumanInputCount: 0, verificationSatisfied: false);

    Assert.Equal(OperatorDispositionState.Wait, disposition.State);
    Assert.Equal("wait", disposition.NextSafeCommand);
    Assert.Contains(disposition.Dispatches, dispatch => dispatch.TaskId == task.Id && dispatch.State == OperatorDispositionState.Wait);
}

    [Xunit.Fact(DisplayName = "GoalOperatorDisposition_blocks_completed_dirty_worker")]
    public void GoalOperatorDispositionBlocksCompletedDirtyWorker()
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-07-03T07:05:00Z");
    var clock = new TestClock(now);
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "worker output",
        string.Empty,
        clock);
    File.WriteAllText(Path.Combine(process.WorkingDirectory, "dirty-disposition.txt"), "dirty evidence");
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Worker completed with dirty worktree.");

    var disposition = new GoalOperatorDispositionSurface(
        clock,
        CreateStateSurface(clock, livePids: [], commandLines: new Dictionary<int, string>()))
        .Evaluate(goal, pendingHumanInputCount: 0, verificationSatisfied: false);

    Assert.Equal(OperatorDispositionState.Blocked, disposition.State);
    Assert.Contains("dirty-worktree", disposition.Blockers);
    Assert.Equal("task 1", disposition.NextSafeCommand);
}

    [Xunit.Fact(DisplayName = "GoalOperatorDisposition_retries_failed_review")]
    public void GoalOperatorDispositionRetriesFailedReview()
    {
        var kernel = new AgentOrchestratorKernel();
        var review = new TaskSpec(TaskId.New(), "Review implementation.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Review retry", [review]);
        var agent = new AgentDefinition(new AgentId("reviewer"), "Reviewer", AgentRole.Reviewer, new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Reviewer found a blocker.");

        var disposition = new GoalOperatorDispositionSurface(new TestClock(DateTimeOffset.Parse("2026-07-03T07:10:00Z")))
            .Evaluate(goal, pendingHumanInputCount: 0, verificationSatisfied: false);

        Assert.Equal(OperatorDispositionState.Retry, disposition.State);
        Assert.Equal("retry 1 <note>", disposition.NextSafeCommand);
        Assert.True(disposition.Reason.Contains("failed", StringComparison.OrdinalIgnoreCase), disposition.Reason);
    }

    [Xunit.Fact(DisplayName = "GoalOperatorDisposition_accepts_active_verified_goal_with_worktree")]
    public void GoalOperatorDispositionAcceptsActiveVerifiedGoalWithWorktree()
    {
        var root = CreateSeededDispatchRepository();
        var kernel = new AgentOrchestratorKernel();
        var taskSpec = new TaskSpec(TaskId.New(), "Finish cleanup.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Cleanup debt", [taskSpec]);
        var agent = new AgentDefinition(new AgentId("developer"), "Developer", AgentRole.Developer, new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        _ = GoalWorktrees.Ensure(root, goal.Id);
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");

        var disposition = new GoalOperatorDispositionSurface(new TestClock(DateTimeOffset.Parse("2026-07-03T07:15:00Z")))
            .Evaluate(goal, pendingHumanInputCount: 0, verificationSatisfied: true, executionDirectory: root);

        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(OperatorDispositionState.Accept, disposition.State);
        Assert.Equal($"acceptance {goal.Id.Value[..8]}", disposition.NextSafeCommand);
        Assert.DoesNotContain("goal-worktree-cleanup-debt", disposition.Blockers);
    }

    [Xunit.Fact(DisplayName = "GoalOperatorDisposition_reports_terminal_worktree_cleanup_debt")]
    public void GoalOperatorDispositionReportsTerminalWorktreeCleanupDebt()
    {
        var root = CreateSeededDispatchRepository();
        var kernel = new AgentOrchestratorKernel();
        var taskSpec = new TaskSpec(TaskId.New(), "Finish cleanup.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Cleanup debt", [taskSpec]);
        var agent = new AgentDefinition(new AgentId("developer"), "Developer", AgentRole.Developer, new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        _ = GoalWorktrees.Ensure(root, goal.Id);
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", root, 0, "ok", string.Empty, DateTimeOffset.Parse("2026-07-03T07:15:00Z")));

        var disposition = new GoalOperatorDispositionSurface(new TestClock(DateTimeOffset.Parse("2026-07-03T07:15:00Z")))
            .Evaluate(goal, pendingHumanInputCount: 0, verificationSatisfied: true, executionDirectory: root);

        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Equal(OperatorDispositionState.Accept, disposition.State);
        Assert.Equal($"acceptance {goal.Id.Value[..8]}", disposition.NextSafeCommand);
        Assert.DoesNotContain("goal-worktree-cleanup-debt", disposition.Blockers);
    }

    [Xunit.Fact(DisplayName = "GoalOperatorDisposition_flags_stale_terminal_human_wait_desync")]
    public void GoalOperatorDispositionFlagsStaleTerminalHumanWaitDesync()
    {
        var kernel = new AgentOrchestratorKernel();
        var taskSpec = new TaskSpec(TaskId.New(), "Answer then finish.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Human wait desync", [taskSpec]);
        var agent = new AgentDefinition(new AgentId("developer"), "Developer", AgentRole.Developer, new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        kernel.RequestHumanInput(goal.Id, task.Id, "Need operator choice.");
        kernel.CancelGoal(goal.Id, "Cancelled despite pending input.");

        var disposition = new GoalOperatorDispositionSurface(new TestClock(DateTimeOffset.Parse("2026-07-03T07:20:00Z")))
            .Evaluate(goal, pendingHumanInputCount: 1, verificationSatisfied: false);

        Assert.Equal(GoalStatus.Cancelled, goal.Status);
        Assert.Equal(OperatorDispositionState.ProductBug, disposition.State);
        Assert.Equal($"terminal-goal-sweep {goal.Id.Value[..8]}", disposition.NextSafeCommand);
        Assert.Contains("stale-terminal-human-wait", disposition.Blockers);
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_writes_claude_auth_diagnostic_when_api_key_missing")]
    public void DispatchProcessHostWritesClaudeAuthDiagnosticWhenApiKeyMissing()
{
    var root = CreateTempDirectory();
    try
    {
        var startInfo = CreateSandboxStartInfo(root);
        var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
        var stderrPath = Path.Combine(root, "dispatch.stderr.log");

        DispatchProcessHost.SeedProviderEnvironment(
            startInfo,
            WorkerSandboxProvider.Claude,
            sandboxRoot,
            stderrPath,
            anthropicApiKeyAccessor: () => null,
            claudeCredentialDirectoryAccessor: () => Path.Combine(root, "no-cli-credentials"));

        Assert.False(startInfo.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(startInfo.Environment.ContainsKey("CODEX_HOME"));
        Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "codex-home")));
        Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var claudeConfigDir));
        Assert.True(Directory.Exists(claudeConfigDir));
        var stderr = File.ReadAllText(stderrPath);
        Assert.Contains("ANTHROPIC_API_KEY is not set", stderr);
        Assert.Contains("Claude", stderr);
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

    [Xunit.Fact(DisplayName = "DispatchProcessHost_seeds_cli_credentials_into_sandbox_config_without_diagnostic")]
    public void DispatchProcessHostSeedsCliCredentialsIntoSandboxConfigWithoutDiagnostic()
{
    var root = CreateTempDirectory();
    try
    {
        var credentialSource = Path.Combine(root, "operator-claude");
        Directory.CreateDirectory(credentialSource);
        File.WriteAllText(Path.Combine(credentialSource, ".credentials.json"), "{\"token\":\"subscription\"}");
        File.WriteAllText(Path.Combine(credentialSource, "settings.json"), "{\"theme\":\"dark\"}");
        var startInfo = CreateSandboxStartInfo(root);
        var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
        var stderrPath = Path.Combine(root, "dispatch.stderr.log");

        DispatchProcessHost.SeedProviderEnvironment(
            startInfo,
            WorkerSandboxProvider.Claude,
            sandboxRoot,
            stderrPath,
            anthropicApiKeyAccessor: () => null,
            claudeCredentialDirectoryAccessor: () => credentialSource);

        Assert.False(startInfo.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var claudeConfigDir));
        Assert.Equal(
            "{\"token\":\"subscription\"}",
            File.ReadAllText(Path.Combine(claudeConfigDir!, ".credentials.json")));
        Assert.Equal(
            "{\"theme\":\"dark\"}",
            File.ReadAllText(Path.Combine(claudeConfigDir!, "settings.json")));
        Assert.False(File.Exists(stderrPath));
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

    [Xunit.Fact(DisplayName = "DispatchProcessHost_worker_stderr_stream_preserves_claude_auth_diagnostic")]
    public void DispatchProcessHostWorkerStderrStreamPreservesClaudeAuthDiagnostic()
{
    var root = CreateTempDirectory();
    try
    {
        var startInfo = CreateSandboxStartInfo(root);
        var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
        var stderrPath = Path.Combine(root, "dispatch.stderr.log");

        DispatchProcessHost.SeedProviderEnvironment(
            startInfo,
            WorkerSandboxProvider.Claude,
            sandboxRoot,
            stderrPath,
            anthropicApiKeyAccessor: () => null,
            claudeCredentialDirectoryAccessor: () => Path.Combine(root, "no-cli-credentials"));

        using (var stderr = DispatchProcessHost.OpenWorkerStderrStream(stderrPath))
        using (var writer = new StreamWriter(stderr))
        {
            writer.WriteLine("worker stderr");
        }

        var text = File.ReadAllText(stderrPath);
        Assert.Contains("ANTHROPIC_API_KEY is not set", text);
        Assert.Contains("worker stderr", text);
        Assert.True(
            text.IndexOf("ANTHROPIC_API_KEY is not set", StringComparison.Ordinal) <
            text.IndexOf("worker stderr", StringComparison.Ordinal),
            text);
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

    [Xunit.Fact(DisplayName = "Dashboard_task_summary_exposes_worker_result_skill_usage")]
    public void DashboardTaskSummaryExposesWorkerResultSkillUsage()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Implement with selected skills", AgentRole.Developer);
    var goal = kernel.CreateGoal("Expose skill evidence", [task]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "worker refresh",
        root,
        0,
        "Done." + Environment.NewLine + WorkerResultBlock(
            "src/Feature.cs",
            "dotnet test --filter Feature",
            "Passed: 1",
            "abc123",
            skills: "dotnet-windows-build-hygiene, orchestrator-dogfood"),
        string.Empty,
        DateTimeOffset.UtcNow));

    var summary = DashboardResponseMapper.ToTaskSummaryDto(goal, task);

    Assert.True(summary.HasWorkerResultSkillEvidence);
    Assert.True(summary.WorkerResultSkills!.Any(skill => skill == "dotnet-windows-build-hygiene"));
    Assert.True(summary.WorkerResultSkills!.Any(skill => skill == "orchestrator-dogfood"));
}

    [Xunit.Fact(DisplayName = "LocalDispatchRunner_rejects_inactive_dispatch_execution")]
    public async Task LocalDispatchRunnerRejectsInactiveDispatchExecution()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Avoid duplicate local dispatch execution");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "Write-Output should-not-run", root, DateTimeOffset.UtcNow));
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Already handled.");

    var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(async () => await new LocalDispatchRunner().ExecuteLatestDispatchAsync(kernel, goal.Id, task.Id));

    Assert.Contains("status is Completed", ex.Message, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_writes_current_retry_evidence_for_collapsed_prompt_pointers")]
    public void WorkerContextArtifactsWritesCurrentRetryEvidenceForCollapsedPromptPointers()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var clock = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var currentTask = new TaskSpec(TaskId.New(), "Fix prompt budget retry.", AgentRole.Developer, "Run focused prompt tests.");
    var goal = kernel.CreateGoal("Carry retry evidence in context artifacts", [currentTask]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.RecordTaskDispatch(goal.Id, currentTask.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec retry-prompt.md",
        workingDirectory,
        clock));
    kernel.RecordTaskVerification(goal.Id, currentTask.Id, new TaskVerificationRecord(
        "dotnet test --filter TaskBriefTests",
        workingDirectory,
        1,
        "current retry stdout evidence",
        "current retry stderr evidence",
        clock));

    var contextDirectory = WorkerContextArtifacts.Write(goal, currentTask, workingDirectory);

    var currentTaskArtifact = File.ReadAllText(Path.Combine(contextDirectory, "current-task.md"));
    var manifest = File.ReadAllText(Path.Combine(contextDirectory, "manifest.md"));
    Assert.Contains("## Last Dispatch", currentTaskArtifact, StringComparison.Ordinal);
    Assert.Contains("codex exec retry-prompt.md", currentTaskArtifact, StringComparison.Ordinal);
    Assert.Contains("## Last Verification", currentTaskArtifact, StringComparison.Ordinal);
    Assert.Contains("current retry stdout evidence", currentTaskArtifact, StringComparison.Ordinal);
    Assert.Contains("current retry stderr evidence", currentTaskArtifact, StringComparison.Ordinal);
    Assert.Contains("retry evidence when present", manifest, StringComparison.Ordinal);
}

    [Xunit.Theory(DisplayName = "WorkerContextArtifacts_writes_role_specific_priorities_and_prior_summaries")]
    [Xunit.InlineData(AgentRole.Developer, "artifact-registry.json: verify current context artifacts", "workflow-brokers.md: use deterministic broker actions")]
    [Xunit.InlineData(AgentRole.Tester, "artifact-registry.json: verify artifact freshness", "workflow-brokers.md: use deterministic broker actions")]
    [Xunit.InlineData(AgentRole.Reviewer, "artifact-registry.json: verify hashes", "workflow-brokers.md: check deterministic broker failures")]
    [Xunit.InlineData(AgentRole.Planner, "artifact-registry.json: confirm available artifacts", "selected-skills.md: read the selected planning skill")]
    [Xunit.InlineData(AgentRole.Researcher, "artifact-registry.json: identify relevant artifacts", "selected-skills.md: read the selected research skill")]
    public void WorkerContextArtifactsWritesRoleSpecificPrioritiesAndPriorSummaries(
        AgentRole role,
        string expectedPrimaryPriority,
        string expectedSecondaryPriority)
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Implement summary source.", AgentRole.Developer);
    var currentTask = new TaskSpec(TaskId.New(), "Use role bundle.", role, "Run focused checks.");
    var goal = kernel.CreateGoal("Bundle role context", [priorTask, currentTask]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "dotnet test --filter WorkerContextArtifacts",
        workingDirectory,
        0,
        """
        Changed files: src/Context.cs, tests/ContextTests.cs
        Behavior changes: context bundles summarize prior work before full evidence
        Verification result: passed focused tests
        Risks: none reported
        """,
        string.Empty,
        DateTimeOffset.UtcNow,
        "Model fit: OpenAI/gpt-5.5 - adequate - focused context bundle implementation.")); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.

    var contextDirectory = WorkerContextArtifacts.Write(goal, currentTask, workingDirectory);

    var manifest = File.ReadAllText(Path.Combine(contextDirectory, "manifest.md"));
    var digest = File.ReadAllText(Path.Combine(contextDirectory, "digest.md"));
    var summaries = File.ReadAllText(Path.Combine(contextDirectory, "prior-task-summaries.md"));
    Assert.Contains("## Role Artifact Priorities", manifest, StringComparison.Ordinal);
    Assert.Contains(expectedPrimaryPriority, manifest, StringComparison.Ordinal);
    Assert.Contains(expectedSecondaryPriority, manifest, StringComparison.Ordinal);
    Assert.Contains("## Role Artifact Priorities", digest, StringComparison.Ordinal);
    Assert.Contains(expectedPrimaryPriority, digest, StringComparison.Ordinal);
    Assert.Contains("Changed files: src/Context.cs, tests/ContextTests.cs", summaries, StringComparison.Ordinal);
    Assert.Contains("Behavior changes: context bundles summarize prior work before full evidence", summaries, StringComparison.Ordinal);
    Assert.Contains("Verification: `dotnet test --filter WorkerContextArtifacts`", summaries, StringComparison.Ordinal);
    Assert.Contains("Verification result: passed focused tests", summaries, StringComparison.Ordinal);
    Assert.Contains("Risks: none reported", summaries, StringComparison.Ordinal);
    Assert.Contains("Model fit: OpenAI/gpt-5.5 - adequate - focused context bundle implementation.", summaries, StringComparison.Ordinal); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
}

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_selects_relevant_skills_and_registers_skill_artifact")]
    public void WorkerContextArtifactsSelectsRelevantSkillsAndRegistersSkillArtifact()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    WriteSkill(workingDirectory, "dotnet-windows-build-hygiene");
    WriteSkill(workingDirectory, "orchestrator-dogfood");
    WriteSkill(workingDirectory, "aspnet-core");
    WriteSkill(workingDirectory, "playwright");
    WriteSkill(workingDirectory, "skill-authoring");
    WriteSkill(workingDirectory, "verification-before-completion");
    WriteSkill(workingDirectory, "systematic-debugging");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(
        TaskId.New(),
        "Implement an ASP.NET Core dashboard UI improvement for orchestrator subscription dispatch with Playwright browser automation coverage and update .agents/skills/skill-authoring/SKILL.md.",
        AgentRole.Developer,
        "Run dotnet test for the focused worker dispatch tests, inspect selected-skills.md, and run a Playwright dashboard UI smoke check.");
    var goal = kernel.CreateGoal("Improve orchestrator dogfood backlog automation", [task]);

    var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory);

    var selectedSkills = File.ReadAllText(Path.Combine(contextDirectory, "selected-skills.md"));
    using var registryDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(contextDirectory, "artifact-registry.json")));
    var artifacts = registryDocument.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
    var skillArtifact = artifacts.Single(artifact => artifact.GetProperty("path").GetString() == "selected-skills.md");
    Assert.True(skillArtifact.GetProperty("exists").GetBoolean());
    Assert.Contains("dotnet-windows-build-hygiene", selectedSkills, StringComparison.Ordinal);
    Assert.Contains("orchestrator-dogfood", selectedSkills, StringComparison.Ordinal);
    Assert.Contains("aspnet-core", selectedSkills, StringComparison.Ordinal);
    Assert.Contains("playwright", selectedSkills, StringComparison.Ordinal);
    Assert.Contains("skill-authoring", selectedSkills, StringComparison.Ordinal);
    Assert.Contains("verification-before-completion", selectedSkills, StringComparison.Ordinal);
    Assert.DoesNotContain("systematic-debugging", selectedSkills, StringComparison.Ordinal);
    Assert.Contains("Status: available", selectedSkills, StringComparison.Ordinal);
    Assert.Contains("WORKER_RESULT skills field", selectedSkills, StringComparison.Ordinal);
    Assert.Contains("skill selection", skillArtifact.GetProperty("summary").GetString()!, StringComparison.Ordinal);
}

    [Xunit.Fact]
    public void WorkerSkillSelectorRoutesUpstreamRoleDefaultsOnlyToOwners()
    {
        var workingDirectory = CreateTempDirectory();
        WriteSkill(workingDirectory, "research-evidence");
        WriteSkill(workingDirectory, "criterion-ownership-planning");

        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var task = new TaskSpec(TaskId.New(), "Map bounded inputs.", role);
            var goal = new AgentOrchestratorKernel().CreateGoal("Bounded inquiry", [task]);
            var selected = new WorkerSkillSelector().SelectSkillRequirements(goal, task, workingDirectory);

            Assert.Equal(role == AgentRole.Researcher, selected.Any(skill => skill.Name == "research-evidence"));
            Assert.Equal(role == AgentRole.Planner, selected.Any(skill => skill.Name == "criterion-ownership-planning"));
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Researcher, "research-evidence")]
    [Xunit.InlineData(AgentRole.Planner, "criterion-ownership-planning")]
    public void WorkerContextArtifactsMarksUpstreamRoleSkillsAvailable(AgentRole role, string expectedSkill)
    {
        var workingDirectory = CreateTempDirectory();
        WriteSkill(workingDirectory, expectedSkill);
        var task = new TaskSpec(TaskId.New(), "Map bounded inputs.", role);
        var goal = new AgentOrchestratorKernel().CreateGoal("Bounded inquiry", [task]);

        var selected = new WorkerSkillSelector().SelectSkillRequirements(goal, task, workingDirectory);
        var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory);
        var selectedSkills = File.ReadAllText(Path.Combine(contextDirectory, "selected-skills.md"));

        var skill = Assert.Single(selected);
        Assert.Equal(expectedSkill, skill.Name);
        Assert.True(skill.Available);
        Assert.Contains($"- {expectedSkill}{Environment.NewLine}", selectedSkills, StringComparison.Ordinal);
        Assert.Contains("  Status: available", selectedSkills, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Researcher, "selected-skills.md: read the selected research skill")]
    [Xunit.InlineData(AgentRole.Planner, "selected-skills.md: read the selected planning skill")]
    public void WorkerContextArtifactsWritesSelectedSkillsPriorityForUpstreamRoles(
        AgentRole role,
        string expectedPriority)
    {
        var workingDirectory = CreateTempDirectory();
        var task = new TaskSpec(TaskId.New(), "Map bounded inputs.", role);
        var goal = new AgentOrchestratorKernel().CreateGoal("Bounded inquiry", [task]);

        var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory);
        var manifest = File.ReadAllText(Path.Combine(contextDirectory, "manifest.md"));
        var digest = File.ReadAllText(Path.Combine(contextDirectory, "digest.md"));

        Assert.Contains(expectedPriority, manifest, StringComparison.Ordinal);
        Assert.Contains(expectedPriority, digest, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PrepareTask_IdeationDispatch_PreservesLegacyBriefAndProfile()
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Propose one bounded improvement.", AgentRole.Ideation);
        var oversized = "legacy-head-" + new string('x', 800) + "-legacy-tail";
        var goal = kernel.CreateGoal(oversized, [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var profile = new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}");

        var result = WorkerProfileDispatcher.PrepareTask(
            kernel, goal, task, profile, Path.Combine(root, "prompts"), workingDirectory, DateTimeOffset.UtcNow,
            providerName: "OpenAI", modelName: AgentCatalog.OpenAiSubscriptionModelAlias);

        var prompt = File.ReadAllText(result.PromptPath);
        Assert.DoesNotContain("## Worker Context Package", prompt, StringComparison.Ordinal);
        Assert.Null(result.Task.LastDispatch!.ContextPackageReceipt);
        Assert.DoesNotContain("--json", result.Task.LastDispatch.Command, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, result.Task.LastDispatch.ModelName);
        var digest = File.ReadAllText(Path.Combine(
            workingDirectory,
            ".orchestrator-context",
            goal.Id.Value,
            "digest.md"));
        Assert.Contains("...[truncated", digest, StringComparison.Ordinal);
        Assert.DoesNotContain("-legacy-tail", digest, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias)]
    [Xunit.InlineData("Anthropic", AgentCatalog.OpenAiSolSubscriptionModelAlias)]
    public void PrepareTask_NonTypedContextRoute_PreservesLegacyArtifactCaps(
        string providerName,
        string modelName)
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement through a legacy context route.", AgentRole.Developer);
        var oversized = "legacy-head-" + new string('x', 800) + "-legacy-tail";
        var goal = kernel.CreateGoal(oversized, [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var profile = new WorkerProfile("subscription", "worker --cd {workingDirectory}");

        var result = WorkerProfileDispatcher.PrepareTask(
            kernel,
            goal,
            task,
            profile,
            Path.Combine(root, "prompts"),
            workingDirectory,
            DateTimeOffset.UtcNow,
            providerName: providerName,
            modelName: modelName);

        Assert.Null(result.Task.LastDispatch!.ContextPackageReceipt);
        var digest = File.ReadAllText(Path.Combine(
            workingDirectory,
            ".orchestrator-context",
            goal.Id.Value,
            "digest.md"));
        Assert.Contains("...[truncated", digest, StringComparison.Ordinal);
        Assert.DoesNotContain("-legacy-tail", digest, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void TypedContextPackageRemovesSemanticSourceProjectionsFromResidualBrief()
    {
        var brief = string.Join(Environment.NewLine,
        [
            "# Agent Task Brief",
            "<!-- ACCUMULATED_RETRY_FEEDBACK_START -->",
            "## Accumulated retry/review feedback",
            "formatted causal event that is not byte-equal to timeline JSON",
            "formatted review finding that is not byte-equal to review-finding JSON",
            "<!-- ACCUMULATED_RETRY_FEEDBACK_END -->",
            "Goal id: goal-1",
            "## Instructions",
            "preserve this worker-only instruction",
            "## Refined Spec",
            "Behavioral contract: formatted typed specification",
            "Acceptance criteria:",
            "- formatted criterion",
            "## Developer Requirements",
            "preserve this role contract",
            "## Durable Research Notes",
            "complete research payload",
            "## Durable Planner Plan",
            "complete planner payload",
            "## Prior Task Evidence",
            "- formatted aggregate and per-task output projection",
            "## Recent Timeline",
            "- formatted timeline projection"
        ]);

        var residual = WorkerProfileDispatcher.RemoveTypedSourceProjections(brief, AgentRole.Developer);

        Assert.DoesNotContain("formatted causal event", residual, StringComparison.Ordinal);
        Assert.DoesNotContain("formatted review finding", residual, StringComparison.Ordinal);
        Assert.DoesNotContain("formatted typed specification", residual, StringComparison.Ordinal);
        Assert.DoesNotContain("formatted aggregate and per-task output projection", residual, StringComparison.Ordinal);
        Assert.DoesNotContain("formatted timeline projection", residual, StringComparison.Ordinal);
        Assert.DoesNotContain("complete research payload", residual, StringComparison.Ordinal);
        Assert.DoesNotContain("complete planner payload", residual, StringComparison.Ordinal);
        Assert.Contains("preserve this worker-only instruction", residual, StringComparison.Ordinal);
        Assert.Contains("preserve this role contract", residual, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void TypedContextPackageWiresRecoveredV0HandoffBytesIntoMandatoryDelivery()
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        File.WriteAllText(Path.Combine(workingDirectory, ".orchestrator-handoff.md"), string.Join(Environment.NewLine,
        [
            "# Prior Task Handoff",
            "",
            "## Planner: legacy plan",
            "",
            "### Verification Output",
            "legacy complete payload",
            "",
            "---"
        ]));
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement from legacy evidence.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Preserve legacy evidence", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var profile = new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}");

        var result = WorkerProfileDispatcher.PrepareTask(
            kernel,
            goal,
            task,
            profile,
            Path.Combine(root, "prompts"),
            workingDirectory,
            DateTimeOffset.UtcNow,
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias);
        var prompt = File.ReadAllText(result.PromptPath);
        var receipt = result.Task.LastDispatch!.ContextPackageReceipt!;
        var section = Assert.Single(receipt.Sections,
            item => item.LogicalIdentity == "legacy-handoff/v0/1");

        Assert.Equal(ContextDeliveryMode.MandatoryFile, section.DeliveryMode);
        Assert.Contains(receipt.Sections, item =>
            item.LogicalIdentity == "goal/timeline.json" &&
            item.DeliveryMode == ContextDeliveryMode.MandatoryFile);
        Assert.Contains(receipt.Sections, item => item.LogicalIdentity == "brief/current.md");
        Assert.Contains(receipt.Sections, item => item.LogicalIdentity == "context/manifest.v1.json");
        Assert.Contains("MANDATORY READ: identity=legacy-handoff/v0/1", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy complete payload", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Context files: read", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("--json", result.Task.LastDispatch.Command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("artifact-registry.json", prompt, StringComparison.Ordinal);
        Assert.Contains("context-package.json", prompt, StringComparison.Ordinal);
        Assert.Contains("manifest.md", prompt, StringComparison.Ordinal);
        Assert.Equal(
            Encoding.UTF8.GetBytes("legacy complete payload"),
            File.ReadAllBytes(Path.Combine(workingDirectory, section.MandatoryRelativePath!.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Xunit.Fact]
    public void PrepareTask_SelfPointerWithMatchingFile_UsesMaterialization()
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Resume the current task.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Recover self-referential handoff evidence", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var bytes = Encoding.UTF8.GetBytes("current task verification evidence");
        var identity = new LogicalArtifactIdentity($"prior/{task.Id.Value}/verification-output");
        var relativePath = $".orchestrator-context/legacy-handoff/{task.Id.Value}/verification-output.bin";
        var materializationPath = Path.Combine(
            workingDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(materializationPath)!);
        File.WriteAllBytes(materializationPath, bytes);
        var pointer = LegacyHandoffCompatibilityResolver.CreateV1Pointer(identity, bytes, relativePath);
        File.WriteAllText(
            Path.Combine(workingDirectory, ".orchestrator-handoff.md"),
            "Compatibility pointer (v1, hash-bound; resolve from authoritative task evidence): " + pointer);

        var result = WorkerProfileDispatcher.PrepareTask(
            kernel,
            goal,
            task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(root, "prompts"),
            workingDirectory,
            DateTimeOffset.UtcNow,
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias);

        var section = Assert.Single(
            result.Task.LastDispatch!.ContextPackageReceipt!.Sections,
            item => item.LogicalIdentity == identity.Value);
        Assert.Equal(WorkerContextArtifact.Hash(bytes), section.ContentHash);
        Assert.Equal(
            bytes,
            File.ReadAllBytes(Path.Combine(
                workingDirectory,
                section.MandatoryRelativePath!.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Xunit.Fact]
    public void PrepareTask_RestoredLegacyVerification_RecoversValidatedOutputFileBeforeDispatch()
    {
        var root = CreateSeededDispatchRepository();
        var outputPath = Path.Combine(root, "legacy-planner.out.log");
        var authoritativeOutput = "legacy planner evidence\r\nwith final newline å\r\n";
        File.WriteAllText(outputPath, authoritativeOutput);
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan from legacy evidence.", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement the recovered plan.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Recover a legacy verification snapshot", [planner, developer]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Completed, "Legacy planner completed.");
        kernel.RecordTaskVerification(goal.Id, planner.Id, new TaskVerificationRecord(
            "legacy planner command",
            root,
            0,
            authoritativeOutput,
            string.Empty,
            DateTimeOffset.Parse("2026-08-15T12:00:00Z"),
            StandardOutputPath: outputPath,
            FullStandardOutput: authoritativeOutput,
            FullStandardError: string.Empty));

        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = Assert.Single(snapshot.Goals);
        var plannerSnapshot = goalSnapshot.Tasks.Single(task => task.Id == planner.Id.Value);
        var legacyVerification = plannerSnapshot.LastVerification! with
        {
            AuthoritativeStandardOutput = null,
            AuthoritativeStandardOutputUnavailableReason = null
        };
        var restoredKernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals =
            [
                goalSnapshot with
                {
                    Tasks = goalSnapshot.Tasks.Select(task => task.Id == planner.Id.Value
                        ? task with
                        {
                            LastVerification = legacyVerification,
                            VerificationHistory = [legacyVerification]
                        }
                        : task).ToArray()
                }
            ]
        });
        var restoredGoal = restoredKernel.GetGoal(goal.Id);
        var restoredPlanner = restoredGoal.Tasks.Single(task => task.Id == planner.Id);
        var restoredDeveloper = restoredGoal.Tasks.Single(task => task.Id == developer.Id);

        File.WriteAllText(outputPath, "changed after the legacy snapshot was recorded");
        WorkerCommandTemplate.WriteHandoffFile(restoredGoal.Tasks, restoredDeveloper.Id, root);
        var unavailableHandoff = File.ReadAllText(Path.Combine(root, ".orchestrator-handoff.md"));
        Assert.Contains("Legacy Verification Context (non-authoritative)", unavailableHandoff, StringComparison.Ordinal);
        Assert.Contains("legacy-snapshot-authoritative-output-unavailable", unavailableHandoff, StringComparison.Ordinal);
        var unavailablePointer = Assert.Single(new LegacyHandoffCompatibilityResolver(
                _ => null,
                root)
            .ResolveArtifactsFromMarkdown(unavailableHandoff));
        var unavailableContent = Encoding.UTF8.GetString(unavailablePointer.Bytes);
        Assert.Contains("authoritative: false", unavailableContent, StringComparison.Ordinal);
        Assert.Contains("legacy-snapshot-authoritative-output-unavailable", unavailableContent, StringComparison.Ordinal);
        Assert.Contains(authoritativeOutput, unavailableContent, StringComparison.Ordinal);
        Assert.DoesNotContain("changed after the legacy snapshot was recorded", unavailableContent, StringComparison.Ordinal);
        File.WriteAllText(outputPath, authoritativeOutput);

        var prepared = WorkerProfileDispatcher.PrepareTask(
            restoredKernel,
            restoredGoal,
            restoredDeveloper,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(root, "prompts"),
            root,
            DateTimeOffset.Parse("2026-08-15T12:01:00Z"),
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias);

        var identity = $"prior/{planner.Id.Value}/verification-output";
        var section = Assert.Single(prepared.Task.LastDispatch!.ContextPackageReceipt!.Sections,
            item => item.LogicalIdentity == identity);
        Assert.Equal(ContextDeliveryMode.MandatoryFile, section.DeliveryMode);
        Assert.Equal(WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes(authoritativeOutput)), section.ContentHash);
        Assert.Equal(
            Encoding.UTF8.GetBytes(authoritativeOutput),
            File.ReadAllBytes(Path.Combine(root, section.MandatoryRelativePath!.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Contains($"MANDATORY READ: identity={identity}", File.ReadAllText(prepared.PromptPath), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void WorkerVerificationEvidence_DoesNotPromoteBoundedLegacyPreviewWithoutHash()
    {
        var root = CreateTempDirectory();
        var outputPath = Path.Combine(root, "legacy-worker.out.log");
        var fullOutput = "head\n" + new string('x', VerificationTextBounds.BoundThreshold + 500) + "\ntail";
        File.WriteAllText(outputPath, fullOutput);
        var legacyVerification = new TaskVerificationRecord(
            "legacy worker command",
            root,
            0,
            VerificationTextBounds.BoundText(fullOutput, outputPath),
            string.Empty,
            DateTimeOffset.Parse("2026-08-15T12:00:00Z"),
            StandardOutputPath: outputPath,
            FullStandardOutputUnavailableReason: "legacy-snapshot-authoritative-output-unavailable",
            StandardOutputIsAuthoritative: false);

        Assert.False(WorkerVerificationEvidence.TryRecoverLegacySnapshotStandardOutput(
            legacyVerification,
            out _));

        var identity = new LogicalArtifactIdentity("prior/legacy/verification-output");
        var contextOutput = WorkerVerificationEvidence.ResolveStandardOutputForContext(legacyVerification, identity);
        Assert.False(contextOutput.IsAuthoritative);
        Assert.Equal("legacy-snapshot-authoritative-output-unavailable", contextOutput.UnavailableReason);
        Assert.Contains("authoritative: false", contextOutput.Content, StringComparison.Ordinal);
        Assert.Contains(legacyVerification.StandardOutput, contextOutput.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', VerificationTextBounds.BoundThreshold + 500), contextOutput.Content, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void WorkerVerificationEvidence_DeliversMarkedPreviewForCapturedUnavailabilityReason()
    {
        const string unavailableReason = "stdout-capture-failed";
        const string preview = "captured head and tail only";
        var verification = new TaskVerificationRecord(
            "worker command",
            CreateTempDirectory(),
            0,
            preview,
            string.Empty,
            DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
            FullStandardOutputUnavailableReason: unavailableReason,
            StandardOutputIsAuthoritative: false);

        var contextOutput = WorkerVerificationEvidence.ResolveStandardOutputForContext(
            verification,
            new LogicalArtifactIdentity("prior/captured/verification-output"));

        Assert.False(contextOutput.IsAuthoritative);
        Assert.Equal(unavailableReason, contextOutput.UnavailableReason);
        Assert.Contains($"unavailable-reason: {unavailableReason}", contextOutput.Content, StringComparison.Ordinal);
        Assert.Contains(preview, contextOutput.Content, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void WorkerVerificationEvidence_MissingAuthoritativeOutputWithoutReasonFailsClosed()
    {
        var verification = new TaskVerificationRecord(
            "worker command",
            CreateTempDirectory(),
            0,
            "bounded preview",
            string.Empty,
            DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
            StandardOutputIsAuthoritative: false);
        var identity = new LogicalArtifactIdentity("prior/invariant-break/verification-output");

        var failure = Assert.Throws<WorkerContextPreparationException>(() =>
            WorkerVerificationEvidence.ResolveStandardOutputForContext(verification, identity));

        Assert.Equal(identity, failure.Identity);
        Assert.Equal("authoritative-evidence-unavailable", failure.Reason);
    }

    [Xunit.Fact]
    public void PrepareTask_RestoredLongLegacyVerification_DeliversMarkedPreviewWithoutTrustingOutputFile()
    {
        var root = CreateSeededDispatchRepository();
        var outputPath = Path.Combine(root, "legacy-long-planner.out.log");
        var omittedMiddle = new string('x', VerificationTextBounds.BoundThreshold + 500);
        var fullOutput = $"legacy head{Environment.NewLine}{omittedMiddle}{Environment.NewLine}legacy tail";
        File.WriteAllText(outputPath, fullOutput);
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan from long legacy evidence.", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement with explicit legacy limitations.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Preserve legacy context without inventing authority", [planner, developer]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Completed, "Legacy planner completed.");
        kernel.RecordTaskVerification(goal.Id, planner.Id, new TaskVerificationRecord(
            "legacy planner command",
            root,
            0,
            fullOutput,
            string.Empty,
            DateTimeOffset.Parse("2026-08-15T12:00:00Z"),
            StandardOutputPath: outputPath,
            FullStandardOutput: fullOutput,
            FullStandardError: string.Empty));

        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = Assert.Single(snapshot.Goals);
        var plannerSnapshot = goalSnapshot.Tasks.Single(task => task.Id == planner.Id.Value);
        var legacyVerification = plannerSnapshot.LastVerification! with
        {
            AuthoritativeStandardOutput = null,
            AuthoritativeStandardOutputUnavailableReason = null
        };
        var restoredKernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals =
            [
                goalSnapshot with
                {
                    Tasks = goalSnapshot.Tasks.Select(task => task.Id == planner.Id.Value
                        ? task with
                        {
                            LastVerification = legacyVerification,
                            VerificationHistory = [legacyVerification]
                        }
                        : task).ToArray()
                }
            ]
        });
        var restoredGoal = restoredKernel.GetGoal(goal.Id);
        var restoredDeveloper = restoredGoal.Tasks.Single(task => task.Id == developer.Id);
        File.WriteAllText(
            outputPath,
            fullOutput.Replace(omittedMiddle, new string('y', omittedMiddle.Length), StringComparison.Ordinal));

        var prepared = WorkerProfileDispatcher.PrepareTask(
            restoredKernel,
            restoredGoal,
            restoredDeveloper,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(root, "prompts"),
            root,
            DateTimeOffset.Parse("2026-08-15T12:01:00Z"),
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias);

        var identity = $"prior/{planner.Id.Value}/verification-output";
        var section = Assert.Single(prepared.Task.LastDispatch!.ContextPackageReceipt!.Sections,
            item => item.LogicalIdentity == identity);
        var delivered = File.ReadAllText(Path.Combine(
            root,
            section.MandatoryRelativePath!.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(ContextDeliveryMode.MandatoryFile, section.DeliveryMode);
        Assert.Contains("authoritative: false", delivered, StringComparison.Ordinal);
        Assert.Contains("legacy-snapshot-authoritative-output-unavailable", delivered, StringComparison.Ordinal);
        Assert.Contains(legacyVerification.StandardOutput, delivered, StringComparison.Ordinal);
        Assert.DoesNotContain(omittedMiddle, delivered, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('y', omittedMiddle.Length), delivered, StringComparison.Ordinal);
        Assert.Contains($"MANDATORY READ: identity={identity}", File.ReadAllText(prepared.PromptPath), StringComparison.Ordinal);
        Assert.Null(restoredDeveloper.LastProcess);
    }

    [Xunit.Fact(DisplayName = "WorkerSkillSelector_routes_hyphenated_worker_skill_signal_in_isolation")]
    public void WorkerSkillSelectorRoutesHyphenatedWorkerSkillSignalInIsolation()
    {
        var workingDirectory = CreateTempDirectory();
        WriteSkill(workingDirectory, "skill-authoring");
        var task = new TaskSpec(
            TaskId.New(),
            "Document hyphenated worker-skill behavior.",
            AgentRole.Ideation);
        var goal = new AgentOrchestratorKernel().CreateGoal("Maintain procedural catalog", [task]);

        var selected = new WorkerSkillSelector().SelectSkillRequirements(goal, task, workingDirectory);

        var skill = Assert.Single(selected);
        Assert.Equal("skill-authoring", skill.Name);
        Assert.True(skill.Available);
    }

    [Xunit.Fact(DisplayName = "WorkerSkillSelector_routes_systematic_debugging_at_criterion_retry_two_only")]
    public void WorkerSkillSelectorRoutesSystematicDebuggingAtCriterionRetryTwoOnly()
{
    var workingDirectory = CreateTempDirectory();
    WriteSkill(workingDirectory, "dotnet-windows-build-hygiene");
    WriteSkill(workingDirectory, "verification-before-completion");
    WriteSkill(workingDirectory, "systematic-debugging");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Repair the parser defect.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Repair parser behavior", [task]);

    var initial = new WorkerSkillSelector().SelectSkillRequirements(goal, task, workingDirectory);
    Assert.Contains(initial, skill => skill.Name == "verification-before-completion" && skill.Available);
    Assert.DoesNotContain(initial, skill => skill.Name == "systematic-debugging");

    var snapshot = kernel.ExportSnapshot();
    var goalSnapshot = snapshot.Goals.Single();
    var transientRetryKernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
    {
        Goals =
        [
            goalSnapshot with
            {
                Tasks = goalSnapshot.Tasks
                    .Select(item => item.Id == task.Id.Value ? item with { EmptyOutputRetryCount = 3 } : item)
                    .ToArray()
            }
        ]
    });
    var transientRetryGoal = transientRetryKernel.GetGoal(goal.Id);
    var transientRetryTask = transientRetryGoal.Tasks.Single(item => item.Id == task.Id);
    Assert.DoesNotContain(
        new WorkerSkillSelector().SelectSkillRequirements(transientRetryGoal, transientRetryTask, workingDirectory),
        skill => skill.Name == "systematic-debugging");

    kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["First criterion miss."]);
    var firstRetry = new WorkerSkillSelector().SelectSkillRequirements(goal, task, workingDirectory);
    Assert.DoesNotContain(firstRetry, skill => skill.Name == "systematic-debugging");

    kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["Second criterion miss."]);
    var secondRetry = new WorkerSkillSelector().SelectSkillRequirements(goal, task, workingDirectory);
    Assert.Contains(secondRetry, skill => skill.Name == "systematic-debugging" && skill.Available);

    var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory);
    var selectedSkills = File.ReadAllText(Path.Combine(contextDirectory, "selected-skills.md"));
    var packagedSkills = File.ReadAllText(Path.Combine(contextDirectory, "packages", task.Id.Value, "selected-skills.md"));
    Assert.Contains("systematic-debugging", selectedSkills, StringComparison.Ordinal);
    Assert.Contains("Status: available", selectedSkills, StringComparison.Ordinal);
    Assert.Equal(selectedSkills, packagedSkills);
}

    [Xunit.Fact(DisplayName = "WorkerSkillSelector_routes_only_task_specific_current_acceptance_failure_retries")]
    public void WorkerSkillSelectorRoutesOnlyTaskSpecificCurrentAcceptanceFailureRetries()
{
    var workingDirectory = CreateTempDirectory();
    WriteSkill(workingDirectory, "dotnet-windows-build-hygiene");
    WriteSkill(workingDirectory, "verification-before-completion");
    WriteSkill(workingDirectory, "systematic-debugging");
    var clock = new MutableClock(DateTimeOffset.Parse("2026-08-10T12:00:00Z"));
    var kernel = new AgentOrchestratorKernel(clock);
    var target = new TaskSpec(TaskId.New(), "Repair the target defect.", AgentRole.Developer);
    var other = new TaskSpec(TaskId.New(), "Repair another defect.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Repair parser behavior", [target, other]);

    kernel.RetryTask(goal.Id, target.Id, "Retry before acceptance failure.", invalidateDownstream: false);
    clock.Advance();
    kernel.RecordAcceptanceFailure(goal.Id, ["ParserTests.Target"]);
    Assert.DoesNotContain(
        new WorkerSkillSelector().SelectSkillRequirements(goal, target, workingDirectory),
        skill => skill.Name == "systematic-debugging");

    clock.Advance();
    kernel.RetryTask(goal.Id, other.Id, "Retry unrelated task.", invalidateDownstream: false);
    Assert.DoesNotContain(
        new WorkerSkillSelector().SelectSkillRequirements(goal, target, workingDirectory),
        skill => skill.Name == "systematic-debugging");

    clock.Advance();
    kernel.RecordAcceptanceFailure(goal.Id, ["ParserTests.NewTarget"]);
    clock.Advance();
    kernel.RetryTask(goal.Id, target.Id, "Retry target after latest failure.", invalidateDownstream: false);
    Assert.Contains(
        new WorkerSkillSelector().SelectSkillRequirements(goal, target, workingDirectory),
        skill => skill.Name == "systematic-debugging" && skill.Available);
    Assert.DoesNotContain(
        new WorkerSkillSelector().SelectSkillRequirements(goal, other, workingDirectory),
        skill => skill.Name == "systematic-debugging");
}

    [Xunit.Theory(DisplayName = "WorkerSkillSelector_omits_new_developer_procedures_for_other_roles")]
    [Xunit.InlineData(AgentRole.Planner)]
    [Xunit.InlineData(AgentRole.Researcher)]
    [Xunit.InlineData(AgentRole.Tester)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void WorkerSkillSelectorOmitsNewDeveloperProceduresForOtherRoles(AgentRole role)
{
    var workingDirectory = CreateTempDirectory();
    var task = new TaskSpec(TaskId.New(), "Inspect parser behavior.", role);
    var goal = new AgentOrchestratorKernel().CreateGoal("Inspect parser behavior", [task]);

    var selected = new WorkerSkillSelector().SelectSkillRequirements(goal, task, workingDirectory);

    Assert.DoesNotContain(selected, skill => skill.Name == "verification-before-completion");
    Assert.DoesNotContain(selected, skill => skill.Name == "systematic-debugging");
}

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_selects_different_skill_manifests_for_tasks_in_same_goal")]
    public void WorkerContextArtifactsSelectsDifferentSkillManifestsForTasksInSameGoal()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    WriteSkill(workingDirectory, "dotnet-windows-build-hygiene");
    WriteSkill(workingDirectory, "orchestrator-dogfood");
    WriteSkill(workingDirectory, "orchestrator-worker-verification");
    WriteSkill(workingDirectory, "aspnet-core");
    WriteSkill(workingDirectory, "playwright");
    WriteSkill(workingDirectory, "verification-before-completion");
    var kernel = new AgentOrchestratorKernel();
    var implementation = new TaskSpec(
        TaskId.New(),
        "Implement an ASP.NET Core dashboard UI workflow with Playwright coverage.",
        AgentRole.Developer,
        "Run dotnet test and a dashboard UI smoke.");
    var review = new TaskSpec(
        TaskId.New(),
        "Review worker result contract evidence.",
        AgentRole.Reviewer,
        "Inspect verification records and dispatch logs.");
    var goal = kernel.CreateGoal("Improve dashboard worker routing", [implementation, review]);

    var contextDirectory = WorkerContextArtifacts.Write(goal, implementation, workingDirectory);
    var implementationSkills = File.ReadAllText(Path.Combine(contextDirectory, "selected-skills.md"));
    WorkerContextArtifacts.Write(goal, review, workingDirectory);
    var reviewSkills = File.ReadAllText(Path.Combine(contextDirectory, "selected-skills.md"));
    var implementationPackageSkills = File.ReadAllText(Path.Combine(contextDirectory, "packages", implementation.Id.Value, "selected-skills.md"));
    var reviewPackageSkills = File.ReadAllText(Path.Combine(contextDirectory, "packages", review.Id.Value, "selected-skills.md"));

    Assert.Contains("dotnet-windows-build-hygiene", implementationSkills, StringComparison.Ordinal);
    Assert.Contains("aspnet-core", implementationSkills, StringComparison.Ordinal);
    Assert.Contains("playwright", implementationSkills, StringComparison.Ordinal);
    Assert.Contains("orchestrator-worker-verification", reviewSkills, StringComparison.Ordinal);
    Assert.False(reviewSkills.Contains("aspnet-core", StringComparison.Ordinal));
    Assert.False(reviewSkills.Contains("playwright", StringComparison.Ordinal));
    Assert.False(string.Equals(implementationSkills, reviewSkills, StringComparison.Ordinal));
    Assert.Equal(implementationSkills, implementationPackageSkills);
    Assert.Equal(reviewSkills, reviewPackageSkills);
}

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_writes_source_survey_and_diff_summary_artifacts")]
    public void WorkerContextArtifactsWritesSourceSurveyAndDiffSummaryArtifacts()
{
    var workingDirectory = CreateSeededDispatchRepository();
    Directory.CreateDirectory(Path.Combine(workingDirectory, "src", "Feature"));
    Directory.CreateDirectory(Path.Combine(workingDirectory, "tests", "Feature.Tests"));
    Directory.CreateDirectory(Path.Combine(workingDirectory, "src", "Feature", "bin"));
    File.WriteAllText(Path.Combine(workingDirectory, "src", "Feature", "FeatureService.cs"), "public sealed class FeatureService {}");
    File.WriteAllText(Path.Combine(workingDirectory, "tests", "Feature.Tests", "FeatureServiceTests.cs"), "public sealed class FeatureServiceTests {}");
    File.WriteAllText(Path.Combine(workingDirectory, "src", "Feature", "bin", "Generated.cs"), "generated");
    RunGit(workingDirectory, ["add", "-A"], DateTimeOffset.Parse("2026-01-01T00:01:00Z"));
    RunGit(workingDirectory, ["commit", "-m", "Add feature source"], DateTimeOffset.Parse("2026-01-01T00:01:00Z"));
    File.WriteAllText(Path.Combine(workingDirectory, "src", "Feature", "FeatureService.cs"), "public sealed class FeatureService { public int Version => 2; }");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(
        TaskId.New(),
        "Update FeatureService behavior and tests.",
        AgentRole.Developer,
        "Run focused FeatureService tests.");
    var goal = kernel.CreateGoal("Improve FeatureService source survey context", [task]);

    var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory);

    var sourceSurvey = File.ReadAllText(Path.Combine(contextDirectory, "source-survey.md"));
    var diffSummary = File.ReadAllText(Path.Combine(contextDirectory, "diff-summary.md"));
    using var registryDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(contextDirectory, "artifact-registry.json")));
    var artifacts = registryDocument.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
    Assert.Contains("src/Feature/FeatureService.cs", sourceSurvey, StringComparison.Ordinal);
    Assert.Contains("tests/Feature.Tests/FeatureServiceTests.cs", sourceSurvey, StringComparison.Ordinal);
    Assert.Contains("## Likely Tests", sourceSurvey, StringComparison.Ordinal);
    Assert.Contains("## Public API Symbols", sourceSurvey, StringComparison.Ordinal);
    Assert.Contains("public class FeatureService", sourceSurvey, StringComparison.Ordinal);
    Assert.Contains("## Call-Site Hints", sourceSurvey, StringComparison.Ordinal);
    Assert.Contains("FeatureService.cs: featureservice", sourceSurvey, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("## Ownership Hints", sourceSurvey, StringComparison.Ordinal);
    Assert.Contains("src/Feature: production source", sourceSurvey, StringComparison.Ordinal);
    Assert.Contains("tests/Feature.Tests: test source", sourceSurvey, StringComparison.Ordinal);
    Assert.Contains("Regeneration: generated at dispatch preparation", sourceSurvey, StringComparison.Ordinal);
    Assert.False(sourceSurvey.Contains("bin/Generated.cs", StringComparison.Ordinal));
    Assert.Contains("src/Feature/FeatureService.cs", diffSummary, StringComparison.Ordinal);
    Assert.Contains("Regeneration: generated at dispatch preparation", diffSummary, StringComparison.Ordinal);
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "source-survey.md"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "diff-summary.md"));
}

    [Xunit.Fact(DisplayName = "Reviewer_dispatch_prompt_uses_merge_base_changed_file_scope")]
    public void ReviewerDispatchPromptUsesMergeBaseChangedFileScope()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-07-15T18:50:00Z");
    var kernel = new AgentOrchestratorKernel();
    var reviewer = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Review merge-base changed-file scope", [reviewer]);
    var agent = new AgentDefinition(
        new AgentId("reviewer"),
        "Reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    WriteSkill(worktree, "orchestrator-worker-verification");
    Directory.CreateDirectory(Path.Combine(worktree, "src", "Feature"));
    File.WriteAllText(Path.Combine(worktree, "src", "Feature", "GoalFeature.cs"), "public sealed class GoalFeature {}");
    RunGit(worktree, ["add", "src/Feature/GoalFeature.cs"], DateTimeOffset.Parse("2026-07-15T18:51:00Z"));
    RunGit(worktree, ["commit", "-m", "Add goal feature"], DateTimeOffset.Parse("2026-07-15T18:51:00Z"));
    File.WriteAllText(Path.Combine(root, "main-only.txt"), "main advanced after goal branch");
    RunGit(root, ["add", "main-only.txt"], DateTimeOffset.Parse("2026-07-15T18:52:00Z"));
    RunGit(root, ["commit", "-m", "Advance main only"], DateTimeOffset.Parse("2026-07-15T18:52:00Z"));

    var result = WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        reviewer,
        [agent],
        DispatchTestProfiles(),
        promptRoot,
        worktree,
        dispatchedAt,
        sandboxOptions: DisabledSandbox);

    var prompt = File.ReadAllText(result.PromptPath);
    Assert.Contains("## Reviewer Changed-File Scope", prompt, StringComparison.Ordinal);
    Assert.Contains("### 1. Spec compliance (do this first)", prompt, StringComparison.Ordinal);
    Assert.Contains("### 2. Code quality (only after section 1)", prompt, StringComparison.Ordinal);
    Assert.Contains("criteria_verdicts:", prompt, StringComparison.Ordinal);
    Assert.Contains("complete candidate diff supplied for the current round", prompt, StringComparison.Ordinal);
    Assert.Contains("enumerate every blocking finding", prompt, StringComparison.Ordinal);
    Assert.Contains("no other blocking findings exist in this diff", prompt, StringComparison.Ordinal);
    Assert.Contains("git diff --name-only main...HEAD", prompt, StringComparison.Ordinal);
    Assert.Contains("git diff main...HEAD", prompt, StringComparison.Ordinal);
    Assert.Contains("Merge-tree status: clean against current main", prompt, StringComparison.Ordinal);
    Assert.Contains("branch-behind-main alone is NOT a blocker", prompt, StringComparison.Ordinal);
    Assert.Contains("src/Feature/GoalFeature.cs", prompt, StringComparison.Ordinal);
    Assert.DoesNotContain("main-only.txt", prompt, StringComparison.Ordinal);
    Assert.Contains("Do not use two-dot diffs", prompt, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "Reviewer_dispatch_prompt_injects_conflicted_merge_tree_paths")]
    public void ReviewerDispatchPromptInjectsConflictedMergeTreePaths()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-07-15T19:02:00Z");
    var kernel = new AgentOrchestratorKernel();
    var reviewer = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Review merge-tree conflicts", [reviewer]);
    var agent = new AgentDefinition(
        new AgentId("reviewer"),
        "Reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    WriteSkill(worktree, "orchestrator-worker-verification");
    File.WriteAllText(Path.Combine(worktree, "seed.txt"), "goal branch content");
    RunGit(worktree, ["add", "seed.txt"], DateTimeOffset.Parse("2026-07-15T19:03:00Z"));
    RunGit(worktree, ["commit", "-m", "Change seed on goal branch"], DateTimeOffset.Parse("2026-07-15T19:03:00Z"));
    File.WriteAllText(Path.Combine(root, "seed.txt"), "main branch content");
    RunGit(root, ["add", "seed.txt"], DateTimeOffset.Parse("2026-07-15T19:04:00Z"));
    RunGit(root, ["commit", "-m", "Change seed on main"], DateTimeOffset.Parse("2026-07-15T19:04:00Z"));

    var result = WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        reviewer,
        [agent],
        DispatchTestProfiles(),
        promptRoot,
        worktree,
        dispatchedAt,
        sandboxOptions: DisabledSandbox);

    var prompt = File.ReadAllText(result.PromptPath);
    Assert.Contains("Merge-tree status: conflicted against current main", prompt, StringComparison.Ordinal);
    Assert.Contains("Conflicting paths: 1; showing 1.", prompt, StringComparison.Ordinal);
    Assert.Contains("- conflict: seed.txt", prompt, StringComparison.Ordinal);
    Assert.Contains("Staleness may block only with concrete integration-risk evidence", prompt, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "ProfileDispatchTask_reviewer_uses_merge_base_changed_file_scope")]
    public void ProfileDispatchTaskReviewerUsesMergeBaseChangedFileScope()
{
    var root = CreateSeededDispatchRepository();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var reviewer = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Review profile dispatch changed-file scope", [reviewer]);
    kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
        "Review profile dispatch changed-file scope",
        ["Reviewer profile dispatch includes authoritative merge-base changed-file scope."],
        VerificationClass.TestVerifiable,
        [],
        []));
    var agent = new AgentDefinition(
        new AgentId("reviewer"),
        "Reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    WriteSkill(worktree, "orchestrator-worker-verification");
    Directory.CreateDirectory(Path.Combine(worktree, "src", "Feature"));
    File.WriteAllText(Path.Combine(worktree, "src", "Feature", "ProfileGoalFeature.cs"), "public sealed class ProfileGoalFeature {}");
    RunGit(worktree, ["add", "src/Feature/ProfileGoalFeature.cs"], DateTimeOffset.Parse("2026-07-15T18:55:00Z"));
    RunGit(worktree, ["commit", "-m", "Add profile goal feature"], DateTimeOffset.Parse("2026-07-15T18:55:00Z"));
    File.WriteAllText(Path.Combine(root, "main-profile-only.txt"), "main advanced after goal branch");
    RunGit(root, ["add", "main-profile-only.txt"], DateTimeOffset.Parse("2026-07-15T18:56:00Z"));
    RunGit(root, ["commit", "-m", "Advance main for profile dispatch"], DateTimeOffset.Parse("2026-07-15T18:56:00Z"));

    var result = GoalManagementCommandService.ProfileDispatchTask(
        kernel,
        workspace,
        goal,
        reviewer,
        DispatchTestProfiles().GetRequired("codex-cli"),
        [agent],
        sandboxOptions: DisabledSandbox);

    var prompt = File.ReadAllText(result.PromptPath);
    Assert.Contains("## Reviewer Changed-File Scope", prompt, StringComparison.Ordinal);
    Assert.Contains("git diff --name-only main...HEAD", prompt, StringComparison.Ordinal);
    Assert.Contains("git diff main...HEAD", prompt, StringComparison.Ordinal);
    Assert.Contains("src/Feature/ProfileGoalFeature.cs", prompt, StringComparison.Ordinal);
    Assert.DoesNotContain("main-profile-only.txt", prompt, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "PrepareReadyTasks_reviewer_uses_merge_base_changed_file_scope")]
    public void PrepareReadyTasksReviewerUsesMergeBaseChangedFileScope()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-07-15T18:57:00Z");
    var kernel = new AgentOrchestratorKernel();
    var reviewer = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Review ready batch changed-file scope", [reviewer]);
    kernel.ActivateGoal(goal.Id, [new AgentDefinition(
        new AgentId("reviewer"),
        "Reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey))]);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    WriteSkill(worktree, "orchestrator-worker-verification");
    Directory.CreateDirectory(Path.Combine(worktree, "src", "Feature"));
    File.WriteAllText(Path.Combine(worktree, "src", "Feature", "ReadyGoalFeature.cs"), "public sealed class ReadyGoalFeature {}");
    RunGit(worktree, ["add", "src/Feature/ReadyGoalFeature.cs"], DateTimeOffset.Parse("2026-07-15T18:58:00Z"));
    RunGit(worktree, ["commit", "-m", "Add ready goal feature"], DateTimeOffset.Parse("2026-07-15T18:58:00Z"));
    File.WriteAllText(Path.Combine(root, "main-ready-only.txt"), "main advanced after goal branch");
    RunGit(root, ["add", "main-ready-only.txt"], DateTimeOffset.Parse("2026-07-15T18:59:00Z"));
    RunGit(root, ["commit", "-m", "Advance main for ready dispatch"], DateTimeOffset.Parse("2026-07-15T18:59:00Z"));
    var profile = new WorkerProfile("codex-cli", "codex exec --sandbox read-only --cd {workingDirectory}");

    var results = WorkerProfileDispatcher.PrepareReadyTasks(
        kernel,
        goal,
        profile,
        promptRoot,
        worktree,
        dispatchedAt,
        sandboxOptions: DisabledSandbox);

    var result = Assert.Single(results);
    var prompt = File.ReadAllText(result.PromptPath);
    Assert.Contains("## Reviewer Changed-File Scope", prompt, StringComparison.Ordinal);
    Assert.Contains("src/Feature/ReadyGoalFeature.cs", prompt, StringComparison.Ordinal);
    Assert.DoesNotContain("main-ready-only.txt", prompt, StringComparison.Ordinal);
    Assert.Contains("Do not use two-dot diffs", prompt, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "PrepareReadyTasks_reviewer_fails_when_merge_base_scope_unavailable")]
    public void PrepareReadyTasksReviewerFailsWhenMergeBaseScopeUnavailable()
{
    var root = CreateSeededDispatchRepository();
    RunGit(root, ["branch", "-m", "not-main"], DateTimeOffset.Parse("2026-07-15T19:00:00Z"));
    var promptRoot = Path.Combine(root, "prompts");
    var kernel = new AgentOrchestratorKernel();
    var reviewer = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Review ready missing main failure", [reviewer]);
    kernel.ActivateGoal(goal.Id, [new AgentDefinition(
        new AgentId("reviewer"),
        "Reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey))]);
    var profile = new WorkerProfile("codex-cli", "codex exec --sandbox read-only --cd {workingDirectory}");

    var ex = Assert.Throws<WorkerSubscriptionPreflightException>(() => WorkerProfileDispatcher.PrepareReadyTasks(
        kernel,
        goal,
        profile,
        promptRoot,
        root,
        DateTimeOffset.Parse("2026-07-15T19:01:00Z"),
        sandboxOptions: DisabledSandbox));

    Assert.Equal(WorkerProfileDispatcher.ReviewerScopeUnavailableErrorCode, ex.ErrorCode);
    Assert.Contains(ex.Findings, finding => finding.Contains("git ref 'main' could not be resolved", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "PrepareTask_reviewer_fails_with_typed_merge_tree_unavailable")]
    public void PrepareTaskReviewerFailsWithTypedMergeTreeUnavailable()
{
    var root = CreateSeededDispatchRepository();
    RunGit(root, ["branch", "-m", "not-main"], DateTimeOffset.Parse("2026-07-15T19:05:00Z"));
    var promptRoot = Path.Combine(root, "prompts");
    var kernel = new AgentOrchestratorKernel();
    var reviewer = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Review missing merge-tree main failure", [reviewer]);
    var agent = new AgentDefinition(
        new AgentId("reviewer"),
        "Reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var profile = new WorkerProfile("codex-cli", "codex exec --sandbox read-only --cd {workingDirectory}");

    var ex = Assert.Throws<WorkerSubscriptionPreflightException>(() => WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        reviewer,
        profile,
        promptRoot,
        root,
        DateTimeOffset.Parse("2026-07-15T19:06:00Z"),
        reviewerScopeChangedFiles: ["seed.txt"],
        reviewerScopeMergeBase: "0000000000000000000000000000000000000000",
        reviewerScopeTotalChangedFileCount: 1,
        sandboxOptions: DisabledSandbox));

    Assert.Equal(WorkerProfileDispatcher.ReviewerMergeTreeUnavailableErrorCode, ex.ErrorCode);
    Assert.Contains(ex.Findings, finding => finding.Contains("git merge-tree --write-tree --name-only main HEAD failed", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "Reviewer_dispatch_preflight_fails_when_merge_base_scope_unavailable")]
    public void ReviewerDispatchPreflightFailsWhenMergeBaseScopeUnavailable()
{
    var root = CreateSeededDispatchRepository();
    RunGit(root, ["branch", "-m", "not-main"], DateTimeOffset.Parse("2026-07-15T18:53:00Z"));
    WriteSkill(root, "orchestrator-worker-verification");
    var promptRoot = Path.Combine(root, "prompts");
    var kernel = new AgentOrchestratorKernel();
    var reviewer = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Review missing main failure", [reviewer]);
    var agent = new AgentDefinition(
        new AgentId("reviewer"),
        "Reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"));
    kernel.ActivateGoal(goal.Id, [agent]);

    var ex = Assert.Throws<WorkerSubscriptionPreflightException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        reviewer,
        [agent],
        DispatchTestProfiles(),
        promptRoot,
        root,
        DateTimeOffset.Parse("2026-07-15T18:54:00Z"),
        sandboxOptions: DisabledSandbox));

    Assert.Equal(WorkerProfileDispatcher.ReviewerScopeUnavailableErrorCode, ex.ErrorCode);
    Assert.Contains(ex.Findings, finding => finding.Contains("git ref 'main' could not be resolved", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_writes_deterministic_verification_checklist_for_reviewer")]
    public void WorkerContextArtifactsWritesDeterministicVerificationChecklistForReviewer()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(Path.Combine(workingDirectory, "config"));
    File.WriteAllText(Path.Combine(workingDirectory, "config", "acceptance-manifest.json"), "{}");
    var kernel = new AgentOrchestratorKernel();
    var developer = new TaskSpec(TaskId.New(), "Implement deterministic review checklist.", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Run verification.", AgentRole.Tester);
    var reviewer = new TaskSpec(TaskId.New(), "Review deterministic evidence.", AgentRole.Reviewer, "Review deterministic-verification.md first.");
    var goal = kernel.CreateGoal("Review with deterministic evidence", [developer, tester, reviewer]);
    kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
        "codex exec prompt",
        workingDirectory,
        0,
        "Implemented." + Environment.NewLine + WorkerResultBlock("src/Feature.cs, bin/generated.dll", "dotnet test", "Passed: 1", "abc123"),
        string.Empty,
        DateTimeOffset.UtcNow,
        "Model fit: OpenAI/gpt-5.5 - adequate - implementation fixture.")); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
    kernel.ReportTaskProgress(goal.Id, tester.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, tester.Id, new TaskVerificationRecord(
        "dotnet test",
        workingDirectory,
        0,
        "Passed: 1",
        string.Empty,
        DateTimeOffset.UtcNow));

    var contextDirectory = WorkerContextArtifacts.Write(goal, reviewer, workingDirectory);

    var checklist = File.ReadAllText(Path.Combine(contextDirectory, "deterministic-verification.md"));
    var manifest = File.ReadAllText(Path.Combine(contextDirectory, "manifest.md"));
    Assert.Contains("Acceptance manifest: present: config/acceptance-manifest.json", checklist, StringComparison.Ordinal);
    Assert.Contains("prior Developer verification passed", checklist, StringComparison.Ordinal);
    Assert.Contains("reported WORKER_RESULT contract", checklist, StringComparison.Ordinal);
    Assert.Contains("reported generated path changes: bin/generated.dll", checklist, StringComparison.Ordinal);
    Assert.Contains("prior Tester task", checklist, StringComparison.Ordinal);
    Assert.Contains("no model-fit evidence", checklist, StringComparison.Ordinal);
    Assert.Contains("## Test Impact Plan", checklist, StringComparison.Ordinal);
    Assert.Contains("deterministic-verification.md", manifest, StringComparison.Ordinal);
    Assert.Contains("check deterministic failures", manifest, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_writes_unmet_acceptance_criterion_retry_feedback")]
    public void WorkerContextArtifactsWritesUnmetAcceptanceCriterionRetryFeedback()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(
        TaskId.New(),
        "Fix the acceptance criterion miss.",
        AgentRole.Developer,
        "Run focused acceptance retry tests.");
    var goal = kernel.CreateGoal("Retry with deterministic criterion feedback", [task]);
    var completeFailure =
        $"[FAIL] Mcg.AgentOrchestrator.Infrastructure.Tests.CompleteRetryEvidence (Failed){Environment.NewLine}" +
        $"Assert.Equal() Failure: Values differ{Environment.NewLine}" +
        $"Expected: 2{Environment.NewLine}Actual:   0{Environment.NewLine}" +
        $"{new string('x', 1300)}-message-end{Environment.NewLine}" +
        "at Mcg.AgentOrchestrator.Infrastructure.Tests.CompleteRetryEvidence() in CompleteRetryEvidence.cs:line 42";
    kernel.RecordCriterionRetryFeedback(
        goal.Id,
        task.Id,
        [completeFailure]);

    var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory);

    var currentTask = File.ReadAllText(Path.Combine(contextDirectory, "current-task.md"));
    Assert.Contains("## Unmet acceptance criteria from the prior attempt - fix these:", currentTask, StringComparison.Ordinal);
    Assert.Contains(completeFailure, currentTask, StringComparison.Ordinal);
    Assert.Contains("Expected: 2", currentTask, StringComparison.Ordinal);
    Assert.Contains("Actual:   0", currentTask, StringComparison.Ordinal);
    Assert.Contains("CompleteRetryEvidence.cs:line 42", currentTask, StringComparison.Ordinal);
    Assert.DoesNotContain("...[truncated", currentTask, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_puts_latest_acceptance_failure_before_context_digest_on_retry")]
    public void BuildTaskBriefPutsLatestAcceptanceFailureBeforeContextDigestOnRetry()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    var contextDirectory = Path.Combine(root, "context");
    Directory.CreateDirectory(workingDirectory);
    Directory.CreateDirectory(contextDirectory);
    var clock = new MutableClock(DateTimeOffset.Parse("2026-06-26T12:00:00Z"));
    var kernel = new AgentOrchestratorKernel(clock);
    var task = new TaskSpec(TaskId.New(), "Fix acceptance-failed schema assertions.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Retry after acceptance failure", [task]);
    kernel.ActivateGoal(goal.Id, [new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey))]);

    var firstAttempt = kernel.BuildTaskBrief(
        goal.Id,
        task.Id,
        workingDirectory: workingDirectory,
        contextDirectory: contextDirectory).Content;
    Assert.DoesNotContain("ACCEPTANCE FAILURE", firstAttempt, StringComparison.Ordinal);

    kernel.RecordAcceptanceFailure(goal.Id, [
        "OldSchemaTests.OldFailure"
    ]);
    clock.Advance();
    kernel.RetryTask(goal.Id, task.Id, "old operator feedback");
    clock.Advance();
    kernel.RecordAcceptanceFailure(goal.Id, [
        "SqliteOrchestratorStateRepositoryTests.Loads_existing_goal_schema",
        "SqliteOrchestratorStateRepositoryTests.Saves_goal_schema_columns"
    ]);
    clock.Advance();
    var operatorFeedback = "Operator rejection: acceptance failed on sqlite schema assertions; fix the schema mapping before reporting complete.";
    kernel.RetryTask(goal.Id, task.Id, operatorFeedback);

    var retryPrompt = kernel.BuildTaskBrief(
        goal.Id,
        task.Id,
        workingDirectory: workingDirectory,
        contextDirectory: contextDirectory).Content;

    var failureStart = retryPrompt.IndexOf("<!-- ACCEPTANCE_FAILURE_START -->", StringComparison.Ordinal);
    var instructions = retryPrompt.IndexOf("## Instructions", StringComparison.Ordinal);
    var contextPointer = retryPrompt.IndexOf("Context files:", StringComparison.Ordinal);
    Assert.True(failureStart >= 0, retryPrompt);
    Assert.True(failureStart < contextPointer, retryPrompt);
    Assert.True(failureStart < instructions, retryPrompt);
    Assert.True(retryPrompt.Contains("<!-- ACCEPTANCE_FAILURE_END -->", StringComparison.Ordinal), retryPrompt);
    Assert.True(retryPrompt.Contains(operatorFeedback, StringComparison.Ordinal), retryPrompt);
    Assert.True(retryPrompt.Contains("SqliteOrchestratorStateRepositoryTests.Loads_existing_goal_schema", StringComparison.Ordinal), retryPrompt);
    Assert.True(retryPrompt.Contains("SqliteOrchestratorStateRepositoryTests.Saves_goal_schema_columns", StringComparison.Ordinal), retryPrompt);
    var failureEnd = retryPrompt.IndexOf("<!-- ACCEPTANCE_FAILURE_END -->", StringComparison.Ordinal);
    var failureBlock = retryPrompt[failureStart..failureEnd];
    Assert.DoesNotContain("OldSchemaTests.OldFailure", failureBlock, StringComparison.Ordinal);
    Assert.DoesNotContain("old operator feedback", failureBlock, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_puts_accumulated_retry_feedback_before_prior_branch_and_digest_context")]
    public void BuildTaskBriefPutsAccumulatedRetryFeedbackBeforePriorBranchAndDigestContext()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    var contextDirectory = Path.Combine(root, "context");
    Directory.CreateDirectory(workingDirectory);
    Directory.CreateDirectory(contextDirectory);
    var clock = new MutableClock(DateTimeOffset.Parse("2026-06-28T12:00:00Z"));
    var kernel = new AgentOrchestratorKernel(clock);
    var planner = new TaskSpec(TaskId.New(), "Plan retry prompt construction.", AgentRole.Planner);
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt construction.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Make Developer retry feedback first class", [planner, developer]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    kernel.RecordTaskVerification(goal.Id, planner.Id, new TaskVerificationRecord(
        "codex exec planner",
        workingDirectory,
        0,
        "prior summary text: existing branch looked clean",
        string.Empty,
        clock.UtcNow));
    kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Completed, "Planner completed.");
    kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec old-focused-tests.md",
        workingDirectory,
        clock.UtcNow));
    kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
        "dotnet test --filter OldFocusedTests",
        workingDirectory,
        1,
        "old focused tests failed",
        string.Empty,
        clock.UtcNow));
    kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Failed, "Developer attempt failed.");
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "Old retry reason for prior history.");
    clock.Advance();
    var exactBlocker = "Reviewer blocker: src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs method PrepareSubscriptionTask still buries RetryTask feedback after context digest.";
    kernel.RetryTask(goal.Id, developer.Id, exactBlocker);

    var prompt = kernel.BuildTaskBrief(
        goal.Id,
        developer.Id,
        workingDirectory: workingDirectory,
        contextDirectory: contextDirectory,
        targetBranchName: "goal/41c6e2a2",
        targetHeadCommit: "abcdef123456").Content;

    var retryStart = prompt.IndexOf("<!-- ACCUMULATED_RETRY_FEEDBACK_START -->", StringComparison.Ordinal);
    var retryEnd = prompt.IndexOf("<!-- ACCUMULATED_RETRY_FEEDBACK_END -->", StringComparison.Ordinal);
    Assert.True(retryStart >= 0, prompt);
    Assert.True(retryEnd > retryStart, prompt);
    Assert.True(retryStart < prompt.IndexOf("Goal:", StringComparison.Ordinal), prompt);
    Assert.True(retryStart < prompt.IndexOf("Context files:", StringComparison.Ordinal), prompt);
    Assert.True(retryStart < prompt.IndexOf("Current target context:", StringComparison.Ordinal), prompt);
    Assert.True(retryStart < prompt.IndexOf("## Prior Task Evidence", StringComparison.Ordinal), prompt);
    Assert.True(retryStart < prompt.IndexOf("## Recent Timeline", StringComparison.Ordinal), prompt);
    Assert.Contains(exactBlocker, prompt, StringComparison.Ordinal);

    var retryBlock = prompt[retryStart..retryEnd];
    Assert.Contains(exactBlocker, retryBlock, StringComparison.Ordinal);
    Assert.Contains("src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs", retryBlock, StringComparison.Ordinal);
    Assert.Contains("PrepareSubscriptionTask", retryBlock, StringComparison.Ordinal);
    Assert.Contains("- Branch: goal/41c6e2a2", retryBlock, StringComparison.Ordinal);
    Assert.Contains("- HEAD commit: abcdef123456", retryBlock, StringComparison.Ordinal);
    Assert.Contains("Old retry reason for prior history.", retryBlock, StringComparison.Ordinal);
    Assert.Contains("[superseded] Retry 1 of 2", retryBlock, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_omits_developer_retry_blocker_on_first_attempt")]
    public void BuildTaskBriefOmitsDeveloperRetryBlockerOnFirstAttempt()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    var contextDirectory = Path.Combine(root, "context");
    Directory.CreateDirectory(workingDirectory);
    Directory.CreateDirectory(contextDirectory);
    var kernel = new AgentOrchestratorKernel();
    var developer = new TaskSpec(TaskId.New(), "Implement first attempt prompt construction.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Keep first attempt prompt stable", [developer]);

    var prompt = kernel.BuildTaskBrief(
        goal.Id,
        developer.Id,
        workingDirectory: workingDirectory,
        contextDirectory: contextDirectory).Content;

    Assert.DoesNotContain("LATEST DEVELOPER RETRY BLOCKER", prompt, StringComparison.Ordinal);
    Assert.DoesNotContain("LATEST_DEVELOPER_RETRY_BLOCKER_START", prompt, StringComparison.Ordinal);
}

    [Xunit.Fact]
    public void WorkerPromptInputBudgetKeepsAllRetryAndContextBytesWhenRejecting()
{
    var exactBlocker = "Reviewer blocker: src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs method PrepareSubscriptionTask still ignores latest retry feedback.";
    var content = string.Join(Environment.NewLine, [
        "## Worker Context Package",
        "# Agent Task Brief",
        string.Empty,
        "<!-- LATEST_DEVELOPER_RETRY_BLOCKER_START -->",
        "## LATEST DEVELOPER RETRY BLOCKER - FIX FIRST",
        "Retry feedback (verbatim):",
        exactBlocker,
        "<!-- LATEST_DEVELOPER_RETRY_BLOCKER_END -->",
        string.Empty,
        "## Instructions",
        "Complete this SDLC task.",
        string.Empty,
        "## Prior Task Evidence",
        new string('p', 400),
        string.Empty,
        "## Last Verification",
        new string('v', 400),
        string.Empty,
        "## Context Digest",
        new string('d', 400)
    ]);
    var brief = new TaskBrief(
        new GoalId("goal123456789"),
        new TaskId("task123456789"),
        AgentRole.Developer,
        "Developer: retry prompt budget",
        content);
    var budget = WorkerPromptInputBudget.CountTokens(content.Replace(new string('p', 400), string.Empty, StringComparison.Ordinal)
        .Replace(new string('v', 400), string.Empty, StringComparison.Ordinal)
        .Replace(new string('d', 400), string.Empty, StringComparison.Ordinal));

    Assert.Throws<WorkerPromptInputBudgetExceededException>(() =>
        WorkerPromptInputBudget.Apply(brief, "Ollama", "qwen3:8b", budget));

    Assert.Contains("LATEST DEVELOPER RETRY BLOCKER", brief.Content, StringComparison.Ordinal);
    Assert.Contains(exactBlocker, brief.Content, StringComparison.Ordinal);
    Assert.Contains("PrepareSubscriptionTask", brief.Content, StringComparison.Ordinal);
    Assert.Contains(new string('p', 400), brief.Content, StringComparison.Ordinal);
    Assert.Contains(new string('d', 400), brief.Content, StringComparison.Ordinal);
}

}
