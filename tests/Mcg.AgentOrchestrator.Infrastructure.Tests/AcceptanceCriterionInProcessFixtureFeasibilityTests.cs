using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable criterion data and a private temporary directory per refinement.
public sealed class AcceptanceCriterionInProcessFixtureFeasibilityTests
{
    private const string LiveCategory = "live-conductor-or-multiple-ticks";

    // Verbatim incident criterion from goal 3bf61f77, supplied in this goal's objective.
    private const string IncidentCriterion = """
        END-TO-END incident replay in `ConductorStartupStatusProjectionTests` through `CliConductorCommand.Run(["conductor","status"])` and `(["conductor","status","--json"])`, both using the real `SystemConductorLockProbe` with a fixture pid probe reporting the same process. The fixture lock reads pid 39752, acquired 2026-10-13T16:11:46Z, process started 2026-10-13T16:11:40Z. The log holds `loop-stop` at 2026-10-13T15:41:15.8870694Z (`LOOP_STOP tick=9 reason=stop-file`) and `loop-ready` at 2026-10-13T16:11:47Z. (a) Before start: the text contains `Conductor: starting (pid 39752)`, `Starting since 2026-10-13T16:11:46.0000000+00:00; stage=loop-ready` and `Previous stop: 2026-10-13T15:41:15.8870694+00:00; reason=stop-file (earlier generation)`, and does not contain `Stopped since ` or `Conductor: running`. The JSON has `conductor.state` `starting`, `conductor.pid` 39752, `since.kind` `starting`, `since.at` and `since.reason` matching the text, and `since.previous.at` and `since.previous.reason` matching the Previous line. (b) After appending `loop-start` at 2026-10-13T16:12:19Z: the text contains `Conductor: running (pid 39752)` and `Running since 2026-10-13T16:12:19.0000000+00:00` and no `Previous stop:` line, and the JSON has `running`, `since.kind` `running` at the same instant, and `since.previous` null. (c) The lock file and conduct-events log bytes are unchanged after every status call. Fails before: step (a) prints `Conductor: running (pid 39752)` together with `Stopped since ...; reason=stop-file` (descriptive: shown by the diff; no earlier run is required). Developer owns; Acceptance executes. TEST-VERIFIABLE.
        """;

    private static readonly string StrippedCriterion = Regex.Replace(
        IncidentCriterion.Replace(
            "`CliConductorCommand.Run([\"conductor\",\"status\"])`",
            "`conductor status`",
            StringComparison.Ordinal),
        @"\bfixture\b", "sample", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] LiveSentences =
    [
        "Repeats against the live board.",
        "Repeats on the running conductor.",
        "Repeats inside the conductor loop.",
        "Repeats on the next tick of the loop."
    ];

    [Xunit.Fact]
    public async Task IncidentCriterion_RefinesToAutoRefinedWithNoFeasibilityQuestion()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var (service, kernel, goalId, collaboration) = BuildScenario(tempDirectory);

            var result = await service.RefineAsync(kernel, goalId);

            Xunit.Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
            Xunit.Assert.Empty(result.Spec.OpenQuestions);
            Xunit.Assert.Equal([IncidentCriterion], result.Spec.AcceptanceCriteria);
            Xunit.Assert.Empty(collaboration.Items);
            Xunit.Assert.Empty(AcceptanceCriterionFeasibility.Evaluate(
                [IncidentCriterion], AgentRole.Developer));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Evaluate_PositiveControls_EqualBaselineWithNonEmptyFindings()
    {
        // Positive rows from AcceptanceCriterionFeasibilityTests (including composite demands).
        (string Text, bool IncludesLiveCategory)[] criteria =
        [
            ("Measure before/after gate makespan on this host by driving two concurrent gates through the real conductor while the machine is otherwise idle.", false),
            ("Drive two concurrent goals through dispatch and compare their results.", false),
            ("Demonstrate the recovery behavior across multiple conductor ticks.", true),
            ("Benchmark wall-clock performance on this host while the machine is idle.", false),
            ("Reserve an acceptance slot and hold it until the competing build completes.", false),
            ("Show the new test RED without its fix while exercising recovery across two ticks.", true)
        ];

        // Positive rows from AcceptanceCriterionExecutingRoleFeasibilityTests retain the executor.
        (string Text, bool IncludesLiveCategory)[] developerCriteria =
        [
            ("Drive two concurrent goals through dispatch and compare their results.", false),
            ("Run the real conductor loop across multiple conductor ticks and observe recovery.", true),
            ("Drive the live conductor and observe recovery.", true),
            ("Run the real conductor loop.", true),
            ("Run the real conductor loops and assert recovery.", true),
            ("Run the real conductor looping and assert recovery.", true),
            ("Run two concurrent goals.", false),
            ("Two concurrent goals run through dispatch.", false),
            ("Add tests that run two concurrent goals and assert both finish.", false),
            ("Two concurrent goals have tests that run to completion.", false),
            ("Add tests that run the real conductor loop and assert recovery.", true)
        ];

        foreach (var (text, includesLiveCategory) in criteria.Concat(developerCriteria.Select(row =>
                     (row.Text + " Developer owns; Developer executes.", row.IncludesLiveCategory))))
        {
            AssertBaselineEqual(text);
            if (!includesLiveCategory)
                AssertBaselineEqual(text + " Uses fixture data.");
        }

        foreach (var sentence in LiveSentences)
            AssertBaselineEqual(IncidentCriterion + " " + sentence);
    }

    [Xunit.Fact]
    public void Evaluate_StrippedIncident_RequiresLiveCapability()
    {
        AssertSingleLiveFinding(StrippedCriterion, honorInProcessMarkers: true);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Uses fixture data.")]
    [Xunit.InlineData("Stays in-process.")]
    [Xunit.InlineData("Writes under a per-test temporary directory.")]
    [Xunit.InlineData("Reads `ConductorStatusSnapshot.Read(root)`.")]
    public void Evaluate_InProcessMarker_ClearsStrippedIncident(string markerSentence)
    {
        Xunit.Assert.Empty(AcceptanceCriterionFeasibility.Evaluate(
            [StrippedCriterion + " " + markerSentence], AgentRole.Developer,
            honorInProcessMarkers: true));
    }

    [Xunit.Fact]
    public void Evaluate_MultipleTicks_DistinguishesFixtureFromPlainDemand()
    {
        const string fixtureCriterion =
            "Verify recovery across two consecutive ticks of `ConductorDriver.AdvanceOnce(goal)` on a fixture clock. Developer owns; Acceptance executes.";
        const string plainCriterion =
            "Verify recovery across two consecutive ticks of the driver on a sample clock. Developer owns; Acceptance executes.";

        Xunit.Assert.Empty(AcceptanceCriterionFeasibility.Evaluate(
            [fixtureCriterion], AgentRole.Developer, honorInProcessMarkers: true));
        AssertSingleLiveFinding(plainCriterion, honorInProcessMarkers: true);
    }

    [Xunit.Fact]
    public void Evaluate_MarkersDisabled_RetainsIncidentBaselineFinding()
    {
        AssertSingleLiveFinding(IncidentCriterion, honorInProcessMarkers: false);
    }

    private static void AssertBaselineEqual(string criterion)
    {
        var baseline = AcceptanceCriterionFeasibility.Evaluate(
            [criterion], AgentRole.Developer, honorInProcessMarkers: false);
        var actual = AcceptanceCriterionFeasibility.Evaluate(
            [criterion], AgentRole.Developer, honorInProcessMarkers: true);

        Xunit.Assert.NotEmpty(baseline);
        Xunit.Assert.NotEmpty(actual);
        Xunit.Assert.Equal(baseline.Count, actual.Count);
        for (var index = 0; index < baseline.Count; index++)
        {
            Xunit.Assert.Equal(baseline[index].Criterion, actual[index].Criterion);
            Xunit.Assert.Equal(baseline[index].Role, actual[index].Role);
            Xunit.Assert.NotEmpty(baseline[index].TriggerCategories);
            Xunit.Assert.NotEmpty(actual[index].TriggerCategories);
            Xunit.Assert.NotEmpty(baseline[index].MissingCapabilities);
            Xunit.Assert.NotEmpty(actual[index].MissingCapabilities);
            Xunit.Assert.True(baseline[index].TriggerCategories.SequenceEqual(actual[index].TriggerCategories));
            Xunit.Assert.True(baseline[index].MissingCapabilities.SequenceEqual(actual[index].MissingCapabilities));
        }
    }

    private static void AssertSingleLiveFinding(string criterion, bool honorInProcessMarkers)
    {
        var finding = Xunit.Assert.Single(AcceptanceCriterionFeasibility.Evaluate(
            [criterion], AgentRole.Developer, honorInProcessMarkers));

        Xunit.Assert.Equal(criterion, finding.Criterion);
        Xunit.Assert.Equal("Acceptance", finding.Role);
        Xunit.Assert.Equal([LiveCategory], finding.TriggerCategories);
        Xunit.Assert.Equal([WorkerCapability.LiveConductorControl], finding.MissingCapabilities);
    }

    private static string BuildFeasibilityJson() => $$"""
        ```json
        {
          "behavioralContract": "Exercise the requested behavior.",
          "acceptanceCriteria": [{{JsonSerializer.Serialize(IncidentCriterion)}}],
          "verificationClass": "TestVerifiable",
          "decisions": [],
          "forks": []
        }
        ```
        """;

    private static (
        GoalRefinementService Service,
        AgentOrchestratorKernel Kernel,
        GoalId GoalId,
        FakeCollaborationItemStore Collaboration) BuildScenario(string tempDirectory)
    {
        var provider = new FakeSmokeProvider(text: BuildFeasibilityJson(), providerName: "fake-refiner");
        var registry = new InMemoryModelProviderRegistry([provider]);
        var catalog = new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]);
        var collaboration = new FakeCollaborationItemStore();
        var service = new GoalRefinementService(
            registry,
            catalog,
            collaboration,
            new SpecRefinerPrecedentStore(Path.Combine(tempDirectory, "precedents.json")),
            rawOutputDirectory: Path.Combine(tempDirectory, "logs"));
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Integrate the billing system");
        return (service, kernel, goal.Id, collaboration);
    }
}
