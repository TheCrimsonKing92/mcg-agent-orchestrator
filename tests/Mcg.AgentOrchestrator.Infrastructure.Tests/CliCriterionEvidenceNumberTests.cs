using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique intent databases and async-context-local console capture; no process or environment mutation.
public sealed class CliCriterionEvidenceNumberTests
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Scope = "operator observation";
    private static readonly CliPersistentStateRunner.OperatorIntentAttribution Attribution =
        new("operator@example", "cli", "local-process");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MapQueuesZeroBasedPayloadAndConductorAppliesIt(bool explicitVersion)
    {
        using var fixture = new IntentFixture();
        var args = MapArguments(fixture.Goal, "7", explicitVersion ? "2" : null);
        CliCommandHelp.ThrowIfInvalidFlags(args);

        var output = InfrastructureTestSupport.CaptureConsole(() =>
            CliCriterionEvidenceIntents.Submit(args, fixture.Workspace, fixture.Goal, Attribution));

        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        var payload = JsonSerializer.Deserialize<CriterionEvidenceMappingOperatorIntentPayload>(
            intent.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Equal(new CriterionEvidenceMappingOperatorIntentPayload(6, 2,
            CriterionEvidenceOwner.Operator, Scope, "finding-seven", Candidate), payload);
        Assert.Contains("Target: criterion 7 (v2) criterion-v2-6 Requirement 7", output, StringComparison.Ordinal);
        Assert.True(output.IndexOf("Operator intent queued:", StringComparison.Ordinal) <
            output.IndexOf("Target:", StringComparison.Ordinal));
        Assert.Empty(fixture.Goal.CriterionEvidenceObligations);
        var restored = AgentOrchestratorKernel.FromSnapshot(
            new OrchestratorSnapshot([fixture.Kernel.ExportGoalSnapshot(fixture.Goal.Id)], []));
        var restoredGoal = restored.GetGoal(fixture.Goal.Id);

        var coordinator = new OperatorIntentCoordinator(fixture.Store);
        var result = coordinator.ExecutePending(restored, restoredGoal);
        coordinator.CompletePersisted([restoredGoal.Id]);

        Assert.True(result.MutatedGoalState);
        var obligation = Assert.Single(restoredGoal.CriterionEvidenceObligations);
        Assert.Equal("criterion-v2-6", obligation.Id);
        Assert.Equal(CriterionEvidenceOwner.Operator, obligation.Owner);
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store.GetAsync(intent.Id))!.Status);
    }

    [Theory]
    [InlineData("criterion-evidence-map")]
    [InlineData("criterion-evidence-repair")]
    public async Task PositionalIndexIsRejectedBeforeQueueing(string verb)
    {
        using var fixture = new IntentFixture();
        var args = new List<string> { verb, "--goal", fixture.Goal.Id.Value };
        if (verb == "criterion-evidence-repair") args.Add("criterion-v2-99");
        args.AddRange(["6", "2", "operator", Scope, "finding-seven", Candidate]);
        if (verb == "criterion-evidence-repair") args.Add("Repair malformed identity");

        var error = Assert.Throws<ArgumentException>(() =>
            CliCriterionEvidenceIntents.Submit(args, fixture.Workspace, fixture.Goal, Attribution));

        Assert.Equal($"{verb} takes --criterion <brief number> [--version <version>]; it no longer accepts a positional zero-based criterion index.", error.Message);
        Assert.Empty(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
    }

    public static TheoryData<string, string?, string> InvalidNumbers => new()
    {
        { "criterion-v2-6", "2", "'criterion-v2-6' is an obligation id; it names criterion 7 of version 2. Pass --criterion 7 --version 2." },
        { "criterion-v2-6", "invalid", "'criterion-v2-6' is an obligation id; it names criterion 7 of version 2. Pass --criterion 7 --version 2." },
        { "0", "2", "Criterion numbers are 1-based, as the brief numbers them." },
        { "8", "2", "Criterion 8 is not present in version 2, which has 7 criteria numbered 1 to 7." },
        { "8", null, "Criterion 8 is not present in version 2, which has 7 criteria numbered 1 to 7." },
        { "07", "2", "Criterion numbers are 1-based, as the brief numbers them." },
        { "+7", "2", "Criterion numbers are 1-based, as the brief numbers them." },
        { "-1", "2", "Criterion numbers are 1-based, as the brief numbers them." },
        { " 7", "2", "Criterion numbers are 1-based, as the brief numbers them." },
        { "7\n", "2", "Criterion numbers are 1-based, as the brief numbers them." },
        { "2147483648", "2", "Criterion numbers are 1-based, as the brief numbers them." },
        { "7", "0", "Criterion numbers are 1-based, as the brief numbers them." },
        { "7", "02", "Criterion numbers are 1-based, as the brief numbers them." },
        { "7", "+2", "Criterion numbers are 1-based, as the brief numbers them." },
        { "7", "-2", "Criterion numbers are 1-based, as the brief numbers them." },
        { "7", "2147483648", "Criterion numbers are 1-based, as the brief numbers them." }
    };

    [Theory]
    [MemberData(nameof(InvalidNumbers))]
    public async Task InvalidTargetIsRejectedBeforeEitherVerbQueues(string number, string? version, string message)
    {
        using var fixture = new IntentFixture();
        foreach (var verb in new[] { "criterion-evidence-map", "criterion-evidence-repair" })
        {
            var error = Assert.Throws<ArgumentException>(() => CliCriterionEvidenceIntents.Submit(
                TargetArguments(fixture.Goal, verb, number, version), fixture.Workspace, fixture.Goal, Attribution));
            Assert.Equal(message, error.Message);
            Assert.Empty(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    public async Task SupersededOrMissingVersionIsRejectedBeforeEitherVerbQueues(string version)
    {
        using var fixture = new IntentFixture();
        foreach (var verb in new[] { "criterion-evidence-map", "criterion-evidence-repair" })
        {
            var error = Assert.Throws<ArgumentException>(() => CliCriterionEvidenceIntents.Submit(
                TargetArguments(fixture.Goal, verb, "7", version), fixture.Workspace, fixture.Goal, Attribution));
            Assert.Equal($"Version {version} is not an active criterion version on goal {fixture.Goal.Id.Value}; its current criterion version is 2.", error.Message);
            Assert.Empty(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        }
    }

    [Theory]
    [InlineData("--criterion")]
    [InlineData("--version")]
    public async Task MissingOrDuplicateFlagValueIsRejectedBeforeEitherVerbQueues(string flag)
    {
        using var fixture = new IntentFixture();
        foreach (var verb in new[] { "criterion-evidence-map", "criterion-evidence-repair" })
        {
            foreach (var extra in new[] { new[] { flag }, new[] { flag, "2" } })
            {
                var args = TargetArguments(fixture.Goal, verb, "7", "2").Concat(extra).ToArray();
                var error = Assert.Throws<ArgumentException>(() =>
                    CliCriterionEvidenceIntents.Submit(args, fixture.Workspace, fixture.Goal, Attribution));
                Assert.Equal("Criterion numbers are 1-based, as the brief numbers them.", error.Message);
                Assert.Empty(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
            }
        }
    }

    [Fact]
    public async Task MissingAuthoritativeVersionIsRejectedBeforeQueueing()
    {
        using var fixture = new IntentFixture();
        var goal = fixture.Kernel.CreateGoal("No refined criterion version");
        var error = Assert.Throws<ArgumentException>(() => CliCriterionEvidenceIntents.Submit(
            MapArguments(goal, "1", null), fixture.Workspace, goal, Attribution));
        Assert.Equal($"Version none is not an active criterion version on goal {goal.Id.Value}; its current criterion version is none.", error.Message);
        Assert.Empty(await fixture.Store.ListForGoalAsync(goal.Id.Value));
    }

    [Fact]
    public void TargetShowsFirstEightyCharactersOnOneLine()
    {
        using var fixture = new IntentFixture();
        var text = "First line\r\n" + new string('x', 100);
        fixture.Kernel.RecordGoalRefinement(fixture.Goal.Id, new RefinedSpec("Long requirement", [text],
            VerificationClass.TestVerifiable, [], []));

        var output = InfrastructureTestSupport.CaptureConsole(() => CliCriterionEvidenceIntents.Submit(
            MapArguments(fixture.Goal, "1", null), fixture.Workspace, fixture.Goal, Attribution));

        var targetLine = Assert.Single(output.Split(Environment.NewLine), line =>
            line.StartsWith("Target:", StringComparison.Ordinal));
        Assert.Equal("Target: criterion 1 (v3) criterion-v3-0 " + ("First line " + new string('x', 100))[..80], targetLine);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepairAcceptsMalformedIdAndFlagsInEitherOrder(bool explicitVersion)
    {
        using var fixture = new IntentFixture();
        var args = new List<string> { "criterion-evidence-repair", "--goal", fixture.Goal.Id.Value, "criterion-v2-99" };
        if (explicitVersion) args.AddRange(["--version", "2"]);
        args.AddRange(["operator", Scope, "--criterion", "7", "finding-seven", Candidate, "Repair malformed identity"]);
        CliCommandHelp.ThrowIfInvalidFlags(args);

        var output = InfrastructureTestSupport.CaptureConsole(() =>
            CliCriterionEvidenceIntents.Submit(args, fixture.Workspace, fixture.Goal, Attribution));

        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        var payload = JsonSerializer.Deserialize<CriterionEvidenceRepairOperatorIntentPayload>(
            intent.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Equal(new CriterionEvidenceRepairOperatorIntentPayload("criterion-v2-99", 6, 2,
            CriterionEvidenceOwner.Operator, Scope, "finding-seven", Candidate, "Repair malformed identity"), payload);
        Assert.Contains("Target: criterion 7 (v2) criterion-v2-6 Requirement 7", output, StringComparison.Ordinal);
    }

    [Fact]
    public void KernelOutOfRangeMessageUsesBriefNumber()
    {
        var (kernel, goal) = CreateVersionTwoGoal();
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => kernel.MapCriterionEvidenceOwner(
            goal.Id, 7, 2, CriterionEvidenceOwner.Operator, "operator", expectedCandidateSha: Candidate));
        Assert.StartsWith("Criterion 8 is not present in version 2, which has 7 criteria numbered 1 to 7.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusedCarryHoldPrintsNumberedRebindCommand()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Rebind gate evidence");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Gate evidence", ["The gate passes"],
            VerificationClass.TestVerifiable, [], []));
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "operator", findingStableId: "original-finding", expectedCandidateSha: "old-candidate");

        var hold = AcceptanceCriterionEvidence.DescribeRefusedCarryHold(goal, Candidate, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, null);

        Assert.NotNull(hold);
        Assert.Contains($"criterion-evidence-map --goal {goal.Id.Value} --criterion 1 --version 1 acceptance acceptance:full-gate original-finding {Candidate}", hold.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectedReviewerAttestationPrintsNumberedRepairCommand()
    {
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Review worker criterion", [reviewer]);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Worker evidence", ["Implementation works"],
            VerificationClass.TestVerifiable, [], []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "fixture", "review", "C:\\fixture", DateTimeOffset.UtcNow));
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, Candidate);
        var output = """
            WORKER_RESULT:
            files: none
            commands: review
            tests: pass - deterministic fixture
            commit: none
            blockers: none
            findings: []
            touched_anchors: []
            criteria_verdicts: [{"criterion_index":0,"verdict":"not-verifiable","evidence":"full acceptance gate"}]
            verdict: pass
            model_fit: fixture/model - adequate - deterministic review
            skills: none
            confidence: high
            END_WORKER_RESULT
            """;

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 0, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true, ReviewedCommit: Candidate));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        var failure = Assert.Single(goal.Timeline, item => item.Kind == ProgressKind.TaskFailed);
        Assert.Contains("attestation rejected", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"--criterion 1 --version 1 acceptance acceptance:full-gate deferred-criterion-0 {Candidate}", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutstandingOperatorEvidenceShowsBriefNumber()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Display operator evidence");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Observe behavior", ["Implementation works", "Operator observes"],
            VerificationClass.RealWorldDependent, [], []) { OperatorOwnedAcceptanceCriteria = ["Operator observes"] });

        var diagnostic = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(goal, Candidate, kernel, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default);

        Assert.NotNull(diagnostic);
        Assert.Contains("criterion 2 (v1) criterion-v1-1:Operator:Pending", diagnostic, StringComparison.Ordinal);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateVersionTwoGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Map a numbered criterion");
        var spec = new RefinedSpec("Seven requirements",
            Enumerable.Range(1, 7).Select(number => $"Requirement {number}").ToArray(),
            VerificationClass.TestVerifiable, [], []);
        kernel.RecordGoalRefinement(goal.Id, spec);
        kernel.RecordGoalRefinement(goal.Id, spec);
        return (kernel, goal);
    }

    private static string[] MapArguments(Goal goal, string number, string? version) =>
        TargetArguments(goal, "criterion-evidence-map", number, version);

    private static string[] TargetArguments(Goal goal, string verb, string number, string? version)
    {
        var args = new List<string> { verb, "--goal", goal.Id.Value };
        if (verb == "criterion-evidence-repair") args.Add("criterion-v2-99");
        args.AddRange(["--criterion", number]);
        if (version is not null) args.AddRange(["--version", version]);
        args.AddRange(["operator", Scope, "finding-seven", Candidate]);
        if (verb == "criterion-evidence-repair") args.Add("Repair malformed identity");
        return args.ToArray();
    }

    private sealed class IntentFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("criterion-number-").FullName;
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public OrchestratorWorkspace Workspace { get; }
        public SqliteOperatorIntentStore Store { get; }

        public IntentFixture()
        {
            (Kernel, Goal) = CreateVersionTwoGoal();
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Store = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }
}
