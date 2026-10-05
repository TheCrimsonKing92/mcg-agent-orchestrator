using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Each SQLite store and workspace belongs to one test; console capture uses the CLI collection.
[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class ReadinessConductorVerdictParityTests : CliTaskQueryTestSupport
{
    [Fact]
    public async Task CrossGoalExhaustion_ReadinessAndConductorReportSameHold()
    {
        var root = CreateTempDirectory();
        try
        {
            var (seed, source, target, agents) = ReadinessTestSeed.Create(crossGoalHold: true);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var path = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(path);
            var repository = new SqliteOrchestratorStateRepository(path);
            await repository.SaveAsync(seed);
            var conductorKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository, [], root);
            Assert.Equal(2, conductorKernel.Goals.Count);
            var profiles = WorkerProfileCatalog.Default();
            var conductor = Assert.IsType<DispatchReadinessBlocked>(
                ConductorDriver.EvaluateConductorReadiness(
                    conductorKernel, conductorKernel.GetGoal(target.Id), agents, profiles, ReadinessTestSeed.Now));
            Assert.True(conductor.HasCandidates);
            Assert.NotNull(conductor.ProviderBudgetHold);
            Assert.Equal(source.Id, conductor.ProviderBudgetHold.SourceGoalId);

            // Establish the defect's discriminating precondition: main's B-only scope is ready.
            var targetOnly = await repository.LoadGoalsAsync([target.Id]);
            Assert.IsType<DispatchReadinessReady>(DispatchReadinessAssessment.Evaluate(
                targetOnly.GetGoal(target.Id), targetOnly.Goals, agents, profiles, ReadinessTestSeed.Now).Verdict);
            Goal? current = null;
            var output = AsyncLocalConsoleRouter.Capture(() => CliPersistentStateRunner.ExecuteCommand(
                ["readiness", target.Id.Value[..8]], repository, workspace, ref agents,
                new InMemoryModelProviderRegistry([]), ref profiles, ref current));

            Assert.Equal(target.Id, current!.Id);
            Assert.Contains($"Blocker provider-budget-exhausted: {conductor.Reason}", output);
            Assert.Contains("xai::<provider-default>", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(source.Objective, output);
            var report = GoalReadinessPreflight.Build(current, agents, root, profiles,
                resolveWorktree: (_, _) => root, providerHoldScope: conductorKernel.Goals,
                now: ReadinessTestSeed.Now);
            var finding = Assert.Single(report.Findings, item => item.Kind == "provider-budget-exhausted");
            Assert.Equal(GoalReadinessSeverity.Blocker, finding.Severity);
            Assert.False(finding.CanOverride);
            Assert.Equal(conductor.Reason, finding.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

internal static class ReadinessTestSeed
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 6, 20, 0, 0, TimeSpan.Zero);

    internal static (AgentOrchestratorKernel Kernel, Goal Source, Goal Target,
        IReadOnlyList<AgentDefinition> Agents) Create(bool crossGoalHold = false, bool selfHold = false)
    {
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [new AgentDefinition(
            new AgentId("xai-developer"), "xAI Developer", AgentRole.Developer,
            new ModelProfile("xAI", "grok-4.6", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
                SubscriptionMode.ApiKey, "high"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("grok-cli", "grok-4.6", "high"))];
        var source = kernel.CreateGoal("Source binding observation",
            [new TaskSpec(TaskId.New(), "Inspect src/Source.cs", AgentRole.Developer)]);
        var target = kernel.CreateGoal("Inspect src/Target.cs",
            selfHold
                ? [new TaskSpec(TaskId.New(), "Inspect first src/Target.cs", AgentRole.Developer),
                   new TaskSpec(TaskId.New(), "Inspect second src/Target.cs", AgentRole.Developer)]
                : [new TaskSpec(TaskId.New(), "Inspect src/Target.cs", AgentRole.Developer)]);
        kernel.ActivateGoal(source.Id, agents);
        kernel.ActivateGoal(target.Id, agents);
        if (crossGoalHold || selfHold)
        {
            var heldGoal = selfHold ? target : source;
            var task = heldGoal.Tasks[0];
            var started = Now.AddMinutes(-30);
            kernel.RecordTaskDispatch(heldGoal.Id, task.Id, new TaskDispatchRecord(
                "grok-cli", "grok", "C:\\repo", started,
                ProviderName: "xAI", WorkerProviderKind: ProviderKind.XaiGrokCli, GoalId: heldGoal.Id));
            kernel.RecordTaskVerification(heldGoal.Id, task.Id, new TaskVerificationRecord(
                "grok", "C:\\repo", 1, string.Empty, "Provider budget exhausted.", started.AddMinutes(1),
                ProviderFailureKind: ProviderFailureKind.BudgetExhausted, DispatchStartedAt: started));
        }
        return (kernel, kernel.GetGoal(source.Id), kernel.GetGoal(target.Id), agents);
    }
}
