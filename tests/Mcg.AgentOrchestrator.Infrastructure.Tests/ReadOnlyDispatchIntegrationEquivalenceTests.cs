using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each run owns a unique journal root and an in-memory lease store.
public sealed class ReadOnlyDispatchIntegrationEquivalenceTests
{
    [Fact]
    public void Source_ReadOnlyIntegration_DelegatesScopeDecisionsToExecutor()
    {
        var source = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(),
            "src", "Mcg.AgentOrchestrator.App", "Orchestration", "ConductorDriver.PreDispatchIntegration.cs"));
        var body = source.Split("private bool TryIntegrateMainBeforeReadOnlyDispatch(",
            StringSplitOptions.None)[1].Split("private ConductorAdvanceResult DispatchStartResult(",
            StringSplitOptions.None)[0];
        Assert.Contains("DeveloperDispatchIntegrationExecutor.Execute(", body, StringComparison.Ordinal);
        foreach (var terminalCall in new[] { "Scope.Complete(", "Scope.Fail(", "Scope.Abort(" })
            Assert.DoesNotContain(terminalCall, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("held", "Live", "live")]
    [InlineData("held", "Reclaimed", "reclaimed")]
    [InlineData("held", null, "state-unavailable")]
    [InlineData("lease", null, null)]
    [InlineData("refused", null, null)]
    [InlineData("allowed", null, null)]
    public void AdvanceOnce_SameInputs_PreserveEventsAndReadOnlyOutcomes(
        string scenario, string? recovery, string? expectedRecovery)
    {
        using var planner = new DriverFixture(AgentRole.Planner, scenario, recovery);
        using var developer = new DriverFixture(AgentRole.Developer, scenario, recovery);

        var plannerResult = planner.Advance();
        developer.Advance();

        var expectedEvents = scenario switch
        {
            "held" => new[] { "begin" },
            "lease" => new[] { "acquire" },
            "refused" => new[] { "begin", "integrate", "receipt", "scope.fail" },
            "allowed" => new[] { "begin", "integrate", "receipt", "scope.complete" },
            _ => throw new InvalidOperationException("Unknown test scenario.")
        };
        GoalOperationStatus? expectedStatus = scenario switch
        {
            "refused" => GoalOperationStatus.Failed,
            "allowed" => GoalOperationStatus.Completed,
            _ => null
        };
        Assert.Equal(expectedEvents, planner.Events);
        Assert.Equal(planner.Events, developer.Events);
        Assert.Equal(scenario is "refused" or "allowed" ? 1 : 0, planner.Receipts);
        Assert.Equal(planner.Receipts, developer.Receipts);
        planner.AssertTerminal(expectedStatus, expectedStatus is null ? null : planner.Integration.Message);
        developer.AssertTerminal(expectedStatus, expectedStatus is null ? null : developer.Integration.Message);
        Assert.Equal(planner.TerminalStatus, developer.TerminalStatus);
        Assert.Equal(scenario == "allowed" ? 1 : 0, planner.Dispatches);
        Assert.Equal(planner.Dispatches, developer.Dispatches);

        if (scenario is "held" or "lease")
        {
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(plannerResult.Outcome);
            Assert.Equal("read-only-integration-hold", held.Decision!.DiscriminatingEvidence);
            Assert.Equal(scenario == "held"
                ? $"Goal evidence mutation is held for {planner.Goal.Id.Value}; lease-recovery={expectedRecovery}."
                : $"Goal evidence mutation is blocked by concurrent acceptance or replacement for {planner.Goal.Id.Value}.",
                held.Reason);
            Assert.Null(planner.Escalation);
        }
        else if (scenario == "refused")
        {
            var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(plannerResult.Outcome);
            Assert.Equal(planner.Integration.Message, escalated.Reason);
            Assert.Equal(planner.Integration.Message, planner.Escalation);
            Assert.Equal(developer.Integration.Message, developer.Escalation);
            Assert.DoesNotContain("scope.complete", planner.Events);
            Assert.DoesNotContain("scope.complete", developer.Events);
        }
        else
        {
            Assert.IsType<ConductorAdvanceOutcome.Executed>(plannerResult.Outcome);
            Assert.Null(planner.Escalation);
            Assert.Null(developer.Escalation);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AdvanceOnce_IntegrationThrows_TerminalizesBothRolesAndRethrows(bool cancelled)
    {
        Exception fault = cancelled
            ? new OperationCanceledException("integration cancelled")
            : new InvalidOperationException("integration faulted");
        using var planner = new DriverFixture(AgentRole.Planner, "allowed", fault: fault);
        using var developer = new DriverFixture(AgentRole.Developer, "allowed", fault: fault);

        foreach (var fixture in new[] { planner, developer })
        {
            var thrown = cancelled
                ? (Exception)Assert.Throws<OperationCanceledException>(() => fixture.Advance())
                : Assert.Throws<InvalidOperationException>(() => fixture.Advance());
            Assert.Same(fault, thrown);
            Assert.Equal(new[] { "begin", "integrate", cancelled ? "scope.abort" : "scope.fail" },
                fixture.Events);
            Assert.Equal(0, fixture.Receipts);
            Assert.Equal(0, fixture.Dispatches);
            Assert.Null(fixture.Escalation);
            fixture.AssertTerminal(cancelled ? GoalOperationStatus.Aborted : GoalOperationStatus.Failed,
                fault.Message);
        }
        Assert.Equal(planner.Events, developer.Events);
        Assert.Equal(planner.TerminalStatus, developer.TerminalStatus);
    }

    private sealed class DriverFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "read-only-dispatch-equivalence", Guid.NewGuid().ToString("N"));
        private readonly RecordingStore _store;
        private readonly ConductorDriver _driver;
        public Goal Goal { get; }
        public List<string> Events { get; } = [];
        public int Receipts { get; private set; }
        public int Dispatches { get; private set; }
        public string? Escalation { get; private set; }
        public GoalOperationStatus? TerminalStatus { get; private set; }
        public DeveloperBranchIntegrationResult Integration { get; }

        public DriverFixture(AgentRole role, string scenario, string? recovery = null, Exception? fault = null)
        {
            var kernel = new AgentOrchestratorKernel();
            Goal = kernel.CreateGoal("Compare pre-dispatch integration",
                [new TaskSpec(TaskId.New(), "Assigned work", role)]);
            kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            var task = Assert.Single(Goal.Tasks);
            Assert.Equal(role, task.RequiredRole);
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Integration = new DeveloperBranchIntegrationResult(
                scenario == "refused" ? DeveloperBranchIntegrationStatus.Conflict : DeveloperBranchIntegrationStatus.Current,
                scenario == "refused" ? "integration rejected" : "integration current",
                scenario == "refused" ? ["src/conflict.cs"] : []);
            _store = new RecordingStore(() =>
            {
                var terminal = Assert.Single(Terminals());
                TerminalStatus = terminal.Status;
                Events.Add(terminal.Status switch
                {
                    GoalOperationStatus.Completed => "scope.complete",
                    GoalOperationStatus.Failed => "scope.fail",
                    GoalOperationStatus.Aborted => "scope.abort",
                    _ => throw new InvalidOperationException("Expected terminal status before lease release.")
                });
            });
            _driver = new ConductorDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningPaidWorkerCount: () => 0,
                createWorkspace: _ => throw new InvalidOperationException("Unexpected workspace creation."),
                dispatchAndStart: candidate =>
                {
                    Assert.Same(Goal, candidate);
                    Assert.Equal("allowed", scenario);
                    Assert.Null(fault);
                    Assert.Equal(GoalOperationStatus.Completed, Assert.Single(Terminals()).Status);
                    Dispatches++;
                    return DispatchStartOutcome.Started();
                },
                startRecordedDispatches: null,
                buildServerShutdown: null,
                runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                runAdvisorySemanticAcceptance: null,
                retryTask: null,
                recordTaskNote: null,
                recordCriterionRetryFeedback: null,
                clearCriterionRetryFeedback: null,
                rebaseOntoMain: _ => throw new InvalidOperationException("Unexpected landing rebase."),
                land: (_, _) => throw new InvalidOperationException("Unexpected landing."),
                afterSuccessfulLanding: null,
                record: _ => { },
                cleanup: _ => throw new InvalidOperationException("Unexpected cleanup."),
                writeEscalation: (candidate, _, reason) =>
                {
                    Assert.Same(Goal, candidate);
                    Assert.Equal("refused", scenario);
                    Assert.Equal(Integration.Message, reason);
                    Assert.Equal(GoalOperationStatus.Failed, Assert.Single(Terminals()).Status);
                    Assert.NotNull(_store.Owner);
                    Assert.Null(Escalation);
                    Escalation = reason;
                },
                classifyChangeRisk: _ => null,
                tryAcquireEvidenceMutationLease: (candidate, key) =>
                {
                    Assert.Same(Goal, candidate);
                    Assert.Equal("lease", scenario);
                    Assert.Equal("conductor:developer-branch-integration", key);
                    Events.Add("acquire");
                    return null;
                },
                integrateMainBeforeDeveloperDispatch: candidate =>
                {
                    Assert.Equal(AgentRole.Developer, role);
                    return Integrate(candidate);
                },
                recordPreDispatchIntegrationReceipt: (candidate, receipt) =>
                {
                    Assert.Same(Goal, candidate);
                    Assert.Same(Integration, receipt);
                    Assert.Empty(Terminals());
                    Assert.NotNull(_store.Owner);
                    Events.Add("receipt");
                    Receipts++;
                },
                tryBeginDeveloperIntegrationEvidenceOperation: scenario == "lease" ? null : candidate =>
                {
                    Assert.Same(Goal, candidate);
                    Events.Add("begin");
                    if (scenario == "held")
                    {
                        var fact = recovery is null ? null : new GoalEvidenceLeaseFact(
                            Goal.Id.Value, "owner", "operation", "instance", null,
                            Enum.Parse<GoalEvidenceLeaseRecoveryStatus>(recovery));
                        return new GoalEvidenceOperationStart(null, fact);
                    }
                    var coordinator = new GoalEvidenceOperationCoordinator(_store, _root,
                        TimeSpan.FromMinutes(30), utcNow: () => DateTimeOffset.UnixEpoch,
                        processId: () => 42, instanceId: () => "test-instance",
                        processStartedAtUtc: () => DateTimeOffset.UnixEpoch);
                    var start = coordinator.TryBegin(candidate, "conductor:developer-branch-integration");
                    Assert.NotNull(start.Scope);
                    return start;
                },
                integrateMainBeforeReadOnlyDispatch: (candidate, dispatchRole) =>
                {
                    Assert.Equal(AgentRole.Planner, role);
                    Assert.Equal(role, dispatchRole);
                    return Integrate(candidate);
                });

            DeveloperBranchIntegrationResult Integrate(Goal candidate)
            {
                Assert.Same(Goal, candidate);
                Assert.True(scenario is "allowed" or "refused");
                Assert.Empty(Terminals());
                Assert.NotNull(_store.Owner);
                Events.Add("integrate");
                if (fault is not null) throw fault;
                return Integration;
            }
        }

        public ConductorAdvanceResult Advance() => _driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);

        private GoalOperationJournalEntry[] Terminals() => GoalOperationJournal.Read(_root, Goal.Id).Entries
            .Where(entry => entry.Status != GoalOperationStatus.Begin).ToArray();

        public void AssertTerminal(GoalOperationStatus? expected, string? message)
        {
            Assert.Equal(expected, TerminalStatus);
            if (expected is null)
            {
                Assert.Empty(Terminals());
                Assert.Equal(0, _store.Releases);
            }
            else
            {
                var terminal = Assert.Single(Terminals());
                Assert.Equal(expected, terminal.Status);
                Assert.Equal(message, terminal.Detail);
                Assert.Equal("test-instance", terminal.OperationInstanceId);
                Assert.Equal(1, _store.Releases);
            }
            Assert.Null(_store.Owner);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingStore(Action beforeRelease) : IReconcileSweepRemediationStore
    {
        public string? Owner { get; private set; }
        public int Releases { get; private set; }
        public bool TryClaimAcceptanceLease(string goalId, string owner, TimeSpan staleAfter)
        {
            Assert.Null(Owner);
            Owner = owner;
            return true;
        }
        public void ReleaseAcceptanceLease(string goalId, string owner)
        {
            Assert.Equal(Owner, owner);
            beforeRelease();
            Releases++;
            Owner = null;
        }
        public ReconcileAcceptanceLeaseState? TryGetAcceptanceLease(string goalId, TimeSpan staleAfter) =>
            Owner is null ? null : new(Owner, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch + staleAfter);
        public string? TryGetAcceptanceLeaseOwner(string goalId) => Owner;
        public ReconcileSweepRemediationState Observe(string key, string goal, string kind, string evidence, string remedy) =>
            throw new NotSupportedException();
        public bool TryMarkBlockerEmitted(string key) => throw new NotSupportedException();
        public ReconcileSweepAttemptClaim TryClaimAttempt(string key, int attempts, string owner) =>
            throw new NotSupportedException();
        public void CompleteAttempt(string key, string owner, int exit, string output, bool consumeAttempt = true) =>
            throw new NotSupportedException();
        public bool TryMarkEscalationEmitted(string key) => throw new NotSupportedException();
        public bool TryReplaceAcceptanceLease(string goal, string expected, string successor) =>
            throw new NotSupportedException();
        public IDisposable? TryAcquireAcceptanceLease(string goal, string owner, TimeSpan staleAfter) =>
            throw new NotSupportedException();
    }
}
