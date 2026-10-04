using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Verbatim Reviewer data from operator-evidence/d0ffaaaf/loop-fixture.md.
internal sealed class ConductorFindingEvidenceLoopFixture
{
    internal const string Candidate = "465dab7accf7a4e448d22e12c66d897b721fa304";
    internal const string FindingId = "timeline-route-load-miss-error-parity";
    internal const string CliProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj";
    internal static readonly DateTimeOffset Now = new(2026, 10, 4, 4, 26, 50, TimeSpan.Zero);
    private const string ReviewerOutput = """
WORKER_RESULT:
blockers: src/Mcg.AgentOrchestrator.App/Cli/CliTimelineQueryCommand.cs:48 severity blocking - when the prefix matches a metadata row but LoadGoalsAsync omits the goal (quarantined snapshot or concurrent delete), Single() throws InvalidOperationException 'Sequence contains no matching element' where main's writer path (CliPersistentStateRunner.cs:4703-4706) throws KeyNotFoundException 'Goal <id> was not found.' - violates the Refined Spec behavioural contract (same exception type and message) and the Decision to reproduce the writer path's exception type and message, which underlies criterion 3 parity
findings: [{"stable_id":"timeline-route-load-miss-error-parity","state":"open","severity":"blocking","category":"correctness","location":{"file":"src/Mcg.AgentOrchestrator.App/Cli/CliTimelineQueryCommand.cs","region":"Execute","hunk":"currentGoal = kernel.Goals.Single(goal => goal.Id == goalId)"},"description":"After a prefix matches a ListGoalMetadataAsync row, Execute calls kernel.Goals.Single(...). LoadGoalsAsync skips quarantined snapshots (SqliteOrchestratorStateRepository.cs:1830-1838) and goals deleted between the two separate reads, while the metadata listing still includes them. Main's writer path (CliPersistentStateRunner.cs:4697-4706 LoadSingleGoalKernel) throws KeyNotFoundException('Goal <full id> was not found.'). The new route throws InvalidOperationException('Sequence contains no matching element'). That breaks the spec's same-type-and-message contract. The task-query exemplar pins this case (CliCommandTestsTaskQueries.cs:402-414). Fix: SingleOrDefault plus the writer path's KeyNotFoundException with the full goal id, and a parity fact in CliTimelineRouteParityTests using ProbeStateRepository.UnavailableGoalIds for each explicit form against skipReadOnlyRoute: true.","evidence_request":{"selections":[{"test_project":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj","test_class":"CliTimelineRouteParityTests"}]}},{"stable_id":"timeline-required-negative-control-evidence","state":"resolved","severity":"blocking","category":"test-evidence","location":{"file":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/CliTimelineReadOnlyWriterHeldTests.cs","region":"ExplicitForms_HeldWriterPreservesOutputAndEveryStateRow"},"description":"Closed by receipt finding-evidence-a1a5986bc0c47b9244e87f9a at candidate 465dab7: candidate arm 17/17 passed, SourceReverted arm 6 failures including the held-writer outbox-drain stderr and classification rejections, TRX hashes matched."},{"stable_id":"task-timeline-startup-execution-capability","state":"open","severity":"advisory","category":"spec-defect","location":{"file":"src/Mcg.AgentOrchestrator.App/Cli/CliCommandCapabilities.cs","region":"QueryCommands"},"description":"task-timeline is not in QueryCommands, so explicit task-timeline forms still run Program.cs:430-436 InitializeWorkerProcessTracking/SweepStartupOrphans (can write the spawn registry when orphans exist) and Program.cs:307-310 default provider creation and the llama.cpp reachability check. Fixing it requires Program.cs or CliCommandCapabilities.cs, which the operator clarification places out of scope. Recommend a follow-up goal."}]
verdict: needs-work
touched_anchors: []
END_WORKER_RESULT
""";

    internal AgentOrchestratorKernel Kernel { get; } = new(new FixedClock(Now));
    internal Goal Goal { get; }
    internal TaskId DeveloperId { get; }
    internal TaskId ReviewerId { get; }
    internal int FocusedRuns { get; private set; }
    internal string CurrentCandidate { get; set; } = Candidate;
    internal List<(TaskId TaskId, RetryCause Cause, string Message)> Retries { get; } = [];
    internal List<GoalLifecycleState> DispatchStates { get; } = [];

    internal ConductorFindingEvidenceLoopFixture()
    {
        Goal = GoalLifecycleCommands.CreateAndActivateGoal(Kernel, AgentCatalog.Default().Agents,
            "Persist finding evidence through a held tick");
        DeveloperId = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer).Id;
        var reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        ReviewerId = reviewer.Id;
        foreach (var task in Goal.Tasks.Where(task => task.Id != ReviewerId))
        {
            RecordVerification(task, 0, "ok");
        }
        RecordVerification(reviewer, 1, ReviewerOutput);
        Assert.All(Goal.Tasks.Where(task => task.Id != ReviewerId),
            task => Assert.Equal(WorkTaskStatus.Completed, task.Status));
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(
            reviewer.LastVerification, out var round, out var diagnostic), diagnostic);
        Assert.Equal(3, round.Findings.Count);
    }

    private void RecordVerification(TaskSpec task, int exitCode, string output)
    {
        Kernel.RecordTaskDispatch(Goal.Id, task.Id,
            new TaskDispatchRecord("test-worker", "fixture", "C:\\tmp", Now, BaseCommit: Candidate));
        var verification = new TaskVerificationRecord("fixture", "C:\\tmp", exitCode, output, "", Now,
            WorkerResultPresent: task.RequiredRole == AgentRole.Reviewer, ReviewedCommit: Candidate);
        if (exitCode == 0) Kernel.RecordTaskVerification(Goal.Id, task.Id, verification);
        else Kernel.RecordDispatchExecutionResult(Goal.Id, task.Id, verification);
    }

    internal ConductorDriver CreateDriver(bool recordReceipt = true) => ConductorDriverTests.MakeDriver(
        getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
        getPreReviewEvidenceContext: _ => ConductorDriverTests.NoPreReviewContext(CurrentCandidate),
        getFindingEvidenceEngineSettings: _ => new AcceptanceGateEngineSettings
        {
            MtpInvocations = [new AcceptanceMtpInvocation { Project = CliProject }]
        },
        dispatchAndStart: goal =>
        {
            DispatchStates.Add(GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(WorkspaceExists: true)));
            return HeldDispatch();
        },
        runFocusedEvidence: (_, request) =>
        {
            FocusedRuns++;
            Assert.Contains("CliTimelineRouteParityTests", request);
            return new FocusedEvidenceRunResult(request, true, true, "candidate focused evidence passed",
                [new AcceptanceCheckResult("fixture candidate", true, 0, null)]);
        },
        retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
        {
            Retries.Add((taskId, cause, message));
            return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
        },
        recordFindingEvidenceRequest: (goalId, taskId, message) =>
            Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
        recordFindingEvidenceRun: (goalId, taskId, message) =>
            Kernel.RecordFindingEvidenceRun(goalId, taskId, message),
        recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
        {
            if (recordReceipt) Kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt);
        });

    internal static DispatchStartOutcome HeldDispatch() =>
        DispatchStartOutcome.Deferred("fixture background attempt owns dispatch") with
        { HoldOwner = ConductorHoldOwner.BackgroundAttempt };

    // Serialize as storage does, so the retained projection cannot share mutable kernel objects.
    internal OrchestratorSnapshot StoredProjection() => JsonSerializer.Deserialize<OrchestratorSnapshot>(
        JsonSerializer.Serialize(Kernel.ExportSnapshot()))!;

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}
