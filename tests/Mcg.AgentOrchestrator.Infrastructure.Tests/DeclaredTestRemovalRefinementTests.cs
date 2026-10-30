using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each scenario owns its kernel, fake provider, and unique temporary files.
public sealed class DeclaredTestRemovalRefinementTests
{
    private const string Ordinary =
        "The Reviewer confirms the behavior in the diff. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.";

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(50)]
    public async Task RefineAsync_FiftyRemovals_RegistersOnlyOrdinaryCriterion(int ordinaryPosition)
    {
        var bullets = RemovalBullets();
        bullets.Insert(ordinaryPosition, Ordinary);
        using var scenario = new Scenario(bullets);

        var result = await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
        Assert.NotNull(scenario.Provider.LastRequest);
        Assert.Equal([Ordinary], scenario.Goal.RefinedSpec!.AcceptanceCriteria);
        Assert.Empty(scenario.Goal.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        Assert.Empty(scenario.Goal.RefinedSpec.OperatorOwnedAcceptanceCriteria);
        Assert.Empty(scenario.Goal.RefinedSpec.OpenQuestions);
        Assert.Empty(scenario.Collaboration.Items);
        var set = DeclaredTestRemovalSet.FromObjective(scenario.Goal.Objective);
        Assert.Equal(50, set.Count);
        Assert.Equal(Enumerable.Range(0, 50).Select(i => $"Example.Tests.FooTests.Removed{i:D2}"),
            set.Declarations.Select(item => item.TestIdentity));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecordReviewerResult_OneOrdinaryCriterion_RequiresOnlyIndexZero(bool includesZero)
    {
        var bullets = RemovalBullets();
        bullets.Insert(25, Ordinary);
        using var scenario = new Scenario(bullets);
        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);
        Assert.Single(scenario.Goal.RefinedSpec!.AcceptanceCriteria);
        scenario.Kernel.ActivateGoal(scenario.Goal.Id, AgentCatalog.Default().Agents);
        var now = DateTimeOffset.UtcNow;
        scenario.Kernel.RecordTaskDispatch(scenario.Goal.Id, scenario.Reviewer.Id,
            new TaskDispatchRecord("codex-cli", "review", "C:\\fixture", now));
        var verdicts = includesZero
            ? """[{"criterion_index":0,"verdict":"met","evidence":"The diff implements the behavior."}]"""
            : """[{"criterion_index":1,"verdict":"met","evidence":"An unregistered observation."}]""";
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: review", "tests: pass - fixture",
            "commit: none", "blockers: none", "findings: []", "touched_anchors: []",
            $"criteria_verdicts: {verdicts}", "verdict: pass",
            "model_fit: fixture/model - adequate - review", "skills: none", "confidence: high",
            "END_WORKER_RESULT");

        scenario.Kernel.RecordDispatchExecutionResult(scenario.Goal.Id, scenario.Reviewer.Id,
            new TaskVerificationRecord("review", "C:\\fixture", 0, output, string.Empty, now,
                WorkerResultPresent: true, DispatchStartedAt: now));

        Assert.Equal(includesZero ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, scenario.Reviewer.Status);
        if (!includesZero)
            Assert.Contains(scenario.Goal.Timeline, entry => entry.Kind == ProgressKind.TaskFailed &&
                entry.Message.Contains("expected every registered criterion_index 0..0 (criteria 1 to 1)",
                    StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefineAsync_AllRemovals_NeverRegistersRemovalCriteria(bool invalidOutput)
    {
        using var scenario = new Scenario(RemovalBullets(), invalidOutput);
        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);
        Assert.NotNull(scenario.Provider.LastRequest);
        Assert.NotNull(scenario.Goal.RefinedSpec);
        if (invalidOutput)
        {
            var criterion = Assert.Single(scenario.Goal.RefinedSpec.AcceptanceCriteria);
            Assert.StartsWith("The objective is achieved:", criterion);
            Assert.Contains("Reviewer owns; Reviewer executes. TEST-VERIFIABLE.", criterion);
            Assert.Equal(VerificationClass.TestVerifiable, scenario.Goal.RefinedSpec.VerificationClass);
        }
        else
        {
            Assert.Empty(scenario.Goal.RefinedSpec.AcceptanceCriteria);
        }
        Assert.Empty(scenario.Goal.RefinedSpec.OpenQuestions);
    }

    [Fact]
    public async Task RefineAsync_DuplicateRemovals_DoNotRaiseOwnershipIssues()
    {
        using var scenario = new Scenario(["test-removal: FooTests.Removed", Ordinary,
            "test-removal: FooTests.Removed"]);
        var result = await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);
        Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
        Assert.Equal([Ordinary], scenario.Goal.RefinedSpec!.AcceptanceCriteria);
        Assert.Empty(scenario.Collaboration.Items);
    }

    [Fact]
    public void OwnershipChecks_Removals_DoNotRequireOwnerSentences()
    {
        const string owned = "The behavior works. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
        var objective = "## Acceptance criteria\n1. test-removal: FooTests.Removed\n2. " + owned;
        Assert.True(OwnedBriefOwnershipSettlement.TrySettle("ownership-lifecycle",
            "Who executes verification?", objective, out var settlement));
        Assert.Equal([owned], settlement.Citations);
        var checks = AuthorBriefDraftChecks.Run(objective, "main",
            new CliAuthorDraftCommandTests.FakeRepository());
        Assert.True(Assert.Single(checks, check => check.Name == "owner-sentence").Passed);
    }

    private static List<string> RemovalBullets() => Enumerable.Range(0, 50)
        .Select(i => $"test-removal: Example.Tests.FooTests.Removed{i:D2}").ToList();

    private sealed class Scenario : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("removal-refinement-").FullName;
        public AgentOrchestratorKernel Kernel { get; } = new();
        public TaskSpec Reviewer { get; } = new(TaskId.New(), "Review the diff", AgentRole.Reviewer);
        public FakeCollaborationItemStore Collaboration { get; } = new();
        public FakeSmokeProvider Provider { get; }
        public Goal Goal { get; }
        public GoalRefinementService Service { get; }

        public Scenario(IReadOnlyList<string> bullets, bool invalidOutput = false)
        {
            var objective = "Implement the behavior.\n\n## Acceptance criteria\n" +
                string.Join("\n", bullets.Select((text, index) => $"{index + 1}. {text}"));
            var response = invalidOutput ? "invalid output" : JsonSerializer.Serialize(new
            {
                behavioralContract = "Implement the behavior.",
                acceptanceCriteria = bullets.Select((text, index) => new
                {
                    text, declared_index = index + 1,
                    evidence_owner = text == Ordinary ? "worker" : "acceptance-gate"
                }),
                verificationClass = "TestVerifiable", decisions = Array.Empty<object>(), forks = Array.Empty<object>()
            });
            Provider = new FakeSmokeProvider(response, providerName: "fake-refiner");
            Goal = Kernel.CreateGoal(objective, [Reviewer]);
            Service = new GoalRefinementService(new InMemoryModelProviderRegistry([Provider]),
                new ModelFunctionCatalog([new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner,
                    ModelLane.CheapApi, new ModelProfile("fake-refiner", "fake-model",
                        ModelCapability.Text, SubscriptionMode.ApiKey))]),
                Collaboration, new SpecRefinerPrecedentStore(Path.Combine(_root, "precedents.json")),
                WorkerProfileCatalog.Default(), rawOutputDirectory: Path.Combine(_root, "logs"));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
