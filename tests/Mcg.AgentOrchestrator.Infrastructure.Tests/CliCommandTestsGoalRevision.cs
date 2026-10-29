using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandTestsGoalRevision : CliCommandTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void CliReviseReportsResetOnlyForFailedRefinement(bool escalated)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Original brief");
        var store = SpecRefinementLaunchAttemptStore.ForWorkspace(workspace);
        if (escalated)
        {
            store.Save(goal.Id, new SpecRefinementLaunchAttempt(DateTimeOffset.UtcNow, 3, DateTimeOffset.UtcNow,
                GoalRefinementWorkCoordinator.MessageId(goal.Id), workspace.SqliteStatePath));
        }

        var output = ExecuteCliAndCapture(["revise", goal.Id.Value, "Revised brief"], kernel, workspace);

        Xunit.Assert.Contains(escalated ? "refined-criteria=reset" : "refined-criteria=unchanged",
            output, StringComparison.Ordinal);
        Xunit.Assert.Equal("Revised brief", goal.Objective);
        Xunit.Assert.Null(store.Get(goal.Id));
        Xunit.Assert.Equal(escalated, GoalRefinementWorkCoordinator.HasPendingWork(goal));
        if (escalated)
        {
            var state = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
                .GetOutboxStateAsync(GoalRefinementWorkCoordinator.MessageId(goal.Id)).GetAwaiter().GetResult();
            Xunit.Assert.Equal(OrchestratorStateOutboxStatus.Pending, state!.Status);
        }
    }

    [Xunit.Fact]
    public void CliReviseResetsStoredLifecycleRepairWithUnchangedCriteria()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Original prose\n\n## Acceptance criteria\n\n1. Observe service.");
        kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id) with
        {
            RefinedSpec = new RefinedSpecSnapshot("Legacy contract", ["Observe service."], "TestVerifiable", [], [],
                CriterionEvidenceDeclarations: [new("Observe service.", CriterionEvidencePhase.PostLanding,
                    new(CriterionObservationKind.Runtime, "healthy", "service"), Atomic: true)])
        });
        goal = kernel.GetGoal(goal.Id);
        var repair = Xunit.Assert.Single(goal.CriterionEvidenceObligations);
        Xunit.Assert.Equal("lifecycle declaration repair required", repair.RequiredScope);
        Xunit.Assert.StartsWith(CriterionEvidenceLifecycle.AtomicDeclarationRequired, repair.Provenance, StringComparison.Ordinal);
        Xunit.Assert.Contains("Offending criterion: 'Observe service.'; failed rule: owner.", repair.Provenance, StringComparison.Ordinal);

        var output = ExecuteCliAndCapture(["revise", goal.Id.Value,
            "Revised prose\n\n## Acceptance criteria\n\n1. Observe service."], kernel, workspace);

        Xunit.Assert.Contains("refined-criteria=reset", output, StringComparison.Ordinal);
        Xunit.Assert.Null(goal.RefinedSpec);
        Xunit.Assert.Empty(goal.RefinedSpecVersions);
        Xunit.Assert.Empty(goal.CriterionEvidenceObligations);
        Xunit.Assert.True(GoalRefinementWorkCoordinator.HasPendingWork(goal));
    }

    // Parallel-safe: every test owns a unique temp root and SQLite database; no process or global state is used.
    [Xunit.Fact]
    public async Task SqliteGoalBriefVersionsRoundTripWithoutLosingSupersededText()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Persisted original", [new TaskSpec(TaskId.New(), "work", AgentRole.Developer)]);
        kernel.ReviseGoalBrief(goal.Id, "Persisted revision", "audit reason");

        await repository.SaveAsync(kernel);
        var restored = await repository.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);

        Xunit.Assert.Equal("Persisted revision", restoredGoal.Objective);
        Xunit.Assert.Equal(["Persisted original", "Persisted revision"], restoredGoal.BriefVersions.Select(version => version.Text));
        Xunit.Assert.Equal(2, restoredGoal.BriefVersions[0].SupersededByVersion);
        Xunit.Assert.Equal("audit reason", restoredGoal.AuthoritativeBrief.Reason);
    }

    [Xunit.Fact]
    public void CliReviseUpdatesBriefAndHistoryRendersAllVersions()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Original CLI brief", [new TaskSpec(TaskId.New(), "work", AgentRole.Developer)]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Original CLI behavior",
            ["Original CLI criterion"],
            VerificationClass.TestVerifiable,
            [],
            []));

        var revisedBriefPath = Path.Combine(root, "revised-cli-brief.md");
        File.WriteAllText(revisedBriefPath, """
            Revised CLI brief

            ## Acceptance criteria

            1. First revised criterion.
            2. Second revised criterion.
            """);
        var parts = CliArgumentParser.SplitCommand(
            $"revise {goal.Id.Value[..8]} --brief-file {revisedBriefPath} --reason narrower slice");
        var output = ExecuteCliAndCapture(parts, kernel, workspace);
        var history = ExecuteCliAndCapture(["revise", goal.Id.Value[..8], "--history"], kernel, workspace);

        Xunit.Assert.StartsWith("Revised CLI brief", goal.Objective, StringComparison.Ordinal);
        Xunit.Assert.Contains("authoritative=v2", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("refined-criteria=2", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("  refined acceptance criteria:", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("    2. Second revised criterion.", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("completed-unchanged=0", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("v1 superseded-by=v2", history, StringComparison.Ordinal);
        Xunit.Assert.Contains("Original CLI brief", history, StringComparison.Ordinal);
        Xunit.Assert.Contains("v2 authoritative", history, StringComparison.Ordinal);
        Xunit.Assert.Contains("reason=narrower slice", history, StringComparison.Ordinal);
        Xunit.Assert.Contains("Revised CLI brief", history, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void CliReviseInteractiveAndOneShotArgumentsMatch()
    {
        var interactive = CliArgumentParser.SplitCommand(
            "revise abcdef12 Revised brief --reason narrower slice");
        var oneShot = CliArgumentParser.NormalizeArgs(
            ["revise", "abcdef12", "Revised", "brief", "--reason", "narrower", "slice"]);

        Xunit.Assert.Equal(interactive, oneShot);
    }

    [Xunit.Fact]
    public void CliReviseAcceptsBriefFileAndOptionalReasonFile()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Original", [new TaskSpec(TaskId.New(), "work", AgentRole.Developer)]);
        var briefPath = Path.Combine(root, "revised-brief.md");
        var reasonPath = Path.Combine(root, "revision-reason.txt");
        File.WriteAllText(briefPath, "Revised from file");
        File.WriteAllText(reasonPath, "Operator chose a smaller slice");

        ExecuteCliAndCapture(
            ["revise", goal.Id.Value[..8], "--brief-file", briefPath, "--reason-file", reasonPath],
            kernel,
            workspace);

        Xunit.Assert.Equal("Revised from file", goal.Objective);
        Xunit.Assert.Equal("Operator chose a smaller slice", goal.AuthoritativeBrief.Reason);
    }

    [Xunit.Fact]
    public void CliReviseAcceptsBriefFromStandardInput()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Original", [new TaskSpec(TaskId.New(), "work", AgentRole.Developer)]);
        ExecuteCliAndCapture(
            ["revise", goal.Id.Value[..8], "--brief-file", "-"],
            kernel,
            workspace,
            standardInput: new StringReader("Revised from stdin"),
            isStandardInputRedirected: true);

        Xunit.Assert.Equal("Revised from stdin", goal.Objective);
        Xunit.Assert.Contains("- reads stdin for revise file flags", CliCommandHelp.ReviseUsage, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void CliReviseRejectsInteractiveStandardInputWithoutReading()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Original", [new TaskSpec(TaskId.New(), "work", AgentRole.Developer)]);

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => ExecuteCliAndCapture(
            ["revise", goal.Id.Value[..8], "--brief-file", "-"],
            kernel,
            workspace,
            standardInput: new FailOnReadTextReader(),
            isStandardInputRedirected: false));

        Xunit.Assert.Equal("Standard input is not redirected; pipe content or provide a file.", error.Message);
        Xunit.Assert.Equal("Original", goal.Objective);
    }

    [Xunit.Fact]
    public void StandardInputMarkerRemainsAFilePathOutsideRevise()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();

        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            ExecuteCliAndCapture(["goal", "--brief-file", "-"], kernel, workspace));

        Xunit.Assert.Equal("--brief-file not found: -", error.Message);
        Xunit.Assert.Empty(kernel.Goals);
    }

    private sealed class FailOnReadTextReader : StringReader
    {
        public FailOnReadTextReader()
            : base(string.Empty)
        {
        }

        public override string ReadToEnd() =>
            throw new Xunit.Sdk.XunitException("Interactive stdin must be rejected before it is read.");
    }
}
