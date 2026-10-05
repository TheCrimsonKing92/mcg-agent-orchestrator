using System.Text.Json;
using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// The reused scenario owns its temporary repository, databases and build storage.
public sealed class AcceptanceCohortWorkflowTestsIdenticalFailureRetraction : AcceptanceCohortWorkflowTests
{
    private const string TestIdentity = AcceptanceCohortWorkflowTestsMainSuspect.TestA;

    [Fact]
    public async Task LaterIndeterminateFailure_RetractsBlameAndTripsExistingCircuitOnce()
    {
        using var scenario = AcceptanceCohortWorkflowTestsMainSuspect.CreateScenario();
        scenario.Driver.CohortPartitionRunner = new PartitionRunner(indeterminate: true);
        var earlierMembers = new[] { AbsentMember(), scenario.Selection.BindMembers()[0] };
        var earlier = Seed(scenario, earlierMembers, blame: true);
        var before = JsonSerializer.Serialize(scenario.Store.ReadPartitionReceipts(earlier.Value));
        var key = $"{earlierMembers[0].GoalId.Value}:{earlierMembers[0].CandidateRevision}";
        Assert.Contains(key, scenario.Store.ReadAttributedMemberKeys());

        var receipt = Assert.IsType<AcceptanceCohortReceipt>(scenario.Run().Receipt);

        Assert.Equal(AcceptanceCohortAttributionOutcome.Indeterminate, receipt.Attribution);
        Assert.Empty(receipt.AttributedMembers);
        Assert.Equal("main-suspect", receipt.AttributionSource);
        Assert.DoesNotContain(key, new CohortAcceptanceStore(DatabasePath(scenario)).ReadAttributedMemberKeys());
        Assert.Equal(before, JsonSerializer.Serialize(scenario.Store.ReadPartitionReceipts(earlier.Value)));
        Assert.Single(scenario.Store.TryReadReceipt(earlier.Value)!.AttributedMembers);
        var detail = scenario.Store.ReadAttributionReasonDetail(receipt.Identity.Value);
        Assert.Contains("identical-failure-without-member", detail, StringComparison.Ordinal);
        Assert.Contains(earlier.Value, detail, StringComparison.Ordinal);
        Assert.Contains(receipt.Identity.Value, detail, StringComparison.Ordinal);
        var failure = Assert.Single((await scenario.Events.ReadAllAsync())
            .Where(evt => evt.Kind == PostLandingCanaryEventKind.Failed));
        Assert.Equal("main-suspect", failure.Payload.FailureReason);
        Assert.Contains(detail!, failure.Payload.Detail, StringComparison.Ordinal);
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await scenario.Circuit.ReadAsync()).Health);
        var retracted = Assert.Single(scenario.ReadLog().Where(evt => evt.EventKind == "cohort-attribution-retracted"));
        Assert.Equal($"COHORT_ATTRIBUTION_RETRACTED goal={earlierMembers[0].GoalId.Value[..8]} earlier={earlier.Value} later={receipt.Identity.Value} tests=1", retracted.Detail);
        Assert.Equal("decision", retracted.Operator);
        var canary = Assert.Single(scenario.ReadLog().Where(evt => evt.EventKind == "canary-gate"));
        Assert.Contains("reason=main-suspect", canary.Detail, StringComparison.Ordinal);
        Assert.Contains(detail!, canary.Detail, StringComparison.Ordinal);
        Assert.Empty(scenario.Store.ReadSuppressedPairs());
        AcceptanceCohortWorkflowTestsMainSuspect.AssertMembersUnrouted(scenario);

        scenario.Run();
        Assert.Single(scenario.ReadLog().Where(evt => evt.EventKind == "cohort-attribution-retracted"));
        Assert.Single((await scenario.Events.ReadAllAsync()).Where(evt => evt.Kind == PostLandingCanaryEventKind.Failed));
        Assert.Equal(before, JsonSerializer.Serialize(scenario.Store.ReadPartitionReceipts(earlier.Value)));
    }

    [Fact]
    public async Task EarlierFailureWithoutMember_WithholdsLaterSingleReproducer()
    {
        using var scenario = AcceptanceCohortWorkflowTestsMainSuspect.CreateScenario();
        scenario.Driver.CohortPartitionRunner = new PartitionRunner(indeterminate: false);
        var earlier = Seed(scenario, [AbsentMember(), scenario.Selection.BindMembers()[1]], blame: false);

        var receipt = Assert.IsType<AcceptanceCohortReceipt>(scenario.Run().Receipt);

        Assert.Equal(AcceptanceCohortAttributionOutcome.FirstMemberFailed, receipt.Attribution);
        Assert.Empty(receipt.AttributedMembers);
        Assert.Empty(scenario.Store.ReadAttributedMemberKeys());
        Assert.Equal("main-suspect", receipt.AttributionSource);
        Assert.Contains(earlier.Value, scenario.Store.ReadAttributionReasonDetail(receipt.Identity.Value), StringComparison.Ordinal);
        Assert.Single((await scenario.Events.ReadAllAsync()).Where(evt => evt.Payload.FailureReason == "main-suspect"));
        Assert.Empty(scenario.ReadLog().Where(evt => evt.EventKind == "cohort-attribution-retracted"));
        AcceptanceCohortWorkflowTestsMainSuspect.AssertMembersUnrouted(scenario);
    }

    [Fact]
    public void Retraction_DoesNotRemoveIndependentBlameForSameCandidate()
    {
        using var scenario = AcceptanceCohortWorkflowTestsMainSuspect.CreateScenario();
        scenario.Driver.CohortPartitionRunner = new PartitionRunner(indeterminate: true);
        var a = AbsentMember();
        var earlier = Seed(scenario, [a, scenario.Selection.BindMembers()[0]], blame: true);
        var independent = Seed(scenario, [a, scenario.Selection.BindMembers()[1]], blame: true, test: "Tests.Other.Fails");

        scenario.Run();

        Assert.Contains($"{a.GoalId.Value}:{a.CandidateRevision}", scenario.Store.ReadAttributedMemberKeys());
        Assert.Single(scenario.Store.TryReadReceipt(independent.Value)!.AttributedMembers);
        Assert.Equal(earlier.Value, scenario.ReadLog().Single(evt => evt.EventKind == "cohort-attribution-retracted")
            .Detail.Split("earlier=", StringSplitOptions.None)[1].Split(' ')[0]);
    }

    [Fact]
    public void InterruptedRetraction_ReplaysEvidenceAndDeduplicatesOperatorEvent()
    {
        using var scenario = AcceptanceCohortWorkflowTestsMainSuspect.CreateScenario();
        scenario.Driver.CohortPartitionRunner = new PartitionRunner(indeterminate: true);
        var a = AbsentMember();
        var earlier = Seed(scenario, [a, scenario.Selection.BindMembers()[0]], blame: true);
        var members = scenario.Selection.BindMembers();
        var identity = AcceptanceCohortIdentity.Create(members, scenario.Selection.Members[0].MainRevision,
            new string('d', 40), "later-manifest");
        scenario.Store.SaveGateReceipt(new AcceptanceCohortReceipt("interrupted-receipt", identity,
            AcceptanceCohortGateOutcome.Failed, DateTimeOffset.Parse("2026-10-04T03:15:00Z"), 0, ["red"], 1, []));
        var method = typeof(ConductorDriver).GetMethod("SaveCohortPartitionAttribution",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var runner = new PartitionRunner(indeterminate: true);
        var failedWriter = new ConductEventLogWriter(scenario.Workspace.ConductEventsLogPath,
            beforeRequiredEventDrain: () => throw new IOException("controlled event-write failure"));
        AcceptanceCohortReceipt Invoke(ConductEventLogWriter log) => Assert.IsType<AcceptanceCohortReceipt>(
            method.Invoke(scenario.Driver, [scenario.Store, identity, members, "later-pair",
                new[] { TestIdentity }, runner.RunFull(members[0], 0, identity, log, default),
                runner.RunFull(members[1], 1, identity, log, default), log, null]));

        var fault = Assert.Throws<TargetInvocationException>(() => Invoke(failedWriter));
        Assert.IsType<IOException>(fault.InnerException);
        Assert.Equal(AcceptanceCohortAttributionOutcome.NotApplicable,
            scenario.Store.TryReadReceipt(identity.Value)!.Attribution);
        Assert.DoesNotContain($"{a.GoalId.Value}:{a.CandidateRevision}", scenario.Store.ReadAttributedMemberKeys());
        var writer = new ConductEventLogWriter(scenario.Workspace.ConductEventsLogPath);
        var receipt = Invoke(writer);
        Invoke(writer);

        Assert.Equal("main-suspect", receipt.AttributionSource);
        Assert.DoesNotContain($"{a.GoalId.Value}:{a.CandidateRevision}", scenario.Store.ReadAttributedMemberKeys());
        Assert.Single(scenario.ReadLog().Where(evt => evt.EventKind == "cohort-attribution-retracted"));
        Assert.Contains(earlier.Value, scenario.Store.ReadAttributionReasonDetail(identity.Value), StringComparison.Ordinal);
    }

    private static string DatabasePath(AcceptanceCohortWorkflowTestsMainSuspect.Scenario scenario) =>
        Path.Combine(scenario.Workspace.OrchestratorDirectory, "cohort-acceptance.db");

    private static AcceptanceCohortMemberBinding AbsentMember() => new(new GoalId(new string('a', 32)),
        new string('b', 40), new string('b', 40), ["src/Absent.cs"], ["resource:absent"],
        ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");

    private static AcceptanceCohortIdentity Seed(AcceptanceCohortWorkflowTestsMainSuspect.Scenario scenario,
        IReadOnlyList<AcceptanceCohortMemberBinding> members, bool blame, string test = TestIdentity)
    {
        var identity = AcceptanceCohortIdentity.Create(members, scenario.Selection.Members[0].MainRevision,
            new string('c', 40), "earlier-manifest");
        scenario.Store.SaveGateReceipt(new AcceptanceCohortReceipt($"receipt-{identity.Value}", identity,
            AcceptanceCohortGateOutcome.Failed, DateTimeOffset.Parse("2026-10-04T03:09:00Z"), 0, ["red"], 1, []));
        var partitions = members.Select((member, ordinal) => new AcceptanceCohortPartitionReceipt(
            $"partition-{identity.Value}-{ordinal}", member.GoalId, ordinal, member.CandidateRevision,
            identity.ObservedMainRevision, member.CandidateRevision, identity.ManifestIdentity,
            ordinal == 0 ? AcceptanceCohortGateOutcome.Failed : AcceptanceCohortGateOutcome.Passed, 0, [])
        { FailingTestIdentities = ordinal == 0 ? [test] : [] }).ToArray();
        scenario.Store.SaveAttribution(identity.Value, blame ? AcceptanceCohortAttributionOutcome.FirstMemberFailed :
            AcceptanceCohortAttributionOutcome.Indeterminate, partitions, $"pair-{identity.Value}", null,
            blame ? [new(members[0].GoalId, 0, members[0].CandidateRevision, [test])] : [],
            suppressPair: false, cohortFailingTests: [test]);
        return identity;
    }

    private sealed class PartitionRunner(bool indeterminate) : IConductorCohortPartitionRunner
    {
        public ConductorCohortFocusedPassResult RunFocused(AcceptanceCohortMemberBinding member, int ordinal,
            AcceptanceCohortIdentity identity, ConductorCohortFocusedSelection selection,
            ConductEventLogWriter writer, CancellationToken cancellationToken) =>
            ConductorAcceptanceCohortFocusedAttribution.Unusable(ConductorCohortFocusedPassKind.InfrastructureFailure, "fixture fallback");

        public AcceptanceCohortPartitionReceipt RunFull(AcceptanceCohortMemberBinding member, int ordinal,
            AcceptanceCohortIdentity identity, ConductEventLogWriter writer, CancellationToken cancellationToken) =>
            new($"later-{ordinal}", member.GoalId, ordinal, member.CandidateRevision,
                identity.ObservedMainRevision, member.CandidateRevision, identity.ManifestIdentity,
                indeterminate ? AcceptanceCohortGateOutcome.InfrastructureFailure : ordinal == 0 ?
                    AcceptanceCohortGateOutcome.Failed : AcceptanceCohortGateOutcome.Passed, 0, [])
            { FailingTestIdentities = ordinal == 0 ? [TestIdentity] : [] };
    }
}
