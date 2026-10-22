using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using Fixture = ConductorDriverTestsAttributionPending.Fixture;

public sealed class ConductorAttributionPendingRecoveryTests : GoalWorktreeTestBase
{
    // Each case owns its workspace, SQLite stores and injected operations. Production audit
    // calls consume global hooks; GoalWorktreeTestsAcceptanceRetry isolates their setters.
    [Fact]
    public async Task ResolvedOccurrenceReplayCannotCloseNewerSameReasonOrOrdinaryAttention()
    {
        using var fixture = new Fixture();
        var goal = PreparePending(fixture);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        const string reason = "same pending reason";
        var occurrenceA = goal.LatestAcceptanceFailure!.OccurredAt;
        Raise(workspace, goal, store, reason);
        Raise(workspace, goal, store, reason);
        var raisedA = Assert.Single(await store.ListAsync(goal.Id.Value));
        Assert.Equal(OperatorInbox.LandingEscalationResolution.Resolved,
            OperatorInbox.ResolveLandingEscalationOccurrence(workspace, goal, "resolve A", occurrenceA));
        var resolvedA = Assert.Single(await store.ListAsync(goal.Id.Value));
        Assert.Equal(raisedA.Id, resolvedA.Id);
        Assert.Equal(CollaborationItemStatus.Resolved, resolvedA.Status);
        var recordA = ReadRecords(workspace).Single().GetRawText();

        // An ordinary no-marker escalation with the same reason keeps its historical behavior.
        var ordinary = WithOccurrence(fixture, occurrenceA.AddTicks(1), pending: false);
        Raise(workspace, ordinary, store, reason);
        var ordinaryItem = Assert.Single(await store.ListAsync(goal.Id.Value), row => row.Id != resolvedA.Id);
        var goalB = WithOccurrence(fixture, occurrenceA.AddTicks(2));
        Raise(workspace, goalB, store, reason);
        Raise(workspace, goalB, store, reason);
        var raisedB = Assert.Single(await store.ListAsync(goal.Id.Value), row =>
            row.Id != resolvedA.Id && row.Id != ordinaryItem.Id);
        Assert.NotEqual(resolvedA.CorrelationKey, raisedB.CorrelationKey);

        // Replay A using the current goal B, exactly as a delayed audit message would.
        Assert.Equal(OperatorInbox.LandingEscalationResolution.AlreadyResolved,
            OperatorInbox.ResolveLandingEscalationOccurrence(workspace, goalB, "replay A", occurrenceA));
        Assert.Equal("skipped-stale", Raise(workspace, goal, store, reason).JsonOutcome);
        var rows = await store.ListAsync(goal.Id.Value);
        Assert.Equal(3, rows.Count);
        AssertUnchanged(resolvedA, rows.Single(row => row.Id == resolvedA.Id));
        AssertUnchanged(raisedB, rows.Single(row => row.Id == raisedB.Id));
        AssertUnchanged(ordinaryItem, rows.Single(row => row.Id == ordinaryItem.Id));
        Assert.Equal(CollaborationItemStatus.Raised, rows.Single(row => row.Id == raisedB.Id).Status);
        Assert.Equal(recordA, ReadRecords(workspace).Single(row =>
            row.GetProperty("acceptanceFailureOccurredAt").GetDateTimeOffset() == occurrenceA).GetRawText());
    }

    [Fact]
    public async Task AbsentAttentionKeepsAuditOutboxRetryableUntilRaiseSucceeds()
    {
        using var fixture = new Fixture();
        var goal = PreparePending(fixture);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var failingRaise = Proxy(store, out var proxy);
        proxy.Raise = (_, _, _, _, _, _) => Task.FromException<CollaborationItem>(new IOException("raise unavailable"));
        Assert.Equal("error", Raise(workspace, goal, failingRaise).CollaborationOutcome);
        Assert.Empty(await store.ListAsync(goal.Id.Value));
        var before = File.ReadAllText(RecordPath(workspace));
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        var message = await QueueAudit(repository, goal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessAudit(repository, workspace, goal, message, store));
        Assert.Equal(before, File.ReadAllText(RecordPath(workspace)));
        Assert.Equal(message.Id, Assert.Single(await repository.ListOutboxMessagesAsync(GoalOperationJournal.AcceptanceRetryAuditOutboxKind)).Id);

        Raise(workspace, goal, store);
        Assert.True(await ProcessAudit(repository, workspace, goal, message, store));
        Assert.Empty(await repository.ListOutboxMessagesAsync(GoalOperationJournal.AcceptanceRetryAuditOutboxKind));
        var resolved = Assert.Single(await store.ListAsync(goal.Id.Value));
        Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
        Assert.Equal("audit repair", resolved.Resolution);
        Assert.Equal("acceptance-retry", Assert.Single(ReadRecords(workspace)).GetProperty("resolvedBy").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledStoreOperationLeavesAuditRetryableAndReleasesLock(bool cancel)
    {
        using var fixture = new Fixture();
        var goal = PreparePending(fixture);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        Raise(workspace, goal, store);
        var before = File.ReadAllText(RecordPath(workspace));
        var raised = Assert.Single(await store.ListAsync(goal.Id.Value));
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        var message = await QueueAudit(repository, goal);
        using var cancellation = new CancellationTokenSource();
        var failingStore = Proxy(store, out var proxy);
        var operationFinished = false;
        proxy.Resolve = async (_, _, token, _) =>
        {
            try
            {
                if (!cancel) throw new IOException("resolve unavailable");
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return false;
            }
            finally
            {
                // Force an asynchronous cancellation unwind; callers must await the actual task.
                await Task.Yield();
                operationFinished = true;
            }
        };
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ProcessAudit(repository, workspace, goal, message, failingStore, cancellation.Token));
        else
            await Assert.ThrowsAsync<IOException>(() => ProcessAudit(repository, workspace, goal, message, failingStore));
        Assert.True(operationFinished);
        Assert.Equal(before, File.ReadAllText(RecordPath(workspace)));
        AssertUnchanged(raised, Assert.Single(await store.ListAsync(goal.Id.Value)));
        Assert.Equal(message.Id, Assert.Single(await repository.ListOutboxMessagesAsync(GoalOperationJournal.AcceptanceRetryAuditOutboxKind)).Id);

        // This reacquires the same file lock and the released outbox claim, with no ambient hooks.
        Assert.True(await ProcessAudit(repository, workspace, goal, message, store));
        Assert.Empty(await repository.ListOutboxMessagesAsync(GoalOperationJournal.AcceptanceRetryAuditOutboxKind));
        var resolved = Assert.Single(await store.ListAsync(goal.Id.Value));
        Assert.Equal(raised.Id, resolved.Id);
        Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
        Assert.Equal(OperatorInbox.LandingEscalationResolution.AlreadyResolved,
            OperatorInbox.ResolveLandingEscalationOccurrence(workspace, goal, "repeat audit", goal.LatestAcceptanceFailure!.OccurredAt));
        AssertUnchanged(resolved, Assert.Single(await store.ListAsync(goal.Id.Value)));
        Assert.Single(GoalOperationJournal.Read(fixture.Root, goal.Id).Entries, entry => entry.Operation == "acceptance-retry");
    }

    [Fact]
    public async Task CancelledRaiseIsAwaitedAndCannotRecreateResolvedOccurrence()
    {
        using var fixture = new Fixture();
        var goal = PreparePending(fixture);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var failingStore = Proxy(store, out var proxy);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationFinished = false;
        proxy.Raise = async (type, id, subject, body, key, token) =>
        {
            try
            {
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = token.Register(() => cancelled.TrySetCanceled(token));
                entered.SetResult();
                await cancelled.Task;
                return await store.RaiseAsync(type, id, subject, body, key, token);
            }
            finally
            {
                cancelling.SetResult();
                await release.Task;
                operationFinished = true;
            }
        };
        var raise = Task.Run(() => OperatorInbox.RecordLandingEscalation(workspace, goal, "pending reason",
            "conductor:acceptance", collaborationStore: failingStore,
            collaborationRaiseTimeout: Timeout.InfiniteTimeSpan, cancellationToken: cancellation.Token));
        try
        {
            await AwaitSignal(entered.Task, "pending raise entered store");
            cancellation.Cancel();
            await AwaitSignal(cancelling.Task, "pending raise observed cancellation");
            // The writer is still unwinding, so the occurrence lock must still be owned.
            Assert.Throws<IOException>(() =>
            {
                using var competingLock = new FileStream(Path.Combine(workspace.OrchestratorDirectory,
                    "landing-escalations.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            });
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await AwaitSignal(raise, "pending raise completed cancellation unwind");
        }
        Assert.Equal("timeout", (await raise).CollaborationOutcome);
        Assert.True(operationFinished);
        Assert.Empty(await store.ListAsync(goal.Id.Value));
        Raise(workspace, goal, store);
        var occurrence = goal.LatestAcceptanceFailure!.OccurredAt;
        Assert.Equal(OperatorInbox.LandingEscalationResolution.Resolved,
            OperatorInbox.ResolveLandingEscalationOccurrence(workspace, goal, "repair", occurrence));
        var resolved = Assert.Single(await store.ListAsync(goal.Id.Value));
        Assert.Equal("skipped-stale", Raise(workspace, goal, store).JsonOutcome);
        AssertUnchanged(resolved, Assert.Single(await store.ListAsync(goal.Id.Value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOccurrenceOnTypedMarkerFailsClosed(bool blankMarker)
    {
        using var fixture = new Fixture();
        var goal = PreparePending(fixture);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        Raise(workspace, goal, store);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(RecordPath(workspace)))!;
        var record = json["items"]![0]!;
        if (blankMarker) record["attributionPendingIdentity"] = "";
        else record["acceptanceFailureOccurredAt"] = null;
        File.WriteAllText(RecordPath(workspace), json.ToJsonString());
        var before = File.ReadAllText(RecordPath(workspace));
        Assert.Throws<InvalidOperationException>(() => OperatorInbox.ResolveLandingEscalationOccurrence(
            workspace, goal, "repair", goal.LatestAcceptanceFailure!.OccurredAt));
        Assert.Equal(before, File.ReadAllText(RecordPath(workspace)));
        Assert.Equal(CollaborationItemStatus.Raised, Assert.Single(await store.ListAsync(goal.Id.Value)).Status);
    }

    private static Goal PreparePending(Fixture fixture)
    {
        var driver = MakeDriver(runAcceptanceSummary: _ => fixture.Summary,
            recordAcceptanceFailure: (goal, checks, branch, main, attrs, baseline) =>
                fixture.Kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attrs, baseline),
            getLandingFileScopes: _ => ["src/Changed.cs"], apparatusRedGate: fixture.Gate);
        ConductorDriverTestsAttributionPending.AssertPending(
            driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative), fixture.Goal);
        return fixture.Goal;
    }

    private static Goal WithOccurrence(Fixture fixture, DateTimeOffset occurrence, bool pending = true)
    {
        var snapshot = fixture.Kernel.ExportSnapshot();
        var goal = snapshot.Goals.Single();
        var failure = goal.LatestAcceptanceFailure!;
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goal with { LatestAcceptanceFailure = failure with
                { OccurredAt = occurrence, AttributionPending = pending ? failure.AttributionPending : null } }]
        }).GetGoal(fixture.Goal.Id);
    }

    private static LandingEscalationWriteResult Raise(OrchestratorWorkspace workspace, Goal goal,
        ICollaborationItemStore store, string reason = "pending reason") =>
        OperatorInbox.RecordLandingEscalation(workspace, goal, reason, "conductor:acceptance", collaborationStore: store);

    private static string RecordPath(OrchestratorWorkspace workspace) =>
        Path.Combine(workspace.OrchestratorDirectory, "landing-escalations.json");

    private static async Task AwaitSignal(Task task, string signal)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException exception) { throw new TimeoutException($"Did not observe {signal}.", exception); }
    }

    private static JsonElement[] ReadRecords(OrchestratorWorkspace workspace)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RecordPath(workspace)));
        return document.RootElement.GetProperty("items").EnumerateArray().Select(row => row.Clone()).ToArray();
    }

    private static void AssertUnchanged(CollaborationItem expected, CollaborationItem actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.CorrelationKey, actual.CorrelationKey);
        Assert.Equal(expected.Body, actual.Body);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Resolution, actual.Resolution);
        Assert.Equal(expected.RaisedAt, actual.RaisedAt);
        Assert.Equal(expected.ResolvedAt, actual.ResolvedAt);
    }

    private static async Task<OrchestratorStateOutboxMessage> QueueAudit(SqliteOrchestratorStateRepository repository, Goal goal)
    {
        var message = GoalOperationJournal.CreateAcceptanceRetryAuditMessage(goal, "audit repair", Fixture.Main, Fixture.Main,
            1, goal.LatestAcceptanceFailure!.OccurredAt);
        await repository.EnsureOutboxMessageAsync(message);
        return message;
    }

    private static Task<bool> ProcessAudit(SqliteOrchestratorStateRepository repository, OrchestratorWorkspace workspace,
        Goal goal, OrchestratorStateOutboxMessage message, ICollaborationItemStore store,
        CancellationToken cancellationToken = default) =>
        repository.TryProcessOutboxMessageAsync(message.Id, (pending, _) => Task.FromResult(
            GoalOperationJournal.ApplyAcceptanceRetryAuditMessage(workspace, goal, pending, store, cancellationToken)));

    private static ICollaborationItemStore Proxy(ICollaborationItemStore store, out PendingStoreProxy proxy)
    {
        var result = DispatchProxy.Create<ICollaborationItemStore, PendingStoreProxy>();
        proxy = (PendingStoreProxy)result;
        proxy.Store = store;
        return result;
    }

    public class PendingStoreProxy : DispatchProxy
    {
        public ICollaborationItemStore Store { get; set; } = null!;
        public Func<string, string, CancellationToken, int?, Task<bool>>? Resolve { get; set; }
        public Func<CollaborationItemType, string?, string, string, string?, CancellationToken, Task<CollaborationItem>>? Raise { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ICollaborationItemStore.TryResolveAsync) && Resolve is not null)
                return Resolve((string)args![0]!, (string)args[1]!, (CancellationToken)args[2]!, (int?)args[3]);
            if (method.Name == nameof(ICollaborationItemStore.RaiseAsync) && Raise is not null)
                return Raise((CollaborationItemType)args![0]!, (string?)args[1], (string)args[2]!,
                    (string)args[3]!, (string?)args[4], (CancellationToken)args[5]!);
            return method.Invoke(Store, args);
        }
    }

    [Theory]
    [InlineData("introduced")]
    [InlineData("inherited")]
    [InlineData("repaired")]
    public async Task ConfirmedOperatorRetryClearsPersistentHoldAndReevaluatesNewEvidence(string nextEvidence)
    {
        var repo = CreateSeededRepository();
        try
        {
            using var fixture = new Fixture();
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var initial = MakeDriver(runAcceptanceSummary: _ => fixture.Summary,
                recordAcceptanceFailure: (goal, checks, branch, main, attrs, baseline) =>
                    fixture.Kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attrs, baseline),
                getLandingFileScopes: _ => ["src/Changed.cs"], apparatusRedGate: fixture.Gate,
                writeEscalationWithResult: (goal, _, reason) => OperatorInbox.RecordLandingEscalation(workspace, goal, reason, "conductor:acceptance", collaborationStore: store));
            var held = ConductorDriverTestsAttributionPending.AssertPending(initial.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative), fixture.Goal);
            var attention = Assert.Single(await store.ListAsync(fixture.Goal.Id.Value));
            Assert.Equal(CollaborationItemStatus.Raised, attention.Status);
            await repository.SaveAsync(fixture.Kernel);
            var kernel = await repository.LoadAsync();
            var goal = kernel.GetGoal(fixture.Goal.Id);
            Assert.Equal(held.StableIdentity, goal.LatestAcceptanceFailure!.AttributionPending!.Identity);
            var occurrence = goal.LatestAcceptanceFailure.OccurredAt;
            var context = CreateAcceptanceContext(kernel, repo, goal);
            Assert.Throws<ArgumentException>(() => CliCommandHandlers.Execute(
                ["acceptance-retry", goal.Id.Value[..8], "verified repair supplied"], context));
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.Equal(occurrence, goal.LatestAcceptanceFailure!.OccurredAt);
            Assert.Equal(OperatorInbox.LandingEscalationResolution.Superseded,
                OperatorInbox.ResolveLandingEscalationOccurrence(workspace, goal, "stale retry", occurrence.AddTicks(-1)));
            Assert.Equal(CollaborationItemStatus.Raised, Assert.Single(await store.ListAsync(goal.Id.Value)).Status);
            CliCommandHandlers.Execute(["acceptance-retry", goal.Id.Value[..8], "verified repair supplied", "--confirm-acceptance-retry"], context);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Null(goal.RetainedAcceptanceFailure);
            Assert.Equal(1, goal.OperatorAcceptanceRegateCount);
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
            var resolved = Assert.Single(await store.ListAsync(goal.Id.Value));
            Assert.Equal(attention.Id, resolved.Id);
            Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
            Assert.Equal("verified repair supplied", resolved.Resolution);
            Assert.Equal(OperatorInbox.LandingEscalationResolution.AlreadyResolved,
                OperatorInbox.ResolveLandingEscalationOccurrence(workspace, goal, "audit replay", occurrence));
            var replayed = Assert.Single(await store.ListAsync(goal.Id.Value));
            Assert.Equal(resolved.Id, replayed.Id);
            Assert.Equal(resolved.Status, replayed.Status);
            Assert.Equal(resolved.Resolution, replayed.Resolution);
            Assert.Equal(resolved.ResolvedAt, replayed.ResolvedAt);
            await repository.SaveAsync(kernel);
            var cleared = (await repository.LoadAsync()).GetGoal(goal.Id);
            Assert.Null(cleared.RetainedAcceptanceFailure);

            var attributions = Fixture.Identities.Select(identity => new AcceptanceTestFailureAttribution(identity,
                nextEvidence == "inherited" ? AcceptanceTestFailureOrigin.Inherited : AcceptanceTestFailureOrigin.Introduced,
                nextEvidence == "inherited" ? "executed baseline failed for exact identity; receipt baseline.trx" : "executed baseline passed for exact identity; receipt baseline.trx")).ToArray();
            fixture.Check = fixture.Check with { FailingTestAttributions = attributions };
            var gates = 0;
            var developerRetries = 0;
            var next = MakeDriver(runAcceptanceSummary: _ =>
                {
                    gates++;
                    return nextEvidence == "repaired" ? AcceptanceVerificationSummary.PassedWithNoUnmetCriteria : fixture.Summary;
                },
                retryTaskWithCause: (id, task, message, round, cause) =>
                { developerRetries++; return kernel.RetryTask(id, task, message, retryRoundKind: round, retryCause: cause); },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                recordAcceptanceFailure: (g, checks, branch, main, attrs, baseline) => kernel.RecordAcceptanceFailure(g.Id, checks, branch, main, attrs, baseline),
                getLandingFileScopes: _ => ["src/Changed.cs"], apparatusRedGate: fixture.Gate);
            var result = next.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
            Assert.Equal(1, gates);
            Assert.Equal(nextEvidence == "introduced" ? 1 : 0, developerRetries);
            Assert.Null(goal.LatestAcceptanceFailure?.AttributionPending);
            if (nextEvidence == "inherited")
            {
                var inherited = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
                Assert.Equal(GoalLifecycleState.Verified, inherited.State);
                Assert.Equal("excluded-unattributable-hold", inherited.Decision!.Facts.Single(fact => fact.Name == "disposition").Value);
            }
            else Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.Single(await store.ListAsync(goal.Id.Value));
        }
        finally { DeleteDirectory(repo); }
    }
}
