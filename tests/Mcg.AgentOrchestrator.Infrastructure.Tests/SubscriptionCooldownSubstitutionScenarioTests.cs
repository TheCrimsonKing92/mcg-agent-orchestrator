using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Scenario = SubscriptionCooldownSubstitutionTests.Scenario;

// Parallel-safe: isolated worktrees/SQLite files and an injected stdin-gated fixture process.
public sealed class SubscriptionCooldownSubstitutionScenarioTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false, false, false)]
    [Xunit.InlineData(true, false, false)]
    [Xunit.InlineData(false, true, false)]
    [Xunit.InlineData(true, true, false)]
    [Xunit.InlineData(false, true, true)]
    [Xunit.InlineData(true, true, true)]
    public async Task Conduct_ClassifiedCooldown_StartsAlternateAcrossReload(
        bool reload, bool offline, bool rejectedFirst)
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var original = goal.Tasks.Single();
        var reset = original.SubscriptionRetryAfter!.Value;
        var sourceDispatch = original.LastDispatch!;
        var sourceVerification = Assert.Single(original.VerificationHistory);
        Assert.True(DispatchFailureClassifier.IsRecoverableSubscriptionLimitFailure(sourceVerification));
        Assert.NotEqual(ProviderFailureKind.BudgetExhausted, sourceVerification.ProviderFailureKind);
        if (offline) scenario.Agents = [scenario.Primary with { Status = AgentStatus.Offline }, scenario.Alternate];
        if (rejectedFirst)
        {
            var rejected = scenario.Alternate with
            {
                Id = new("rejected-alternate"),
                Subscription = scenario.Alternate.Subscription! with { WorkerProfileName = "missing-profile" }
            };
            scenario.Agents = [scenario.Agents[0], rejected, scenario.Alternate];
        }
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(scenario.Workspace.SqliteStatePath);
        await repository.SaveAsync(scenario.Kernel);
        if (reload)
        {
            var loaded = await repository.LoadAsync();
            scenario.Kernel = AgentOrchestratorKernel.FromSnapshot(loaded.ExportSnapshot(), scenario.Clock);
            goal = scenario.Kernel.GetGoal(goal.Id);
        }
        var plan = Plan(scenario, goal);
        Assert.False(Assert.Single(plan.Items).CanPrepare);
        Assert.True(Assert.Single(plan.Items).CanAttemptSubscriptionSubstitution);
        Assert.IsType<DispatchReadinessReady>(DispatchReadinessEvaluator.EvaluateDispatchReadiness(goal, plan, scenario.Clock.UtcNow));
        SubscriptionStartResult? started = null;
        var launchCount = 0;
        using var worker = new ProviderHoldFallbackScenarioTests.PreparationWorker();
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            evaluateReadiness: current => DispatchReadinessEvaluator.EvaluateDispatchReadiness(
                current, Plan(scenario, current), scenario.Clock.UtcNow),
            dispatchAndStart: current =>
            {
                started = Start(scenario, current, requested =>
                {
                    launchCount++;
                    return worker.Start(requested);
                });
                return started.Dispatches.Count > 0
                    ? DispatchStartOutcome.Started(started.Dispatches.Select(item => item.Task).ToArray())
                    : DispatchStartOutcome.EmptyBatch(string.Join("\n", started.BlockedDiagnostics.Select(item => item.ToLine())));
            });

        var advanced = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(advanced.Outcome);
        Assert.Equal(1, launchCount);
        Assert.NotNull(started);
        Assert.Single(started.Processes.Tasks);
        var currentGoal = scenario.Kernel.GetGoal(goal.Id);
        var task = currentGoal.Tasks.Single();
        Assert.Equal(scenario.Alternate.Id, task.AssignedAgentId);
        Assert.Equal(scenario.Alternate.Id.Value, task.LastDispatch!.AssignedAgentId);
        Assert.Equal("codex-cli", task.LastDispatch.WorkerName);
        Assert.NotNull(task.LastProcess);
        Assert.Null(task.SubscriptionRetryAfter);
        Assert.False(DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, scenario.Clock.UtcNow, out _));
        Assert.Contains(task.VerificationHistory, item =>
            item.DispatchStartedAt == sourceVerification.DispatchStartedAt &&
            item.CompletedAt == sourceVerification.CompletedAt &&
            item.StandardErrorPath == sourceVerification.StandardErrorPath &&
            DispatchFailureClassifier.IsRecoverableSubscriptionLimitFailure(item));
        Assert.Contains(task.DispatchHistory, item => item.DispatchedAt == sourceDispatch.DispatchedAt &&
            item.ProviderName == sourceDispatch.ProviderName && item.AssignedAgentId == sourceDispatch.AssignedAgentId);
        var evt = Assert.Single(currentGoal.Timeline, item => item.Kind == ProgressKind.ProviderHoldSubstituted);
        Assert.Equal(task.Id, evt.TaskId);
        Assert.Contains("reason=subscription-cooldown", evt.Message, StringComparison.Ordinal);
        Assert.Contains("held binding=anthropic::<provider-default>", evt.Message, StringComparison.Ordinal);
        Assert.Contains($"held agent={scenario.Primary.Id.Value}", evt.Message, StringComparison.Ordinal);
        Assert.Contains($"substitute={scenario.Alternate.Id.Value}", evt.Message, StringComparison.Ordinal);
        Assert.Contains($"receipt={scenario.Receipt(goal)}", evt.Message, StringComparison.Ordinal);
        Assert.Contains($"source dispatch={sourceDispatch.DispatchedAt:O}", evt.Message, StringComparison.Ordinal);
        Assert.Contains($"reset={reset:O}", evt.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(currentGoal.Timeline, item => item.Message.Contains(OfflineAssignedAgentRedelegation.Marker, StringComparison.Ordinal));
        Assert.True(scenario.AuthProbes > 0);

        await repository.SaveAsync(scenario.Kernel);
        var restored = await repository.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        Assert.Equal(evt, Assert.Single(restoredGoal.Timeline, item => item.Kind == ProgressKind.ProviderHoldSubstituted));
        // Returning the primary to Available must not remove its persisted provider cooldown.
        scenario.Agents = [scenario.Primary, scenario.Alternate];
        var sibling = restored.AddTask(goal.Id, AgentRole.Planner, "Another provider request.", scenario.Agents);
        Assert.Equal(scenario.Primary.Id, sibling.AssignedAgentId);
        Assert.True(DispatchFailureClassifier.TryGetProviderSubscriptionCooldown(
            restoredGoal, sibling.Id, "Anthropic", scenario.Clock.UtcNow, out var hold));
        Assert.Equal(reset, hold.RetryAfter);
        Assert.Equal(scenario.Receipt(goal), hold.EvidenceReceipt);
        var blocked = scenario.Preflight(restoredGoal, sibling, restored.Goals);
        Assert.False(blocked.Allowed);
        Assert.Contains(blocked.Findings, finding => finding.Contains("provider Anthropic is cooling down", StringComparison.Ordinal));
        Assert.True(DispatchFailureClassifier.TryGetProviderSubscriptionCooldown(
            restoredGoal, sibling.Id, "Anthropic", reset.AddTicks(-1), out _));
        Assert.False(DispatchFailureClassifier.TryGetProviderSubscriptionCooldown(
            restoredGoal, sibling.Id, "Anthropic", reset, out _));
        Assert.False(DispatchFailureClassifier.TryGetProviderSubscriptionCooldown(
            restoredGoal, sibling.Id, "Anthropic", reset.AddTicks(1), out _));
        var intents = SqliteOperatorIntentStore.ForDirectories(scenario.Workspace.OrchestratorDirectory, scenario.Workspace.LogDirectory);
        Assert.Empty(await intents.ListForGoalAsync(goal.Id.Value, cancellationToken: Xunit.TestContext.Current.CancellationToken));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Prepare_UnfinishedDependency_OnlyStartsReadyResearcher(bool offline)
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        if (offline) scenario.Agents = [scenario.Primary with { Status = AgentStatus.Offline }, scenario.Alternate];
        var researcher = scenario.Alternate with { Id = new("codex-researcher"), Role = AgentRole.Researcher };
        scenario.Agents = [..scenario.Agents, researcher];
        var ready = scenario.Kernel.AddTask(goal.Id, AgentRole.Researcher, "Research first.", [researcher]);
        Assert.Equal(WorkTaskStatus.Assigned, ready.Status);
        Assert.Equal(ready.Id.Value, Assert.Single(
            DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, task)));
        var launches = 0;
        using var worker = new ProviderHoldFallbackScenarioTests.PreparationWorker();
        var result = Start(scenario, goal, requested =>
        {
            launches++;
            return worker.Start(requested);
        });
        Assert.Equal(ready.Id, Assert.Single(result.Dispatches).Task.Id);
        Assert.Single(result.Processes.Tasks);
        Assert.Equal(1, launches);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.NotNull(task.SubscriptionRetryAfter);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Message.Contains(OfflineAssignedAgentRedelegation.Marker, StringComparison.Ordinal));
        // The ready predecessor can be scheduled, so the Planner belongs to a later batch.
        Assert.Contains(result.BlockedDiagnostics, diagnostic =>
            diagnostic.TaskId == task.Id.Value &&
            diagnostic.Reason == "parallel-serialized" &&
            diagnostic.Details?.Contains("waits for dependency completion") == true);
    }

    [Xunit.Theory]
    [Xunit.InlineData("pin")]
    [Xunit.InlineData("no-alternate")]
    [Xunit.InlineData("preflight-rejected")]
    [Xunit.InlineData("offline-pin")]
    [Xunit.InlineData("offline-no-alternate")]
    [Xunit.InlineData("offline-preflight-rejected")]
    public void Prepare_IneligibleSubstitution_DoesNotStart(string control)
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        var reset = task.SubscriptionRetryAfter;
        if (control.StartsWith("offline-", StringComparison.Ordinal))
        {
            scenario.Agents = [scenario.Primary with { Status = AgentStatus.Offline }, scenario.Alternate];
            control = control["offline-".Length..];
        }
        if (control == "pin") scenario.Kernel.ReassignTaskAgent(goal.Id, task.Id, scenario.Primary);
        if (control == "no-alternate") scenario.Agents = [scenario.Agents[0]];
        if (control == "preflight-rejected") scenario.Agents = [scenario.Agents[0],
            scenario.Alternate with { Subscription = scenario.Alternate.Subscription! with { WorkerProfileName = "missing-profile" } }];
        var launchCount = 0;
        var result = Start(scenario, goal, _ =>
        {
            launchCount++;
            throw new InvalidOperationException("An ineligible alternate must not start.");
        });
        Assert.Empty(result.Dispatches);
        Assert.Equal(0, launchCount);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.Equal(reset, task.SubscriptionRetryAfter);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Message.Contains(OfflineAssignedAgentRedelegation.Marker, StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void Prepare_OfflineSubstitutedAgent_PreservesSharedBound()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        scenario.Resolver().ApplyBatch(goal, [task.Id]);
        Assert.Equal(scenario.Alternate.Id, task.AssignedAgentId);
        var substitution = Assert.Single(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddMinutes(1);
        scenario.Limit(goal, provider: "OpenAI");
        scenario.Kernel.AcknowledgeSubscriptionLimitReview(goal.Id, task.Id,
            "Acknowledge repeated limits to isolate the automatic substitution bound.");
        var reset = task.SubscriptionRetryAfter;
        Assert.NotNull(reset);
        var third = Scenario.Agent("third", "Ollama", "codex-oss-cli", "qwen3-coder");
        var researcher = third with { Id = new("oss-researcher"), Role = AgentRole.Researcher };
        scenario.Agents = [scenario.Primary, scenario.Alternate with { Status = AgentStatus.Offline }, third, researcher];
        var scratch = AgentOrchestratorKernel.FromSnapshot(scenario.Kernel.ExportSnapshot(), scenario.Clock);
        var trial = scratch.ReassignTaskAgent(goal.Id, task.Id, third);
        var preflight = scenario.Preflight(scratch.GetGoal(goal.Id), trial, scratch.Goals);
        Assert.True(preflight.Allowed, string.Join("\n", preflight.Findings));
        var ready = scenario.Kernel.AddTask(goal.Id, AgentRole.Researcher, "Independent research.", [researcher]);
        using var worker = new ProviderHoldFallbackScenarioTests.PreparationWorker();
        var launches = 0;

        var result = Start(scenario, goal, requested =>
        {
            launches++;
            return worker.Start(requested);
        });

        Assert.Equal(ready.Id, Assert.Single(result.Dispatches).Task.Id);
        Assert.Single(result.Processes.Tasks);
        Assert.Equal(1, launches);
        Assert.Equal(scenario.Alternate.Id, task.AssignedAgentId);
        Assert.Equal(reset, task.SubscriptionRetryAfter);
        Assert.Equal(substitution, Assert.Single(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted));
        Assert.DoesNotContain(goal.Timeline, evt => evt.Message.Contains(OfflineAssignedAgentRedelegation.Marker, StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task Cooldown_AfterNewDispatchAndHistoryCap_RetainsOriginalAuthority()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        var reset = task.SubscriptionRetryAfter;
        var original = Assert.Single(task.VerificationHistory);
        scenario.Resolver().ApplyBatch(goal, [task.Id]);
        Assert.Equal(scenario.Alternate.Id, task.AssignedAgentId);
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddMinutes(1);
        scenario.Kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "alternate fixture", scenario.Workspace.ResolveExecutionDirectory(goal.Id), scenario.Clock.UtcNow,
            ProviderName: "OpenAI", WorkerProviderKind: ProviderKind.OpenAICodexCli));
        for (var round = 0; round < 25; round++)
            scenario.Kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "alternate fixture", task.LastDispatch!.WorkingDirectory, 0, "", "",
                scenario.Clock.UtcNow.AddSeconds(round), DispatchStartedAt: task.LastDispatch.DispatchedAt));
        Assert.Contains(original, task.VerificationHistory);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(scenario.Workspace.SqliteStatePath);
        await repository.SaveAsync(scenario.Kernel);
        var restored = await repository.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        Assert.True(DispatchFailureClassifier.TryGetProviderSubscriptionCooldown(restoredGoal,
            TaskId.New(), "Anthropic", scenario.Clock.UtcNow, out var cooldown));
        Assert.Equal(reset, cooldown.RetryAfter);
        Assert.Equal(scenario.Receipt(goal), cooldown.EvidenceReceipt);
        Assert.Equal("anthropic::<provider-default>", cooldown.BindingKey);
        Assert.Equal(original.DispatchStartedAt, cooldown.DispatchStartedAt);
        Assert.False(DispatchFailureClassifier.TryGetProviderSubscriptionCooldown(restoredGoal,
            TaskId.New(), "OpenAI", scenario.Clock.UtcNow, out _));
    }

    private static SubscriptionPlan Plan(Scenario scenario, Goal goal) => SubscriptionPlanBuilder.Build(
        goal, scenario.Agents, scenario.Profiles, now: scenario.Clock.UtcNow,
        sandboxOptions: scenario.Sandbox, commandExists: scenario.CommandExists,
        providerHoldScope: scenario.Kernel.ProviderHoldScope.ToArray(), claudeAuthProbe: scenario.ProbeAuth);

    private static SubscriptionStartResult Start(Scenario scenario, Goal goal, Func<ProcessStartInfo, Process> launcher)
    {
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(scenario.Workspace.SqliteStatePath);
        return new GoalDispatchOperations().StartSubscriptionReadyTasks(scenario.Kernel, scenario.Workspace,
            goal, scenario.Agents, scenario.Profiles,
            checkpointBeforeWorkerStart: (kernel, _, _, _) => repository.SaveAsync(kernel).GetAwaiter().GetResult(),
            runner: new BackgroundDispatchRunner(clock: scenario.Clock, disableProcessStart: false, startProcess: launcher),
            sandboxOptions: scenario.Sandbox, commandExists: scenario.CommandExists,
            claudeAuthProbe: scenario.ProbeAuth, cascadeTesterCheapFirst: false, utcNow: () => scenario.Clock.UtcNow);
    }
}
