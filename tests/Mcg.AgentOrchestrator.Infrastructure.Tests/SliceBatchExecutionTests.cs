using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

// Parallel-safe: repositories/workspaces are unique, and worker dispatch is seamed.
public sealed class SliceBatchExecutionTests
{
    [Xunit.Fact]
    public void Prompt_SiblingContract_DeclaresEdgeInInstructionAndExample()
    {
        var prompt = GoalDagDecompositionPlanner.BuildPrompt("Implement product", true);
        Xunit.Assert.Contains("uses a symbol, API, path or contract that the sibling introduces", prompt);
        Xunit.Assert.Contains("list that sibling's id in \"dependsOn\"", prompt);
        var example = GoalDagDecompositionPlanner.ValidateSliceBatch(
            GoalDagDecompositionPlanner.Parse("example", prompt));
        Xunit.Assert.True(example.IsValid, string.Join("; ", example.ValidationErrors));
        var consumer = Xunit.Assert.Single(example.Nodes, node => node.DependsOn.Count > 0);
        Xunit.Assert.Contains(example.Nodes, node => node.Id == consumer.DependsOn.Single());
    }

    [Xunit.Fact]
    public void Validation_DeclaredSiblingContract_AcceptsEdge()
    {
        var plan = GoalDagDecompositionPlanner.ValidateSliceBatch(
            GoalDagDecompositionPlanner.Parse("slices", SiblingPlan("Code against g1's contract.", true)));
        Xunit.Assert.True(plan.IsValid, string.Join("; ", plan.ValidationErrors));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Built in parallel against the sibling contract.", "in parallel against")]
    [Xunit.InlineData("Code against g1's contract.", "g1")]
    [Xunit.InlineData("Consume the contract from g1.", "g1")]
    [Xunit.InlineData("Read src/Mcg.AgentOrchestrator.Core/Application/AlphaService.cs.", "AlphaService.cs")]
    [Xunit.InlineData("Use the contract introduced by the sibling.", "introduced by")]
    public void Validation_HiddenSiblingReference_RejectsPlan(string objective, string match)
    {
        var plan = GoalDagDecompositionPlanner.ValidateSliceBatch(
            GoalDagDecompositionPlanner.Parse("slices", SiblingPlan(objective, false)));
        Xunit.Assert.False(plan.IsValid);
        Xunit.Assert.Contains(plan.ValidationErrors, error =>
            error.Contains("g2", StringComparison.Ordinal) &&
            error.Contains(match, StringComparison.OrdinalIgnoreCase) &&
            error.Contains("g1", StringComparison.Ordinal) &&
            error.Contains("dependsOn", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void Validation_IndependentNodes_IgnoresIdsInsideIncludes()
    {
        // Keep a trusted repository scope, with the sibling id only in the Includes annotation.
        var json = SiblingPlan("Implement independent feature.", false,
            "src/Mcg.AgentOrchestrator.Core/Application/BetaService.cs (g1)");
        var plan = GoalDagDecompositionPlanner.ValidateSliceBatch(
            GoalDagDecompositionPlanner.Parse("slices", json));
        Xunit.Assert.True(plan.IsValid, string.Join("; ", plan.ValidationErrors));
        Xunit.Assert.All(plan.Nodes, node => Xunit.Assert.Empty(node.DependsOn));
    }

    [Xunit.Fact]
    public void Validation_SiblingReferenceAfterIncludes_RejectsPlan()
    {
        var json = SiblingPlan("Implement independent feature.", false)
            .Replace("BetaService.cs", "BetaService.cs\\nNotes: use g1's contract.", StringComparison.Ordinal);
        var plan = GoalDagDecompositionPlanner.ValidateSliceBatch(
            GoalDagDecompositionPlanner.Parse("slices", json));
        Xunit.Assert.False(plan.IsValid);
        Xunit.Assert.Contains(plan.ValidationErrors, error => error.Contains("without declaring dependsOn", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void Plan_DeclaredEdge_RecordsChildDependencyAndSnapshot()
    {
        var (kernel, workspace, agents, providers) = CreateContext(SiblingPlan("Consume g1's contract.", true));
        try
        {
            var parent = CreateBatch(kernel, workspace, agents, providers);
            var children = kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id).ToArray();
            Xunit.Assert.Equal(2, children.Length);
            Xunit.Assert.Equal([children[0].Id], children[1].DependsOn);
            var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
            Xunit.Assert.Equal([children[0].Id], restored.GetGoal(children[1].Id).DependsOn);
        }
        finally { Directory.Delete(workspace.RootDirectory, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void SiblingEdge_StreamComplete_PreparesBranchBeforeFirstDispatch(bool existingBranch)
    {
        var (kernel, workspace, agents, providers) = CreateContext(SiblingPlan("Consume g1's contract.", true));
        using var repository = new SliceBatchSiblingBranchFixture();
        try
        {
            var parent = CreateBatch(kernel, workspace, agents, providers);
            var children = kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id).ToArray();
            var producer = children[0];
            var consumer = children[1];
            var tip = repository.Commit(producer.Id,
                "src/Mcg.AgentOrchestrator.Core/Application/AlphaService.cs", "Producer contract");
            if (existingBranch)
                repository.Commit(consumer.Id,
                    "src/Mcg.AgentOrchestrator.Core/Application/BetaService.cs", "Existing consumer work");
            var dispatches = new List<GoalId>();
            var callbackObserved = false;
            var driver = CreateDriver(kernel, [], dispatches, repository.Repository, goal =>
            {
                Xunit.Assert.Equal(consumer.Id, goal.Id);
                Xunit.Assert.All(goal.Tasks, task => Xunit.Assert.Null(task.LastProcess));
                repository.AssertContains(consumer.Id, tip);
                callbackObserved = true;
            });
            driver.SliceBatchAdmissionEvaluator = new SliceBatchAdmissionEvaluator(
                () => kernel.Goals,
                _ => ["src/Mcg.AgentOrchestrator.Core/Application/AlphaService.cs"],
                kernel.RecordGoalPolicyDecision,
                new SliceBatchSiblingDependencyCoordinator(repository.Repository));

            // Exercise both the batch dependency gate and the driver admission gate.
            RunConsumerTick(kernel, driver, consumer, repository.Repository);
            Xunit.Assert.Empty(dispatches);
            driver.BeginTick();
            var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(
                driver.AdvanceOnce(consumer, ConductorAutonomyPolicy.Conservative).Outcome);
            Xunit.Assert.Contains("stream completion", held.Reason);
            CompleteStream(kernel, producer);
            Xunit.Assert.Equal(GoalStatus.Verified, producer.Status);
            Xunit.Assert.False(kernel.IsKnownCompletedDependencyGoal(producer.Id));
            Xunit.Assert.True(SliceBatchParentExecutionGuard.IsStreamComplete(producer));

            RunConsumerTick(kernel, driver, consumer, repository.Repository);
            Xunit.Assert.True(callbackObserved, "Consumer dispatch callback was not invoked.");
            Xunit.Assert.Equal([consumer.Id], dispatches);
            Xunit.Assert.Equal(GoalStatus.Verified, producer.Status);
            Xunit.Assert.Null(new SliceBatchSiblingDependencyCoordinator(repository.Repository)
                .PrepareBranch(consumer, kernel.Goals));
            repository.AssertContains(consumer.Id, tip);
        }
        finally { Directory.Delete(workspace.RootDirectory, recursive: true); }
    }

    [Xunit.Fact]
    public void Admission_CompleteSiblingWithoutEdge_StillBlocksCollidingScope()
    {
        var (kernel, workspace, agents, providers) = CreateContext(SiblingPlan("Implement independent feature.", false));
        try
        {
            var parent = CreateBatch(kernel, workspace, agents, providers);
            var children = kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id).ToArray();
            CompleteStream(kernel, children[0]);
            var driver = CreateDriver(kernel, [], new List<GoalId>());
            driver.BeginTick();
            var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(
                driver.AdvanceOnce(children[1], ConductorAutonomyPolicy.Conservative).Outcome);
            Xunit.Assert.Contains("occupies colliding scope", held.Reason);
        }
        finally { Directory.Delete(workspace.RootDirectory, recursive: true); }
    }

    [Xunit.Fact]
    public void SiblingEdge_MergeConflict_HoldsConsumerAndRestoresBranch()
    {
        var (kernel, workspace, agents, providers) = CreateContext(SiblingPlan("Consume g1's contract.", true));
        using var repository = new SliceBatchSiblingBranchFixture();
        try
        {
            var parent = CreateBatch(kernel, workspace, agents, providers);
            var children = kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id).ToArray();
            const string path = "src/Mcg.AgentOrchestrator.Core/Application/AlphaService.cs";
            repository.Commit(children[0].Id, path, "Producer contract");
            var consumerTip = repository.Commit(children[1].Id, path, "Conflicting consumer contract");
            CompleteStream(kernel, children[0]);
            var dispatches = new List<GoalId>();
            var driver = CreateDriver(kernel, [], dispatches, repository.Repository);
            driver.BeginTick();
            var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(
                driver.AdvanceOnce(children[1], ConductorAutonomyPolicy.Conservative).Outcome);
            Xunit.Assert.Contains(children[0].Id.Value[..8], held.Reason);
            Xunit.Assert.Contains(children[1].Id.Value[..8], held.Reason);
            Xunit.Assert.Contains(path, held.Reason);
            Xunit.Assert.Empty(dispatches);
            repository.AssertRestored(children[1].Id, consumerTip);
        }
        finally { Directory.Delete(workspace.RootDirectory, recursive: true); }
    }

    [Xunit.Fact]
    public void DependencyHold_FailedSibling_EscalatesWithoutDispatch()
    {
        var (kernel, workspace, agents, providers) = CreateContext(SiblingPlan("Consume g1's contract.", true));
        try
        {
            var parent = CreateBatch(kernel, workspace, agents, providers);
            var children = kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id).ToArray();
            var producerId = children[0].Id;
            var consumerId = children[1].Id;
            var snapshot = kernel.ExportSnapshot();
            kernel.ReplaceWithSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal.Id == producerId.Value
                    ? goal with { Status = GoalStatus.Failed }
                    : goal).ToArray()
            });
            kernel.MarkKnownDependencyGoalStatuses([
                new KeyValuePair<GoalId, string>(producerId, GoalStatus.Failed.ToString())
            ]);
            var producer = kernel.GetGoal(producerId);
            var consumer = kernel.GetGoal(consumerId);
            Xunit.Assert.True(SliceBatchSiblingDependencyCoordinator.IsSiblingEdge(consumer, producer));
            Xunit.Assert.False(SliceBatchParentExecutionGuard.IsStreamComplete(producer));
            var expected = $"dependency-terminal-without-landing: {producerId.Value[..8]} state=Failed";
            var reason = ConductorDependencyHoldEvaluator.Evaluate(consumer, [], [], kernel, out var requiresPerson);
            Xunit.Assert.Equal(expected, reason);
            Xunit.Assert.True(requiresPerson);

            var dispatches = new List<GoalId>();
            CaptureConsole(() =>
            {
                var summary = new ConductorBatchLoop().Run(kernel, CreateDriver(kernel, [], dispatches),
                    ConductorAutonomyPolicy.Conservative, Path.Combine(workspace.RootDirectory, "stop.txt"),
                    maxIterations: 1, onlyGoalId: consumerId.Value);
                Xunit.Assert.Equal(1, summary.Escalated);
            });
            Xunit.Assert.Empty(dispatches);
            Xunit.Assert.Contains(kernel.GetGoal(consumerId).Timeline, item =>
                item.Message.Contains(expected, StringComparison.Ordinal));
        }
        finally { Directory.Delete(workspace.RootDirectory, recursive: true); }
    }

    [Xunit.Fact]
    public void DependencyHold_EscalatedSibling_RequiresPerson()
    {
        var (kernel, workspace, agents, providers) = CreateContext(SiblingPlan("Consume g1's contract.", true));
        try
        {
            var parent = CreateBatch(kernel, workspace, agents, providers);
            var children = kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id).ToArray();
            Xunit.Assert.False(SliceBatchParentExecutionGuard.IsStreamComplete(children[0]));
            var reason = ConductorDependencyHoldEvaluator.Evaluate(children[1], [],
                [children[0].Id.Value], kernel, out var requiresPerson);
            Xunit.Assert.Equal($"dependency escalated: {children[0].Id.Value[..8]}", reason);
            Xunit.Assert.True(requiresPerson);
        }
        finally { Directory.Delete(workspace.RootDirectory, recursive: true); }
    }

    private static void RunConsumerTick(AgentOrchestratorKernel kernel, ConductorDriver driver,
        Goal consumer, string root) => CaptureConsole(() =>
        new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
            Path.Combine(root, "stop.txt"), maxIterations: 1, onlyGoalId: consumer.Id.Value));

    private static void CompleteStream(AgentOrchestratorKernel kernel, Goal goal)
    {
        var completedAt = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        foreach (var task in goal.Tasks)
        {
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("test-worker", "test.exe", "unused", completedAt));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id,
                new TaskProcessRecord(101, "test.exe", "unused", "stdout", "stderr", "exit",
                    completedAt, completedAt, 0));
            kernel.RecordTaskVerification(goal.Id, task.Id,
                new TaskVerificationRecord("test.exe", "unused", 0, "ok", "", completedAt));
        }
    }

    private static string SiblingPlan(string consumerObjective, bool dependsOn, string? consumerPath = null)
    {
        static string Scoped(string objective, string path) =>
            $"{objective}\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- {path}";
        return "```json\n" + JsonSerializer.Serialize(new[]
        {
            new { id = "g1", objective = Scoped("Introduce producer contract.",
                "src/Mcg.AgentOrchestrator.Core/Application/AlphaService.cs"), dependsOn = Array.Empty<string>() },
            new { id = "g2", objective = Scoped(consumerObjective,
                consumerPath ?? "src/Mcg.AgentOrchestrator.Core/Application/BetaService.cs"),
                dependsOn = dependsOn ? new[] { "g1" } : Array.Empty<string>() }
        }) + "\n```";
    }

    [Xunit.Fact]
    public void Plan_TwoSlices_RecordsStreamReviewPipelineOnlyForChildren()
    {
        var (kernel, workspace, agents, providers) = CreateContext(TwoSliceBatchJson);
        var parent = CreateBatch(kernel, workspace, agents, providers);
        var children = kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id).ToArray();

        Xunit.Assert.Equal(2, children.Length);
        Xunit.Assert.Equal([AgentRole.Reviewer],
            parent.Tasks.Select(task => task.RequiredRole));
        Xunit.Assert.Contains(parent.Timeline, item =>
            item.Kind == ProgressKind.GoalPolicyDecision &&
            item.Message.StartsWith("Intake pipeline decision (override): whole-goal-review;", StringComparison.Ordinal));
        Xunit.Assert.All(children, child =>
        {
            Xunit.Assert.Equal([AgentRole.Developer, AgentRole.Reviewer],
                child.Tasks.Select(task => task.RequiredRole));
            Xunit.Assert.Contains(child.Timeline, item =>
                item.Kind == ProgressKind.GoalPolicyDecision &&
                item.Message.StartsWith("Intake pipeline decision (override): developer-stream-reviewer;", StringComparison.Ordinal));
            Xunit.Assert.All(child.Tasks, task => Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status));
        });

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var requested = GoalObjectivePlanner.Build("Implement src/Other.cs.", GoalIntakePipeline.FiveRole);
        Xunit.Assert.All(restored.Goals.Where(goal => goal.SliceBatchParentId == parent.Id), child =>
        {
            var mismatch = Xunit.Assert.Throws<InvalidOperationException>(() =>
                GoalLifecycleCommands.EnsureRequestedPipelineMatchesPersistedGoal(requested, child));
            Xunit.Assert.Contains("persisted workflow='developer-stream-reviewer'", mismatch.Message, StringComparison.Ordinal);
        });
        var parentMismatch = Xunit.Assert.Throws<InvalidOperationException>(() =>
            GoalLifecycleCommands.EnsureRequestedPipelineMatchesPersistedGoal(requested, restored.GetGoal(parent.Id)));
        Xunit.Assert.Contains("orderedRoles=[Reviewer]", parentMismatch.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlanCreatesDispatchableChildrenAndNonExecutingParent()
    {
        var (kernel, workspace, agents, providers) = CreateContext(DisjointSliceBatchJson);
        var parent = CreateBatch(kernel, workspace, agents, providers);
        var children = kernel.Goals
            .Where(goal => goal.SliceBatchParentId == parent.Id)
            .ToArray();
        var workspaceCreations = new List<GoalId>();
        var dispatches = new List<GoalId>();
        var driver = CreateDriver(kernel, workspaceCreations, dispatches);

        driver.BeginTick();
        var parentResult = driver.AdvanceOnce(parent, ConductorAutonomyPolicy.Conservative);
        var childResults = children
            .Select(child => driver.AdvanceOnce(child, ConductorAutonomyPolicy.Conservative))
            .ToArray();

        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(parentResult.Outcome);
        Xunit.Assert.Contains("does not execute worker tasks", held.Reason, StringComparison.Ordinal);
        Xunit.Assert.All(childResults, result =>
            Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome));
        Xunit.Assert.Equal(children.Select(child => child.Id), dispatches);
        Xunit.Assert.Empty(workspaceCreations);
        Xunit.Assert.DoesNotContain(parent.Id, dispatches);
    }

    [Xunit.Fact]
    public void PlanCreatesCollidingChildrenThatSequenceWithRecordedReason()
    {
        var (kernel, workspace, agents, providers) = CreateContext(CollidingSliceBatchJson);
        var parent = CreateBatch(kernel, workspace, agents, providers);
        var children = kernel.Goals
            .Where(goal => goal.SliceBatchParentId == parent.Id)
            .ToArray();
        var workspaceCreations = new List<GoalId>();
        var dispatches = new List<GoalId>();
        var driver = CreateDriver(kernel, workspaceCreations, dispatches);

        driver.BeginTick();
        var firstResult = driver.AdvanceOnce(children[0], ConductorAutonomyPolicy.Conservative);
        var secondResult = driver.AdvanceOnce(children[1], ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(firstResult.Outcome);
        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(secondResult.Outcome);
        Xunit.Assert.Contains("ownership:shared-infrastructure:core/application", held.Reason, StringComparison.Ordinal);
        Xunit.Assert.Contains(children[1].Timeline, item =>
            item.Kind == ProgressKind.GoalPolicyDecision && item.Message == held.Reason);
        Xunit.Assert.Equal([children[0].Id], dispatches);
        Xunit.Assert.Empty(workspaceCreations);
    }

    [Xunit.Fact]
    public void SliceBatchParentDoesNotAccumulateAStall()
    {
        var (kernel, workspace, agents, providers) = CreateContext(DisjointSliceBatchJson);
        var parent = CreateBatch(kernel, workspace, agents, providers);
        var workspaceCreations = new List<GoalId>();
        var dispatches = new List<GoalId>();
        var driver = CreateDriver(kernel, workspaceCreations, dispatches);
        var root = CreateTempDirectory();
        var logPath = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
        var stopPath = Path.Combine(root, "stop.txt");
        var now = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var writer = new ConductEventLogWriter(logPath, utcNow: () => now);

        new ConductorBatchLoop(conductEventLogWriter: writer, utcNow: () => now).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            stopPath,
            maxIterations: 1,
            onlyGoalId: parent.Id.Value,
            goalStallThreshold: TimeSpan.FromMinutes(10));

        now = now.AddMinutes(11);
        new ConductorBatchLoop(conductEventLogWriter: writer, utcNow: () => now).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            stopPath,
            maxIterations: 1,
            onlyGoalId: parent.Id.Value,
            goalStallThreshold: TimeSpan.FromMinutes(10));

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        Xunit.Assert.DoesNotContain(records, record =>
            record.EventKind == "goal-stalled" && record.GoalId == parent.Id.Value[..8]);
        Xunit.Assert.Null(parent.CurrentHold?.StalledAt);
        Xunit.Assert.Empty(workspaceCreations);
        Xunit.Assert.Empty(dispatches);
    }

    [Xunit.Fact]
    public void OrdinaryGoalWithParentGuardDispatchesNormally()
    {
        var (kernel, _, agents, _) = CreateContext(DisjointSliceBatchJson);
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            agents,
            "Implement an ordinary goal");
        var workspaceCreations = new List<GoalId>();
        var dispatches = new List<GoalId>();
        var driver = CreateDriver(kernel, workspaceCreations, dispatches);

        driver.BeginTick();
        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Xunit.Assert.Equal([goal.Id], dispatches);
        Xunit.Assert.Empty(workspaceCreations);
    }

    internal static Goal CreateBatch(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers)
    {
        using var samples = PlanSampleProviderBridge.Use(providers);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["plan", "Implement three slices", "--slice-batch", "--confirm-plan"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        return Xunit.Assert.Single(kernel.Goals.Where(goal => goal.SliceBatchParentId is null));
    }

    internal static ConductorDriver CreateDriver(
        AgentOrchestratorKernel kernel,
        ICollection<GoalId> workspaceCreations,
        ICollection<GoalId> dispatches,
        string? repository = null,
        Action<Goal>? beforeDispatch = null)
    {
        var driver = new ConductorDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningPaidWorkerCount: () => dispatches.Count,
            createWorkspace: goal =>
            {
                workspaceCreations.Add(goal.Id);
                return "unused";
            },
            dispatchAndStart: goal =>
            {
                beforeDispatch?.Invoke(goal);
                dispatches.Add(goal.Id);
                return DispatchStartOutcome.Started();
            },
            startRecordedDispatches: null,
            buildServerShutdown: null,
            runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: null,
            retryTask: null,
            recordTaskNote: null,
            recordCriterionRetryFeedback: null,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                "goal/test",
                "Current.",
                [],
                null),
            land: (goal, _) => new LandingResult(
                goal.Id.Value,
                goal.Id.Value[..8],
                new LandingDecision.Promote(),
                "integration",
                true,
                "Landed."),
            afterSuccessfulLanding: null,
            record: _ => { },
            cleanup: _ => new GoalWorktreeRemoveResult("Clean.", null, [], null),
            writeEscalation: (_, _, _) => { },
            classifyChangeRisk: _ => null);
        driver.SliceBatchParentExecutionGuard = new SliceBatchParentExecutionGuard(() => kernel.Goals);
        driver.SliceBatchAdmissionEvaluator = new SliceBatchAdmissionEvaluator(
            () => kernel.Goals,
            _ => [],
            kernel.RecordGoalPolicyDecision,
            new SliceBatchSiblingDependencyCoordinator(repository));
        return driver;
    }

    internal static (
        AgentOrchestratorKernel Kernel,
        OrchestratorWorkspace Workspace,
        IReadOnlyList<AgentDefinition> Agents,
        IModelProviderRegistry Providers) CreateContext(string plannerOutput)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-slice-batch-execution", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var planner = new AgentDefinition(
            AgentId.New(),
            "Test-Planner",
            AgentRole.Planner,
            new ModelProfile("Fake", "fake-plan-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        var developer = new AgentDefinition(
            AgentId.New(),
            "Test-Developer",
            AgentRole.Developer,
            new ModelProfile("Fake", "fake-dev-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        var reviewer = new AgentDefinition(
            AgentId.New(), "Test-Reviewer", AgentRole.Reviewer,
            new ModelProfile("Fake", "fake-review-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        var streamReviewer = reviewer with { Id = AgentId.New(), Name = "Test-Stream-Reviewer" };
        IReadOnlyList<AgentDefinition> agents = [planner, developer, reviewer, streamReviewer];
        IModelProviderRegistry providers = new InMemoryModelProviderRegistry([
            new FakeSmokeProvider(plannerOutput, providerName: "Fake")
        ]);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("missing-provider", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        return (kernel, workspace, agents, providers);
    }

    private const string TwoSliceBatchJson = """
        ```json
        [{"id":"g1","objective":"Implement feature A.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureA/A.cs","dependsOn":[]},{"id":"g2","objective":"Implement feature B.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureB/B.cs","dependsOn":[]}]
        ```
        """;

    internal const string DisjointSliceBatchJson = """
        ```json
        [{"id":"g1","objective":"Implement feature A.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureA/A.cs","dependsOn":[]},{"id":"g2","objective":"Implement feature B.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureB/B.cs","dependsOn":[]},{"id":"g3","objective":"Implement feature C.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureC/C.cs","dependsOn":[]}]
        ```
        """;

    private const string CollidingSliceBatchJson = """
        ```json
        [{"id":"g1","objective":"Implement feature A.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/Mcg.AgentOrchestrator.Core/Application/AlphaService.cs","dependsOn":[]},{"id":"g2","objective":"Implement feature B.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/Mcg.AgentOrchestrator.Core/Application/BetaService.cs","dependsOn":[]},{"id":"g3","objective":"Implement feature C.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/Mcg.AgentOrchestrator.App/Orchestration/GammaSlice.cs","dependsOn":[]}]
        ```
        """;
}
