using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsFindingEvidencePlanning : IDisposable
{
    private const string Candidate = "548214df";
    private const string DashboardProject =
        "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj";
    private readonly string _artifacts = InfrastructureTestSupport.CreateTempDirectory();

    private static AcceptanceGateEngineSettings IncidentSettings()
    {
        var settings = new AcceptanceGateEngineSettings
        {
            MtpInvocations = [new AcceptanceMtpInvocation { Project = DashboardProject }]
        };
        // The historical Dashboard alias was retired. Use the declared path so the
        // incident's third project reaches union planning instead of being refused.
        Assert.True(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(
            DashboardProject, settings, out var resolvedProject));
        Assert.Equal(DashboardProject, resolvedProject);
        return settings;
    }

    // Infrastructure names are from the operator's b7d579bd historical package.
    // The Core/Dashboard pipe selections and eight-class union are the objective's shape.
    private static readonly FindingEvidenceSelection[] Expected =
    [
        new("Core.Tests", "DispatchExecutionTests"),
        new("Core.Tests", "TaskProcessTests"),
        new("Infrastructure.Tests", "CliCommandTestsPersistentRunnerCommands"),
        new("Infrastructure.Tests", "WorkerDispatchTestsDispatchPreparation"),
        new("Infrastructure.Tests", "CliCommandTestsPersistentRunnerCommandsDispatchRecovery"),
        new("Infrastructure.Tests", "SqliteOrchestratorStateRepositoryTests"),
        new(DashboardProject, "DashboardRenderingTests"),
        new(DashboardProject, "GoalScopeCollisionDashboardMapperTests")
    ];

    private static ReviewFinding[] Requests()
    {
        var core = EvidenceFindingWithRequest("Core receipt missing.", id: "core", project: "Core.Tests",
            classes: [" FullyQualifiedName~TaskProcessTests | FullyQualifiedName~DispatchExecutionTests "]);
        var infrastructure = EvidenceFindingWithRequest("Infrastructure receipt missing.", id: "infrastructure",
            classes: Expected.Where(selection => selection.TestProject == "Infrastructure.Tests")
                .Select(selection => selection.TestClass).ToArray());
        var dashboard = EvidenceFindingWithRequest("Dashboard receipt missing.", id: "dashboard", project: DashboardProject,
            classes: ["DashboardRenderingTests|GoalScopeCollisionDashboardMapperTests"]);
        var union = EvidenceFindingWithRequest("Union receipt missing.", id: "union") with
        {
            EvidenceRequest = new FindingEvidenceRequest(Expected.Reverse().Concat([Expected[0]]).ToArray())
        };
        return [core, infrastructure, dashboard, union];
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Reviewer) Arrange(
        IReadOnlyList<ReviewFinding> findings)
    {
        var (kernel, goal) = SoftwareGoal();
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: findings.ToArray(), reviewedCommit: Candidate);
        return (kernel, goal, reviewer);
    }

    private ConductorDriver Driver(AgentOrchestratorKernel kernel,
        Func<Goal, string, FocusedEvidenceRunResult> run,
        Action<IReadOnlyList<FindingEvidenceRequestDisposition>>? onPlan = null,
        Func<Goal, DispatchStartOutcome>? start = null,
        string candidate = Candidate,
        AcceptanceGateEngineSettings? settings = null) => MakeDriver(
        getPreReviewEvidenceContext: _ => NoPreReviewContext(candidate),
        getFindingEvidenceEngineSettings: _ => settings ?? IncidentSettings(),
        runFocusedEvidence: run,
        dispatchAndStart: start,
        // Rejection must make the Developer eligible immediately. Hold the subsequent
        // worker admission so this fixture measures planning/retry without starting a worker.
        getWorkerAdmissionCapacity: start is null ? null : () => 0,
        retryTaskWithRoundKind: (goalId, taskId, message, kind) =>
            kernel.RetryTask(goalId, taskId, message, retryRoundKind: kind),
        recordFindingEvidenceRequestDispositions: (goalId, taskId, dispositions) =>
        {
            kernel.RecordFindingEvidenceRequestDispositions(goalId, taskId, dispositions);
            onPlan?.Invoke(dispositions);
        },
        recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
            kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

    [Xunit.Fact]
    public void RejectedIncidentCandidate_DefersEveryRequestWithoutStartingEvidence()
    {
        var requests = Requests();
        var blocker = new ReviewFinding("conduct-single-goal-start-claim-missing-durable-checkpoint",
            ReviewFindingState.Open, new("src/dispatch.cs", "Start"),
            "Prepared generation cannot be persisted before the durable claim.",
            FindingSeverity.Blocking, FindingCategory.Correctness);
        var (kernel, goal, reviewer) = Arrange([blocker, .. requests]);
        var focusedRuns = 0;
        var workerStarts = 0;
        var result = Driver(kernel, (_, request) =>
        {
            focusedRuns++;
            return PassingPreReviewEvidence(request);
        }, start: _ => { workerStarts++; return DispatchStartOutcome.Started(); })
            .AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(0, workerStarts);
        Assert.Equal(ConductorHoldOwner.WorkerCapacity,
            Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome).Owner);
        Assert.Equal(WorkTaskStatus.Assigned,
            goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer).Status);
        var durable = Reload(kernel).Goals.Single().Tasks.Single(task => task.Id == reviewer.Id);
        Assert.Empty(durable.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? []));
        var decisions = durable.VerificationHistory.SelectMany(record => record.FindingEvidenceRequestDispositions ?? []).ToArray();
        Assert.Equal(4, decisions.Length);
        Assert.All(decisions, decision =>
        {
            Assert.Equal(Candidate, decision.CandidateSha);
            Assert.StartsWith("finding-round-", decision.FindingRoundFingerprint);
            Assert.Equal(FindingEvidenceRequestDecision.DeferredToNextCandidate, decision.Decision);
            Assert.Equal("unresolved-writable-blockers-on-unchanged-candidate", decision.Reason);
            Assert.Null(decision.SourceReceiptIds);
        });
        Assert.All(requests, request => Assert.Equal(request.EvidenceRequest!.Selections,
            reviewer.VerificationHistory.Last().MergedReviewFindings!.Single(finding => finding.StableId == request.StableId).EvidenceRequest!.Selections));
    }

    [Xunit.Fact]
    public void IncidentUnion_ExecutesEachClassOnceAndSharesPendingAndCompletedReceipt()
    {
        var (kernel, goal, reviewer) = Arrange(Requests());
        var runs = 0;
        var counts = Expected.ToDictionary(selection => $"{selection.TestProject}:{selection.TestClass}", _ => 0);
        var pendingObserved = false;
        var driver = Driver(kernel, (_, request) =>
        {
            runs++;
            Assert.True(pendingObserved);
            var selections = ExecuteFixtureSelections(request);
            foreach (var selection in selections) counts[selection]++;
            return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                _artifacts, request, Candidate, selections.Select(selection => selection.Split(':')[1]).ToArray());
        }, onPlan: decisions =>
        {
            if (decisions.All(decision => decision.SourceReceiptIds?.Count > 0)) return;
            pendingObserved = true;
            Assert.Equal(4, decisions.Count);
            Assert.Equal(FindingEvidenceRequestDecision.PendingExecution,
                decisions.Single(decision => decision.FindingStableId == "union").Decision);
            Assert.All(decisions.Where(decision => decision.FindingStableId != "union"), decision =>
            {
                Assert.Equal(FindingEvidenceRequestDecision.CoveredByPendingRequest, decision.Decision);
                Assert.NotNull(decision.CoveringRequestIdentity);
                Assert.Empty(decision.SourceReceiptIds ?? []);
            });
        });
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, runs);
        Assert.All(counts.Values, count => Assert.Equal(1, count));
        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        var decisions = reviewer.VerificationHistory.Last().FindingEvidenceRequestDispositions!;
        Assert.Equal(4, decisions.Count);
        Assert.All(decisions, decision => Assert.Equal(receipt.ReceiptId, Assert.Single(decision.SourceReceiptIds!)));
        Assert.Equal(FindingEvidenceRequestDecision.Executed, decisions.Single(decision => decision.FindingStableId == "union").Decision);
        Assert.All(decisions.Where(decision => decision.FindingStableId != "union"),
            decision => Assert.Equal(FindingEvidenceRequestDecision.CoveredByReceipt, decision.Decision));

        kernel.RefreshTrackedGoals(Reload(kernel).ExportSnapshot());
        driver.AdvanceOnce(kernel.Goals.Single(), ConductorAutonomyPolicy.Permissive);
        Assert.Equal(1, runs);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void IncidentUnion_CompletedCoreReceiptExecutesOnlyUncoveredClasses(bool omitted)
    {
        var (kernel, goal, reviewer) = Arrange(Requests());
        SeedCoreReceipt(kernel, goal, reviewer, omitted);
        var counts = Expected.ToDictionary(selection => $"{selection.TestProject}:{selection.TestClass}", _ => 0);
        var runs = 0;
        Driver(kernel, (_, request) =>
        {
            runs++;
            var selections = ExecuteFixtureSelections(request);
            foreach (var selection in selections) counts[selection]++;
            return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                _artifacts, request, Candidate, selections.Select(selection => selection.Split(':')[1]).ToArray());
        }).AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, runs);
        Assert.Equal(0, counts["Core.Tests:DispatchExecutionTests"]);
        Assert.Equal(omitted ? 1 : 0, counts["Core.Tests:TaskProcessTests"]);
        Assert.All(counts.Where(item => !item.Key.StartsWith("Core.Tests:")).Select(item => item.Value),
            count => Assert.Equal(1, count));
        var union = reviewer.VerificationHistory.Last().FindingEvidenceRequestDispositions!
            .Single(decision => decision.FindingStableId == "union");
        Assert.Contains("core-receipt", union.SourceReceiptIds!);
        Assert.Equal(2, union.SourceReceiptIds!.Count);
    }

    [Xunit.Theory]
    [Xunit.InlineData("(FullyQualifiedName~DispatchExecutionTests|FullyQualifiedName~TaskProcessTests)")]
    [Xunit.InlineData("((FullyQualifiedName~TaskProcessTests)|(FullyQualifiedName~DispatchExecutionTests))")]
    public void ParenthesizedCoreReceipt_UnionExecutesNoCoveredClasses(string filter)
    {
        var (kernel, goal, reviewer) = Arrange(Requests());
        SeedCoreReceipt(kernel, goal, reviewer, false, filter: filter);
        kernel = Reload(kernel);
        goal = kernel.Goals.Single();
        reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var counts = Expected.ToDictionary(selection => $"{selection.TestProject}:{selection.TestClass}", _ => 0);
        var runs = 0;
        Driver(kernel, (_, request) =>
        {
            runs++;
            var selections = ExecuteFixtureSelections(request);
            foreach (var selection in selections) counts[selection]++;
            return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                _artifacts, request, Candidate, selections.Select(selection => selection.Split(':')[1]).ToArray());
        }).AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, runs);
        Assert.All(counts.Where(item => item.Key.StartsWith("Core.Tests:")).Select(item => item.Value),
            count => Assert.Equal(0, count));
        Assert.All(counts.Where(item => !item.Key.StartsWith("Core.Tests:")).Select(item => item.Value),
            count => Assert.Equal(1, count));
        var union = reviewer.VerificationHistory.Last().FindingEvidenceRequestDispositions!
            .Single(decision => decision.FindingStableId == "union");
        Assert.Equal(FindingEvidenceRequestDecision.Executed, union.Decision);
        Assert.Contains("core-receipt", union.SourceReceiptIds!);
        Assert.Equal(2, union.SourceReceiptIds!.Count);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void PartialUnion_ReloadKeepsSkippedMembersIncomplete(bool actionable)
    {
        var (kernel, goal, reviewer) = Arrange(Requests());
        var runs = 0;
        var counts = Expected.ToDictionary(selection => $"{selection.TestProject}:{selection.TestClass}", _ => 0);
        var skipped = Expected.Where(selection => selection.TestProject != "Core.Tests").ToArray();
        Driver(kernel, (_, request) =>
        {
            runs++;
            Assert.Equal(8, ExecuteFixtureSelections(request).Length);
            foreach (var selection in Expected.Except(skipped))
                counts[$"{selection.TestProject}:{selection.TestClass}"]++;
            var red = CandidateRedFindingEvidence(request, Candidate, "DispatchExecutionTests.Runs");
            return red with
            {
                Arms = red.Arms!.Select(arm => arm.Arm == FindingEvidenceArm.Candidate
                    ? arm with
                    {
                        Checks = arm.Checks.Select(check => check with { ExecutedTestCount = 2 }).ToArray(),
                        NotRunSelections = skipped
                    }
                    : arm with { Disposition = actionable
                        ? FindingEvidenceArmDisposition.Green : FindingEvidenceArmDisposition.Red,
                        Passed = actionable }).ToArray()
            };
        }).AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, runs);
        Assert.All(counts.Where(item => item.Key.StartsWith("Core.Tests:")).Select(item => item.Value),
            count => Assert.Equal(1, count));
        Assert.All(counts.Where(item => !item.Key.StartsWith("Core.Tests:")).Select(item => item.Value),
            count => Assert.Equal(0, count));
        var durable = Reload(kernel).Goals.Single().Tasks.Single(task => task.Id == reviewer.Id);
        var receipt = Assert.Single(durable.VerificationHistory.Last().FindingEvidenceReceipts!);
        var decisions = durable.VerificationHistory.Last().FindingEvidenceRequestDispositions!;
        Assert.Equal(4, decisions.Count);
        var core = decisions.Single(decision => decision.FindingStableId == "core");
        Assert.Equal(FindingEvidenceRequestDecision.CoveredByReceipt, core.Decision);
        Assert.Equal(receipt.ReceiptId, Assert.Single(core.SourceReceiptIds!));
        Assert.All(decisions.Where(decision => decision.FindingStableId != "core"), decision =>
        {
            Assert.Equal(actionable ? FindingEvidenceRequestDecision.DeferredToNextCandidate
                : FindingEvidenceRequestDecision.PendingExecution, decision.Decision);
            Assert.Equal("incomplete", decision.Disposition);
            Assert.Equal("candidate-selections-not-executed", decision.Reason);
            Assert.Equal(Candidate, decision.CandidateSha);
            Assert.Equal(receipt.FindingRoundFingerprint, decision.FindingRoundFingerprint);
            Assert.Null(decision.CoveringRequestIdentity);
            Assert.Equal(receipt.ReceiptId, Assert.Single(decision.SourceReceiptIds!));
            var finding = durable.VerificationHistory.Last().MergedReviewFindings!
                .Single(finding => finding.StableId == decision.FindingStableId);
            Assert.Equal(FindingEvidenceExecutionState.PendingExecution,
                FindingEvidenceExecutionClassifier.Classify(durable, finding, Candidate));
        });
        var coreFinding = durable.VerificationHistory.Last().MergedReviewFindings!
            .Single(finding => finding.StableId == "core");
        Assert.Equal(FindingEvidenceExecutionState.ExecutedOnCandidate,
            FindingEvidenceExecutionClassifier.Classify(durable, coreFinding, Candidate));
        Assert.Equal(FindingEvidenceExecutionClassifier.BuildRequestIdentity(Requests()[2].EvidenceRequest!),
            decisions.Single(decision => decision.FindingStableId == "dashboard").RequestIdentity);
        Assert.Equal(skipped, receipt.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate).NotRunSelections);
        Assert.All(receipt.RequestDispositions!, decision =>
            Assert.Equal(decisions.Single(durableDecision => durableDecision.FindingStableId == decision.FindingStableId).Decision,
                decision.Decision));
    }

    private void SeedCoreReceipt(AgentOrchestratorKernel kernel, Goal goal, TaskSpec reviewer, bool omitted,
        string? basis = null, bool red = false, string? filter = null)
    {
        var selected = Expected.Where(selection => selection.TestProject == "Core.Tests").ToArray();
        var request = new FindingEvidenceRequest(filter is null ? selected : [new("Core.Tests", filter)]);
        var result = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
            _artifacts, string.Join("; ", selected.Select(selection => $"{selection.TestProject}:{selection.TestClass}")), Candidate,
            omitted ? ["DispatchExecutionTests"] : ["DispatchExecutionTests", "TaskProcessTests"]);
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(reviewer.LastVerification, out var round, out _));
        var fingerprint = ConductorDriver.BuildFindingRoundFingerprint(reviewer, round);
        var arms = result.Arms!.Select(ConductorDriver.CreateFindingEvidenceArmReceipt).ToArray();
        if (omitted) arms = arms.Select(arm => arm.Arm == FindingEvidenceArm.Candidate
            ? arm with { NotRunSelections = [new("Core.Tests", "TaskProcessTests")] } : arm).ToArray();
        if (red) arms = arms.Select(arm => arm.Arm == FindingEvidenceArm.Candidate
            ? arm with { Disposition = FindingEvidenceArmDisposition.Red, Passed = false } : arm).ToArray();
        basis ??= ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(IncidentSettings());
        var receipt = new FindingEvidenceReceipt("core-receipt", Candidate, request, true, !red, "retained core evidence",
            arms, [new("core", filter is null ? FindingEvidenceExecutionClassifier.BuildRequestIdentity(request)
                : $"Core.Tests:{filter}", "executed-standalone",
                "single-request", FindingEvidenceRequestDecision.Executed, Candidate, fingerprint,
                SourceReceiptIds: ["core-receipt"], ExecutionBasisIdentity: basis)], fingerprint, basis);
        kernel.RecordFindingEvidenceOutcome(goal.Id, reviewer.Id, "core",
            new(true, "core-receipt", ResultReason: red ? FindingEvidenceOutcomeReason.CandidateRed : FindingEvidenceOutcomeReason.ValidEvidence), receipt);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void IncompatibleOrRedReceipt_DoesNotHideUnexecutedUnion(bool red)
    {
        var (kernel, goal, reviewer) = Arrange(Requests());
        SeedCoreReceipt(kernel, goal, reviewer, false, basis: red ? null : "different-basis", red: red);
        var runs = new List<string>();
        Driver(kernel, (_, request) =>
        {
            runs.Add(request);
            return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(_artifacts, request, Candidate);
        }).AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        // A RED subset cannot be promoted to a passing union. Without a reusable complete
        // request receipt the union must still execute all its classes.
        Assert.Single(runs);
        Assert.Equal(8, runs[0].Split(';').Length);
    }

    [Xunit.Fact]
    public void MissingCandidate_RecordsUnavailableDecisionWithoutStartingEvidence()
    {
        var (kernel, goal, reviewer) = Arrange(Requests());
        var runs = 0;
        Driver(kernel, (_, request) => { runs++; return PassingPreReviewEvidence(request); }, candidate: "unavailable")
            .AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(0, runs);
        Assert.All(reviewer.VerificationHistory.Last().FindingEvidenceRequestDispositions!, decision =>
        {
            Assert.Equal(FindingEvidenceRequestDecision.CandidateUnavailable, decision.Decision);
            Assert.Null(decision.CandidateSha);
        });
        Assert.Equal(4, reviewer.VerificationHistory.Last().FindingEvidenceRequestDispositions!.Count);
    }

    [Xunit.Theory]
    [Xunit.InlineData("FullyQualifiedName~SomeTests.Method")]
    [Xunit.InlineData("FullyQualifiedName~SomeTests&Category!=Slow")]
    [Xunit.InlineData("FullyQualifiedName~SomeTests|FullyQualifiedName!~OtherTests")]
    public void OpaqueFilters_DoNotBecomeClassCoverage(string filter)
    {
        Assert.False(FindingEvidenceRoundPlanner.TryExpandClasses(filter, out _));
        var selection = new FindingEvidenceSelection("Core.Tests", filter);
        Assert.Equal(selection, Assert.Single(FindingEvidenceRoundPlanner.ExpandSelections([selection])));
    }

    [Xunit.Fact]
    public void OldDisposition_RoundTripsWithoutInventingCandidateOrExecution()
    {
        var legacy = JsonSerializer.Deserialize<FindingEvidenceRequestDisposition>(
            """{"FindingStableId":"old","RequestIdentity":"Core.Tests:TaskProcessTests","Disposition":"pending-standalone"}""")!;
        Assert.Null(legacy.Decision);
        Assert.Null(legacy.CandidateSha);
        Assert.Null(legacy.SourceReceiptIds);
        Assert.Equal(legacy, JsonSerializer.Deserialize<FindingEvidenceRequestDisposition>(JsonSerializer.Serialize(legacy)));
    }

    [Xunit.Fact]
    public void PendingCoverageWithoutReceipt_StillExecutesUnion()
    {
        var (kernel, goal, reviewer) = Arrange(Requests());
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(reviewer.LastVerification, out var round, out _));
        var fingerprint = ConductorDriver.BuildFindingRoundFingerprint(reviewer, round);
        kernel.RecordFindingEvidenceRequestDispositions(goal.Id, reviewer.Id,
            [new("core", FindingEvidenceExecutionClassifier.BuildRequestIdentity(new(Expected[..2])), "pending-batched", "covering-attempt-cancelled",
                FindingEvidenceRequestDecision.CoveredByPendingRequest, Candidate, fingerprint,
                CoveringRequestIdentity: "union",
                ExecutionBasisIdentity: ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(IncidentSettings()))]);
        var runs = new List<string>();
        Driver(kernel, (_, request) =>
        {
            runs.Add(request);
            return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(_artifacts, request, Candidate);
        }).AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(8, Assert.Single(runs).Split(';').Length);
    }

    [Xunit.Fact]
    public void NewCandidate_DoesNotReuseOldCoreReceipt()
    {
        const string next = "8f4c945ff5f5cbaa84170961a535d44ed6da153d";
        var (kernel, goal, reviewer) = Arrange(Requests());
        SeedCoreReceipt(kernel, goal, reviewer, false);
        var runs = new List<string>();
        Driver(kernel, (_, request) =>
        {
            runs.Add(request);
            return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(_artifacts, request, next);
        }, candidate: next).AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(8, Assert.Single(runs).Split(';').Length);
        var decisions = reviewer.VerificationHistory.Last().FindingEvidenceRequestDispositions!;
        Assert.Equal(4, decisions.Count(decision => decision.CandidateSha == next));
        Assert.Contains(decisions, decision => decision.CandidateSha == Candidate);
        Assert.All(decisions.Where(decision => decision.CandidateSha == next),
            decision => Assert.DoesNotContain("core-receipt", decision.SourceReceiptIds!));
    }

    [Xunit.Fact]
    public void StaleEnrichment_PreservesCompletedDecisionAndReceiptLinks()
    {
        var (kernel, goal, reviewer) = Arrange(Requests());
        SeedCoreReceipt(kernel, goal, reviewer, false);
        var completed = reviewer.LastVerification!;
        var original = Assert.Single(completed.FindingEvidenceRequestDispositions!);
        var stale = completed with
        {
            FindingEvidenceRequestDispositions = [original with
            {
                Decision = FindingEvidenceRequestDecision.PendingExecution, SourceReceiptIds = null
            }]
        };
        var merged = completed.MergeSameRoundEnrichment(stale);
        var decision = Assert.Single(merged.FindingEvidenceRequestDispositions!);
        Assert.Equal(FindingEvidenceRequestDecision.Executed, decision.Decision);
        Assert.Equal("core-receipt", Assert.Single(decision.SourceReceiptIds!));
        Assert.Equal(Candidate, decision.CandidateSha);
        Assert.Equal(original.FindingRoundFingerprint, decision.FindingRoundFingerprint);
    }

    [Xunit.Fact]
    public void EquivalentAlternatives_PreserveProjectAndDistinctClassIdentity()
    {
        var expanded = FindingEvidenceRoundPlanner.ExpandSelections(
        [
            new("Core.Tests", " (FullyQualifiedName~TaskProcessTests|FullyQualifiedName~DispatchExecutionTests) "),
            new("Core.Tests", "DispatchExecutionTests|TaskProcessTests|DispatchExecutionTests"),
            new("Infrastructure.Tests", "TaskProcessTests")
        ]);
        Assert.Equal(3, expanded.Count);
        Assert.Contains(new("Core.Tests", "DispatchExecutionTests"), expanded);
        Assert.Contains(new("Core.Tests", "TaskProcessTests"), expanded);
        Assert.Contains(new("Infrastructure.Tests", "TaskProcessTests"), expanded);
    }

    private static AgentOrchestratorKernel Reload(AgentOrchestratorKernel kernel) =>
        AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(kernel.ExportSnapshot()))!);

    // This fake executor supports only the incident's explicit class alternatives. Counts
    // observe the executed classes, so pre-change pipe requests fail the count assertion.
    private static string[] ExecuteFixtureSelections(string request) => request
        .Split(';', StringSplitOptions.TrimEntries)
        .SelectMany(item =>
        {
            var parts = item.Split(':', 2);
            return parts[1].Split('|', StringSplitOptions.TrimEntries)
                .Select(name => parts[0] + ":" + name.Replace("FullyQualifiedName~", "", StringComparison.Ordinal));
        }).ToArray();

    public void Dispose() => Directory.Delete(_artifacts, recursive: true);
}
