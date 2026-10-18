using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

// Parallel-safe: every run owns its root, index, journal, kernel and clock; no processes.
public sealed class GateFlakeLaneEarlyStopRegateScenarioTests
{
    private const string Identity =
        "AcceptanceExecutionCacheIsolatedBuildTests.ColdThenWarm_ProveExecutionProvenanceAndRejectStaleInputs";
    private const string TestSource =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AcceptanceExecutionCacheIsolatedBuildTests.cs";
    private const string Project =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string ConfirmingLane = "infrastructure tests: Remainder";
    private const string IdentitylessReason =
        "a failing check carries no test identity, so there is nothing to locate outside the candidate";

    // git diff --name-only a4edefca 590d4628: the incident candidate's five paths.
    private static readonly string[] IncidentScopes =
    [
        "src/Mcg.AgentOrchestrator.App/Application/GoalDispatchOperationsStart.cs",
        "src/Mcg.AgentOrchestrator.App/Orchestration/OfflineAssignedAgentRedelegation.cs",
        "src/Mcg.AgentOrchestrator.App/Orchestration/ProviderHoldAgentSubstitution.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OfflineAssignedAgentDispatchScenarioTests.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OfflineAssignedAgentRedelegationTests.cs"
    ];

    [Fact]
    public void Incident_PassedFocusedRerun_HoldsVerifiedWithoutWorker()
    {
        var run = RunScenario();

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(run.Outcome);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Contains("candidate-rerun-pass", held.Reason, StringComparison.Ordinal);
        Assert.Contains("1/2", held.Reason, StringComparison.Ordinal);
        Assert.Empty(run.Retries);
        Assert.Equal(WorkTaskStatus.Completed, run.Task.Status);
        Assert.Equal(0, run.Task.CriterionRetryCount);
        Assert.Equal(0, run.Goal.AutomaticAcceptanceRetryCount);
        Assert.Single(run.Entries.Where(entry =>
            entry.Operation == GoalOperationJournal.AcceptanceApparatusRegateOperation));
        Assert.DoesNotContain(run.Entries, entry =>
            entry.Operation == GoalOperationJournal.AcceptanceApparatusGenuineOperation);
        Assert.Single(run.Rows.Where(row => row.Kind == AcceptanceFailingTestIndexKinds.ApparatusRegate));
        var failure = Assert.Single(run.Rows.Where(row =>
            row.Kind == AcceptanceFailingTestIndexKinds.GateFailure));
        Assert.Equal(Identity, failure.TestIdentity);
        Assert.Equal("candidate-rerun-pass", failure.EvidenceKind);
        Assert.Equal(TestSource, failure.ResolvedSourcePath?.Replace('\\', '/'));
        Assert.False(failure.InsideChangedPaths);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Incident_PriorRegates_UsesExistingBoundWithoutWorker(int priorRegates)
    {
        var run = RunScenario(priorRegates: priorRegates);

        if (priorRegates == 1)
        {
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(run.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Contains("2/2", held.Reason, StringComparison.Ordinal);
            Assert.Contains("candidate-rerun-pass", held.Reason, StringComparison.Ordinal);
        }
        else
        {
            var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(run.Outcome);
            Assert.Contains("apparatus-regate-bound-exhausted", escalated.Reason, StringComparison.Ordinal);
            Assert.Contains("candidate-rerun-pass", escalated.Reason, StringComparison.Ordinal);
        }

        Assert.Empty(run.Retries);
        Assert.Equal(WorkTaskStatus.Completed, run.Task.Status);
        Assert.Equal(0, run.Task.CriterionRetryCount);
        Assert.Equal(0, run.Goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(2, run.Rows.Count(row => row.Kind == AcceptanceFailingTestIndexKinds.ApparatusRegate));
    }

    [Theory]
    [InlineData("failed-rerun", "failed again on the candidate")]
    [InlineData("no-rerun", "neither a recorded infrastructure signature")]
    [InlineData("inside-paths", "lives inside the candidate's changed paths")]
    [InlineData("no-identities", null)]
    public void OtherFailures_BaselineControl_PreservesDeveloperRetry(
        string shape, string? changedReason)
    {
        var baseline = RunScenario(shape, baseline: true);
        var changed = RunScenario(shape);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(baseline.Outcome);
        Assert.Equal(baseline.Outcome.GetType(), changed.Outcome.GetType());
        var baselineRetry = Assert.Single(baseline.Retries);
        var changedRetry = Assert.Single(changed.Retries);
        Assert.Equal(baseline.Task.Id, baselineRetry.TaskId);
        Assert.Equal(changed.Task.Id, changedRetry.TaskId);
        Assert.Equal(baselineRetry.TaskId, changedRetry.TaskId);
        Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, baselineRetry.Cause);
        Assert.Equal(baselineRetry.Cause, changedRetry.Cause);
        Assert.False(string.IsNullOrWhiteSpace(baselineRetry.Message));
        Assert.Equal(baselineRetry.Message, changedRetry.Message);
        Assert.Equal(WorkTaskStatus.Assigned, baseline.Task.Status);
        Assert.Equal(baseline.Task.Status, changed.Task.Status);
        Assert.Equal(1, baseline.Task.CriterionRetryCount);
        Assert.Equal(baseline.Task.CriterionRetryCount, changed.Task.CriterionRetryCount);
        var baselineRegates = baseline.Rows.Count(row =>
            row.Kind == AcceptanceFailingTestIndexKinds.ApparatusRegate);
        Assert.Equal(0, baselineRegates);
        Assert.Equal(baselineRegates, changed.Rows.Count(row =>
            row.Kind == AcceptanceFailingTestIndexKinds.ApparatusRegate));

        var baselineDetail = GenuineDetail(baseline);
        var changedDetail = GenuineDetail(changed);
        Assert.Equal(IdentitylessReason, baselineDetail);
        if (changedReason is null)
        {
            Assert.Equal(baselineDetail, changedDetail);
        }
        else
        {
            // Operator amendment: coverage changes the reason, while retry routing stays equal.
            Assert.Contains(changedReason, changedDetail, StringComparison.Ordinal);
            Assert.NotEqual(baselineDetail, changedDetail);
        }
    }

    private static string GenuineDetail(ScenarioRun run)
    {
        Assert.DoesNotContain(run.Entries, entry =>
            entry.Operation == GoalOperationJournal.AcceptanceApparatusRegateOperation);
        var entry = Assert.Single(run.Entries.Where(entry =>
            entry.Operation == GoalOperationJournal.AcceptanceApparatusGenuineOperation));
        return Assert.IsType<string>(entry.Detail);
    }

    private static ScenarioRun RunScenario(
        string shape = "incident", bool baseline = false, int priorRegates = 0)
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, TestSource);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath,
                "public sealed class AcceptanceExecutionCacheIsolatedBuildTests { " +
                "public void ColdThenWarm_ProveExecutionProvenanceAndRejectStaleInputs() { } }");
            var now = DateTimeOffset.Parse("2026-10-18T12:30:00Z");
            var index = new AcceptanceFailingTestIndex(
                Path.Combine(root, "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName));
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(new TaskId("7177dff022524b3fb562bcc462b9437b"),
                "Implement the scoped slice", AgentRole.Developer);
            var goal = kernel.CreateGoal(new GoalId("d39a96cd80184ce69856c1ec52d3eb8a"),
                "Lane early-stop incident replay", [task]);
            kernel.ActivateGoal(goal.Id, DefaultAgents());
            PassVerification(kernel, goal, task);
            for (var ordinal = 1; ordinal <= priorRegates; ordinal++)
            {
                index.Append([new AcceptanceFailingTestIndexRecord(
                    AcceptanceFailingTestIndex.ContractVersion,
                    AcceptanceFailingTestIndexKinds.ApparatusRegate,
                    goal.Id.Value, now.AddMinutes(-ordinal))], now);
            }

            var receipt = new AcceptanceLaneEarlyStop(ConfirmingLane, [], [],
                ["infrastructure tests: Goal worktree cleanup cohort"]).ToReceiptCheck();
            if (baseline)
                receipt = receipt with { CoveredBy = null };
            var noIdentities = shape == "no-identities";
            var attribution = new AcceptanceTestFailureAttribution(
                Identity,
                shape == "failed-rerun"
                    ? AcceptanceTestFailureOrigin.Introduced
                    : AcceptanceTestFailureOrigin.UnconfirmedIntroduced,
                "focused identity was green at merge-base main-a",
                shape == "no-rerun" ? null : new CandidateFailureRerunEvidence(
                    shape == "failed-rerun" ? "Failed" : "Passed", "candidate-rerun.trx"));
            AcceptanceCheckResult[] checks =
            [
                new(ConfirmingLane, false, 1, "Assert.Equal() Failure: Values differ",
                    FailingTestIdentities: noIdentities ? null : [Identity],
                    TestProjectPath: Project,
                    FailingTestAttributions: noIdentities ? null : [attribution]),
                receipt,
                new("infrastructure tests", false, 1, "Remainder failed",
                    CoveredBy: [ConfirmingLane])
            ];
            var summary = new AcceptanceVerificationSummary(false, checks,
                FailedChecks: checks.Select(check => check.Name).ToArray(),
                BranchHeadSha: "candidate-a", MainHeadSha: "main-a",
                CheckAttributions: checks.Select(check => new AcceptanceCheckAttribution(
                    check.Name, AcceptanceFailureOrigin.Introduced, "main green")).ToArray());
            IReadOnlyList<string> scopes = shape == "inside-paths"
                ? [.. IncidentScopes, TestSource] : IncidentScopes;
            var retries = new List<RetryObservation>();
            var acceptanceCalls = 0;
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ =>
                {
                    acceptanceCalls++;
                    return summary;
                },
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                {
                    retries.Add(new RetryObservation(taskId, cause, message));
                    return kernel.RetryTask(goalId, taskId, message,
                        retryRoundKind: roundKind, retryCause: cause);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => scopes,
                executionDirectory: root,
                apparatusRedGate: new ApparatusRedGate(index, _ => root, () => now));

            var outcome = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome;

            Assert.Equal(1, acceptanceCalls);
            return new ScenarioRun(outcome, goal, task, retries, index.Read(),
                GoalOperationJournal.Read(root, goal.Id).Entries);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record RetryObservation(TaskId TaskId, RetryCause Cause, string Message);

    private sealed record ScenarioRun(
        ConductorAdvanceOutcome Outcome,
        Goal Goal,
        TaskSpec Task,
        IReadOnlyList<RetryObservation> Retries,
        IReadOnlyList<AcceptanceFailingTestIndexRecord> Rows,
        IReadOnlyList<GoalOperationJournalEntry> Entries);
}
