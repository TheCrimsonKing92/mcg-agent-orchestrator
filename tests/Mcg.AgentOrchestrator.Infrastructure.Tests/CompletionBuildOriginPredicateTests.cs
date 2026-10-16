using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repository, SQLite and journal roots; injected child lifecycle and clock.
public sealed class CompletionBuildOriginPredicateTests
{
    public static TheoryData<string> PredicateCases => new()
    {
        "OperationIdFormat", "PhaseDefined", "PendingCustodyConsistent", "SuccessExitCodeZero",
        "GoalIdMatches", "TaskIdMatches", "DispatchPresent", "DispatchIdMatches", "WorkerNotRunning",
        "WorkerCommandMatches", "WorkerWorkingDirectoryMatches", "DispatchWorkingDirectoryMatches",
        "StatePathMatches", "ExecutionDirectoryMatches", "ProjectsPresent", "ProjectsNormalized",
        "TreeDigestPresent", "ResultPathMatches"
    };

    [Theory]
    [MemberData(nameof(PredicateCases))]
    public void FirstFailure_SingleBrokenPredicate_ReportsBoundedValues(string predicate)
    {
        var f = Start();
        var operation = Mutate(f, predicate);

        var failure = Assert.IsType<CompletionBuildOriginPredicates.Failure>(FirstFailure(f, operation));

        Assert.Equal(predicate, failure.PredicateName);
        Assert.False(string.IsNullOrWhiteSpace(failure.Observed));
        Assert.False(string.IsNullOrWhiteSpace(failure.Expected));
        Assert.InRange(failure.Observed.Length, 1, 200);
        Assert.InRange(failure.Expected.Length, 1, 200);
    }

    [Fact]
    public void Ordered_BriefPredicates_MatchWithoutDuplicates()
    {
        string[] expected =
        [
            "OperationIdFormat", "PhaseDefined", "PendingCustodyConsistent", "SuccessExitCodeZero",
            "GoalIdMatches", "TaskIdMatches", "DispatchPresent", "DispatchIdMatches", "WorkerNotRunning",
            "WorkerCommandMatches", "WorkerWorkingDirectoryMatches", "DispatchWorkingDirectoryMatches",
            "StatePathMatches", "ExecutionDirectoryMatches", "ProjectsPresent", "ProjectsNormalized",
            "TreeDigestPresent", "ResultPathMatches"
        ];
        var names = CompletionBuildOriginPredicates.Ordered.Select(p => p.Name).ToArray();

        Assert.Equal(expected, names);
        Assert.Equal(18, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void FirstFailure_ValidAttachedOperation_PassesAndReconciles()
    {
        var f = Start();
        var operation = f.Task.CompletionBuildEvidence!;

        Assert.Null(FirstFailure(f, operation));
        var reconciled = f.Coordinator().ReconcileAttached(f.Kernel, f.GoalId, f.TaskId,
            CompletionBuildEvidenceTarget.From(operation));

        Assert.Equal(operation, reconciled);
        Assert.Equal(0, f.Stops);
    }

    [Theory]
    [InlineData("GoalIdMatches", "ResultPathMatches")]
    [InlineData("ProjectsNormalized", "TreeDigestPresent")]
    public void FirstFailure_TwoBrokenPredicates_ReportsEarlier(string first, string second)
    {
        var f = Start();
        var operation = Mutate(f, first);
        operation = second == "ResultPathMatches"
            ? operation with { ResultPath = Path.Combine(f.Root, "foreign-result.json") }
            : operation with { TreeDigest = " " };

        Assert.Equal(first, FirstFailure(f, operation)!.PredicateName);
    }

    [Theory]
    [MemberData(nameof(PredicateCases))]
    public void ReconcileAttached_BrokenOrigin_FailsBeforeChildOrLeaseAction(string predicate)
    {
        var f = Start();
        var lease = Lease(f);
        Assert.False(string.IsNullOrWhiteSpace(lease));
        var operation = Mutate(f, predicate);
        Inject(f, operation);

        var error = Assert.Throws<CompletionBuildOriginRejectedException>(() =>
            f.Coordinator().ReconcileAttached(f.Kernel, f.GoalId, f.TaskId,
                CompletionBuildEvidenceTarget.From(operation)));

        Assert.Equal(predicate, error.PredicateName);
        Assert.Contains(predicate, error.Message);
        Assert.Contains(error.Observed, error.Message);
        Assert.Contains(error.Expected, error.Message);
        Assert.InRange(error.Observed.Length, 1, 200);
        Assert.InRange(error.Expected.Length, 1, 200);
        Assert.Equal(0, f.Stops);
        Assert.Equal(lease, Lease(f));
    }

    [Fact]
    public void RefreshLatestProcess_ForeignResultPath_PreservesDiagnosticSurface()
    {
        var f = Start();
        Inject(f, Mutate(f, "ResultPathMatches"));

        var error = Assert.Throws<CompletionBuildOriginRejectedException>(() =>
            f.Runner().RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId));

        Assert.Equal("ResultPathMatches", error.PredicateName);
        var formatted = ProgramStartupErrorFormatter.Format(error);
        Assert.Contains("ResultPathMatches", formatted);
        Assert.StartsWith(nameof(CompletionBuildOriginRejectedException) + ": ", formatted);
    }

    [Fact]
    public void ReconcileAttached_ForeignChildLease_KeepsPlainException()
    {
        var f = Start();
        var operation = f.Task.CompletionBuildEvidence! with { LeaseOwner = "foreign" };
        Assert.NotNull(operation.ChildPid);
        Assert.Null(FirstFailure(f, operation));
        Inject(f, operation);

        var error = Assert.Throws<InvalidOperationException>(() =>
            f.Coordinator().ReconcileAttached(f.Kernel, f.GoalId, f.TaskId,
                CompletionBuildEvidenceTarget.From(operation)));

        Assert.Equal("Completion build operation child/lease identity is unproven.", error.Message);
    }

    [Theory]
    [InlineData(199)]
    [InlineData(200)]
    [InlineData(201)]
    public void Exception_LongValues_BoundsPropertiesAndMessage(int length)
    {
        var value = new string('a', length);
        var expected = length <= 200 ? value : new string('a', 197) + "...";
        var error = new CompletionBuildOriginRejectedException("ResultPathMatches", value, value);

        Assert.Equal(expected, error.Observed);
        Assert.Equal(expected, error.Expected);
        Assert.Equal($"Completion build operation origin identity is unproven: predicate ResultPathMatches failed; observed {expected}; expected {expected}.",
            error.Message);
        Assert.Empty(error.Data);
    }

    [Fact]
    public void FirstFailure_LongOperationId_BoundsPredicateValues()
    {
        var f = Start();
        var operation = f.Task.CompletionBuildEvidence! with { Id = new string('a', 201) };

        var failure = FirstFailure(f, operation)!;

        Assert.Equal("OperationIdFormat", failure.PredicateName);
        Assert.Equal(new string('a', 197) + "...", failure.Observed);
    }

    private static CompletionBuildOriginPredicates.Failure? FirstFailure(
        CompletionBuildEvidenceFixture f, CompletionBuildEvidenceOperation operation) =>
        CompletionBuildOriginPredicates.FirstFailure(f.Task, f.GoalId, operation, f.StatePath, f.Root);

    private static CompletionBuildEvidenceFixture Start()
    {
        var f = new CompletionBuildEvidenceFixture();
        f.Runner().RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.Pending, f.Task.CompletionBuildEvidence!.Phase);
        Assert.Null(FirstFailure(f, f.Task.CompletionBuildEvidence));
        return f;
    }

    private static CompletionBuildEvidenceOperation Mutate(CompletionBuildEvidenceFixture f, string predicate)
    {
        var operation = f.Task.CompletionBuildEvidence!;
        var foreignWorktree = Path.Combine(f.Root, "foreign-worktree");
        return predicate switch
        {
            "OperationIdFormat" => operation with { Id = "not-a-guid" },
            "PhaseDefined" => operation with { Phase = (CompletionBuildEvidencePhase)99 },
            "PendingCustodyConsistent" => operation with { CustodyReleased = true },
            "SuccessExitCodeZero" => operation with { Phase = CompletionBuildEvidencePhase.Success },
            "GoalIdMatches" => operation with { GoalId = GoalId.New().Value },
            "TaskIdMatches" => operation with { TaskId = TaskId.New().Value },
            "DispatchPresent" => operation with { DispatchAt = operation.DispatchAt.AddMinutes(1) },
            "DispatchIdMatches" => operation with { DispatchId = "foreign" },
            "WorkerNotRunning" => operation with
                { WorkerProcess = operation.WorkerProcess with { CompletedAt = null, ExitCode = null } },
            "WorkerCommandMatches" => operation with
                { WorkerProcess = operation.WorkerProcess with { Command = "foreign" } },
            "WorkerWorkingDirectoryMatches" => operation with
                { WorkerProcess = operation.WorkerProcess with { WorkingDirectory = foreignWorktree } },
            "DispatchWorkingDirectoryMatches" => operation with { WorktreeRoot = foreignWorktree,
                WorkerProcess = operation.WorkerProcess with { WorkingDirectory = foreignWorktree } },
            "StatePathMatches" => operation with { StatePath = Path.Combine(f.Root, "foreign.db") },
            "ExecutionDirectoryMatches" => operation with { ExecutionDirectory = Path.Combine(f.Root, "foreign-exec") },
            "ProjectsPresent" => operation with { Projects = [] },
            "ProjectsNormalized" => operation with { Projects = [.. operation.Projects, .. operation.Projects] },
            "TreeDigestPresent" => operation with { TreeDigest = " " },
            "ResultPathMatches" => operation with { ResultPath = Path.Combine(f.Root, "foreign-result.json") },
            _ => throw new ArgumentOutOfRangeException(nameof(predicate), predicate, "Unknown predicate row.")
        };
    }

    private static void Inject(CompletionBuildEvidenceFixture f, CompletionBuildEvidenceOperation operation)
    {
        var snapshot = f.Kernel.ExportSnapshot();
        var goal = snapshot.Goals.Single();
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [goal with
        { Tasks = goal.Tasks.Select(t => t.Id == f.TaskId.Value ? t with { CompletionBuildEvidence = operation } : t).ToArray() }] }, f.Clock);
    }

    private static string? Lease(CompletionBuildEvidenceFixture f) =>
        new ReconcileSweepRemediationStore(f.StatePath).TryGetAcceptanceLeaseOwner(f.GoalId.Value);
}
