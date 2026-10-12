using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each operation has a unique journal root and an in-memory lease store.
public sealed class DeveloperDispatchIntegrationExecutorTests
{
    [Xunit.Fact]
    public void Execute_OperationSuccess_CompletesAfterReceipt()
    {
        using var fixture = new OperationFixture();
        var integration = Integration(allowed: true);

        var result = DeveloperDispatchIntegrationExecutor.Execute(
            fixture.Goal, fixture.Begin, UnexpectedLease,
            goal =>
            {
                Assert.Same(fixture.Goal, goal);
                fixture.Events.Add("integrate");
                return integration;
            },
            (goal, receipt) =>
            {
                Assert.Same(fixture.Goal, goal);
                Assert.Same(integration, receipt);
                fixture.AssertNoTerminal();
                fixture.Events.Add("recordReceipt");
            });

        Assert.Equal(DeveloperDispatchIntegrationRefusalKind.None, result.RefusalKind);
        Assert.Same(integration, result.Integration);
        Assert.Null(result.LeaseRecoveryStatus);
        Assert.Equal(new[] { "tryBeginOperation", "integrate", "recordReceipt",
            "Scope.Complete(integration current)" }, fixture.Events);
        fixture.AssertTerminal(GoalOperationStatus.Completed, integration.Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Live", "live")]
    [Xunit.InlineData("TerminalReclaimPending", "terminal-reclaim-pending")]
    [Xunit.InlineData("Reclaimed", "reclaimed")]
    [Xunit.InlineData("StateUnavailable", "state-unavailable")]
    [Xunit.InlineData(null, "state-unavailable")]
    public void Execute_NullOperationScope_ReportsRecoveryStatus(string? status, string expected)
    {
        var (_, goal) = ConductorDriverTests.SimpleGoal();
        var fact = status is null ? null : new GoalEvidenceLeaseFact(
            goal.Id.Value, "owner", "operation", "instance", null,
            Enum.Parse<GoalEvidenceLeaseRecoveryStatus>(status));

        var result = DeveloperDispatchIntegrationExecutor.Execute(
            goal, candidate =>
            {
                Assert.Same(goal, candidate);
                return new GoalEvidenceOperationStart(null, fact);
            }, UnexpectedLease, UnexpectedIntegration, UnexpectedReceipt);

        Assert.Equal(DeveloperDispatchIntegrationRefusalKind.OperationHeld, result.RefusalKind);
        Assert.Equal(expected, result.LeaseRecoveryStatus);
        Assert.Null(result.Integration);
    }

    [Xunit.Fact]
    public void Execute_NullLease_RefusesWithoutIntegration()
    {
        var (_, goal) = ConductorDriverTests.SimpleGoal();
        var acquisitions = 0;
        var result = DeveloperDispatchIntegrationExecutor.Execute(
            goal, null, (candidate, key) =>
            {
                Assert.Same(goal, candidate);
                Assert.Equal("conductor:developer-branch-integration", key);
                acquisitions++;
                return null;
            }, UnexpectedIntegration, UnexpectedReceipt);

        Assert.Equal(1, acquisitions);
        Assert.Equal(DeveloperDispatchIntegrationRefusalKind.LeaseRefused, result.RefusalKind);
        Assert.Null(result.LeaseRecoveryStatus);
        Assert.Null(result.Integration);
    }

    [Xunit.Fact]
    public void Execute_OperationRefusal_FailsAfterOneReceipt()
    {
        using var fixture = new OperationFixture();
        var integration = Integration(allowed: false);
        var receipts = 0;
        var result = DeveloperDispatchIntegrationExecutor.Execute(
            fixture.Goal, fixture.Begin, UnexpectedLease,
            _ => { fixture.Events.Add("integrate"); return integration; },
            (_, receipt) =>
            {
                Assert.Same(integration, receipt);
                fixture.AssertNoTerminal();
                receipts++;
                fixture.Events.Add("recordReceipt");
            });

        Assert.Equal(1, receipts);
        Assert.Equal(DeveloperDispatchIntegrationRefusalKind.IntegrationRefused, result.RefusalKind);
        Assert.Same(integration, result.Integration);
        Assert.Equal(new[] { "tryBeginOperation", "integrate", "recordReceipt",
            "Scope.Fail(integration rejected)" }, fixture.Events);
        fixture.AssertTerminal(GoalOperationStatus.Failed, integration.Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, true)]
    [Xunit.InlineData(false, true)]
    public void Execute_OperationException_TerminalizesAndRethrows(bool cancelled, bool receiptFault)
    {
        using var fixture = new OperationFixture();
        Exception fault = cancelled
            ? new OperationCanceledException("integration cancelled")
            : new InvalidOperationException("integration faulted");
        var receipts = 0;
        void Execute() => DeveloperDispatchIntegrationExecutor.Execute(
            fixture.Goal, fixture.Begin, UnexpectedLease,
            _ => receiptFault ? Integration(allowed: true) : throw fault,
            (_, _) => { receipts++; fixture.AssertNoTerminal(); throw fault; });

        var propagated = cancelled
            ? (Exception)Assert.Throws<OperationCanceledException>(Execute)
            : Assert.Throws<InvalidOperationException>(Execute);

        Assert.Same(fault, propagated);
        Assert.Equal(receiptFault ? 1 : 0, receipts);
        fixture.AssertTerminal(cancelled ? GoalOperationStatus.Aborted : GoalOperationStatus.Failed,
            fault.Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData("success")]
    [Xunit.InlineData("refused")]
    [Xunit.InlineData("cancelled")]
    [Xunit.InlineData("integration-fault")]
    [Xunit.InlineData("receipt-fault")]
    public void Execute_LegacyLease_DisposesOnEveryExit(string scenario)
    {
        var (_, goal) = ConductorDriverTests.SimpleGoal();
        var events = new List<string>();
        var lease = new RecordingLease(() => events.Add("dispose"));
        var integration = Integration(scenario != "refused");
        Exception? fault = scenario switch
        {
            "cancelled" => new OperationCanceledException("cancelled"),
            "integration-fault" or "receipt-fault" => new InvalidOperationException("faulted"),
            _ => null
        };
        DeveloperDispatchIntegrationExecution Execute() => DeveloperDispatchIntegrationExecutor.Execute(
            goal, null, (candidate, key) =>
            {
                Assert.Same(goal, candidate);
                Assert.Equal("conductor:developer-branch-integration", key);
                events.Add("tryAcquireLease");
                return lease;
            }, candidate =>
            {
                Assert.Same(goal, candidate);
                events.Add("integrate");
                if (fault is not null && scenario != "receipt-fault") throw fault;
                return integration;
            }, (candidate, receipt) =>
            {
                Assert.Same(goal, candidate);
                Assert.Same(integration, receipt);
                events.Add("recordReceipt");
                if (scenario == "receipt-fault") throw fault!;
            });

        if (fault is null)
        {
            var result = Execute();
            Assert.Equal(scenario == "refused"
                ? DeveloperDispatchIntegrationRefusalKind.IntegrationRefused
                : DeveloperDispatchIntegrationRefusalKind.None, result.RefusalKind);
            Assert.Same(integration, result.Integration);
            Assert.Null(result.LeaseRecoveryStatus);
        }
        else
        {
            var propagated = scenario == "cancelled"
                ? (Exception)Assert.Throws<OperationCanceledException>(() => Execute())
                : Assert.Throws<InvalidOperationException>(() => Execute());
            Assert.Same(fault, propagated);
        }

        Assert.Equal(1, lease.Disposals);
        Assert.Equal(fault is null || scenario == "receipt-fault"
            ? new[] { "tryAcquireLease", "integrate", "recordReceipt", "dispose" }
            : new[] { "tryAcquireLease", "integrate", "dispose" }, events);
    }

    [Xunit.Fact]
    public void Source_ExecutorBoundary_KeepsPolicyAndTerminalCallOwnership()
    {
        var directory = Path.Combine(VerifiedRepositoryRoot.Find(),
            "src", "Mcg.AgentOrchestrator.App", "Orchestration");
        var executor = File.ReadAllText(Path.Combine(directory, "DeveloperDispatchIntegrationExecutor.cs"));
        var driver = File.ReadAllText(Path.Combine(directory, "ConductorDriver.cs"));
        foreach (var forbidden in new[] { "DispatchStartPolicy", "DispatchStartFacts",
            "ConductorAdvanceOutcome", "ConductorDriver", "Console.", "partial class" })
            Assert.DoesNotContain(forbidden, executor, StringComparison.Ordinal);
        Assert.Contains("DeveloperDispatchIntegrationExecutor.Execute(", driver, StringComparison.Ordinal);
        Assert.DoesNotContain("operationStart.Scope.Complete(integration.Message)", driver,
            StringComparison.Ordinal);

        // The sealed scope ignores repeated terminal calls; source controls detect those too.
        Assert.Equal(1, Regex.Count(executor, @"\.Scope\.Complete\("));
        Assert.Equal(2, Regex.Count(executor, @"\.Scope\.Fail\("));
        Assert.Equal(1, Regex.Count(executor, @"\.Scope\.Abort\("));
        var success = executor.Split("if (integration.CanDispatch)", StringSplitOptions.None)[1]
            .Split("return new(DeveloperDispatchIntegrationRefusalKind.None", StringSplitOptions.None)[0];
        Assert.Contains("operationStart.Scope.Complete(integration.Message);", success, StringComparison.Ordinal);
        Assert.DoesNotContain(".Fail(", success, StringComparison.Ordinal);
        Assert.DoesNotContain(".Abort(", success, StringComparison.Ordinal);
        var cancellation = executor.Split("catch (OperationCanceledException ex)", StringSplitOptions.None)[1]
            .Split("catch (Exception ex)", StringSplitOptions.None)[0];
        Assert.Contains("operationStart.Scope.Abort(ex.Message);", cancellation, StringComparison.Ordinal);
        Assert.Contains("throw;", cancellation, StringComparison.Ordinal);
        Assert.DoesNotContain(".Fail(", cancellation, StringComparison.Ordinal);
        Assert.DoesNotContain(".Complete(", cancellation, StringComparison.Ordinal);
    }

    private static DeveloperBranchIntegrationResult Integration(bool allowed) => new(
        allowed ? DeveloperBranchIntegrationStatus.Current : DeveloperBranchIntegrationStatus.Conflict,
        allowed ? "integration current" : "integration rejected", allowed ? [] : ["src/conflict.cs"]);

    private static IDisposable? UnexpectedLease(Goal _, string __) =>
        throw new InvalidOperationException("The operation branch must not acquire a legacy lease.");
    private static DeveloperBranchIntegrationResult UnexpectedIntegration(Goal _) =>
        throw new InvalidOperationException("Refused acquisition must not integrate.");
    private static void UnexpectedReceipt(Goal _, DeveloperBranchIntegrationResult __) =>
        throw new InvalidOperationException("Refused acquisition must not record a receipt.");

    private sealed class RecordingLease(Action dispose) : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() { Disposals++; dispose(); }
    }

    private sealed class OperationFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "developer-integration-executor", Guid.NewGuid().ToString("N"));
        private readonly ReleaseRecordingStore _store;
        public Goal Goal { get; } = ConductorDriverTests.SimpleGoal().Goal;
        public List<string> Events { get; } = [];

        public OperationFixture()
        {
            _store = new ReleaseRecordingStore(() =>
            {
                var entry = Assert.Single(Terminals());
                var method = entry.Status switch
                {
                    GoalOperationStatus.Completed => "Complete",
                    GoalOperationStatus.Failed => "Fail",
                    GoalOperationStatus.Aborted => "Abort",
                    _ => throw new InvalidOperationException("Expected a terminal status before release.")
                };
                Events.Add($"Scope.{method}({entry.Detail})");
            });
        }

        public GoalEvidenceOperationStart Begin(Goal goal)
        {
            Assert.Same(Goal, goal);
            Events.Add("tryBeginOperation");
            var coordinator = new GoalEvidenceOperationCoordinator(_store, _root,
                TimeSpan.FromMinutes(30), utcNow: () => DateTimeOffset.UnixEpoch,
                processId: () => 42, instanceId: () => "test-instance",
                processStartedAtUtc: () => DateTimeOffset.UnixEpoch);
            return coordinator.TryBegin(goal, "conductor:developer-branch-integration");
        }

        private GoalOperationJournalEntry[] Terminals() => GoalOperationJournal.Read(_root, Goal.Id).Entries
            .Where(entry => entry.Status != GoalOperationStatus.Begin).ToArray();
        public void AssertNoTerminal() => Assert.Empty(Terminals());
        public void AssertTerminal(GoalOperationStatus expected, string message)
        {
            var entry = Assert.Single(Terminals());
            Assert.Equal(expected, entry.Status);
            Assert.Equal(message, entry.Detail);
            Assert.Equal("test-instance", entry.OperationInstanceId);
            Assert.Equal(1, _store.Releases);
            Assert.Null(_store.Owner);
        }
        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ReleaseRecordingStore(Action beforeRelease) : IReconcileSweepRemediationStore
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
