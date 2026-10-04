using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal IConductorCohortPartitionRunner? CohortPartitionRunner { get; set; }

    private AcceptanceCohortReceipt AttributeFailedCohort(CohortAcceptanceStore store,
        AcceptanceCohortIdentity identity, IReadOnlyList<AcceptanceCohortMemberBinding> bindings,
        string pairFingerprint, IReadOnlyList<string> cohortFailingTests,
        IReadOnlyList<AcceptanceCheckResult> cohortFailedChecks, ConductEventLogWriter writer,
        CancellationToken cancellationToken)
    {
        AppendCohortAttributionStartEvent(writer, identity, bindings);
        var runner = CohortPartitionRunner ?? new DefaultCohortPartitionRunner(this);
        var selection = ConductorAcceptanceCohortFocusedAttribution.TrySelect(cohortFailingTests, cohortFailedChecks);
        if (selection is not null)
        {
            var firstFocused = RunFocusedSafely(runner, bindings[0], 0, identity, selection, writer, cancellationToken);
            var secondFocused = firstFocused.Kind == ConductorCohortFocusedPassKind.Executed
                ? RunFocusedSafely(runner, bindings[1], 1, identity, selection, writer, cancellationToken)
                : ConductorAcceptanceCohortFocusedAttribution.Unusable(firstFocused.Kind, "first-member-unusable");
            if (ConductorAcceptanceCohortFocusedAttribution.Decide(cohortFailingTests, firstFocused, secondFocused) is { } reproducer)
            {
                var first = FocusedPartitionReceipt(bindings[0], 0, identity, firstFocused, reproducer == 0);
                var second = FocusedPartitionReceipt(bindings[1], 1, identity, secondFocused, reproducer == 1);
                var receipt = SaveCohortPartitionAttribution(store, identity, bindings, pairFingerprint,
                    cohortFailingTests, first, second, writer, AcceptanceCohortAttributionSources.FocusedPass);
                AppendFocusedAttributionEvent(writer, identity, "attributed", "single-reproducer");
                return receipt;
            }
            AppendFocusedAttributionEvent(writer, identity, "fallback",
                firstFocused.Kind != ConductorCohortFocusedPassKind.Executed ? firstFocused.Kind.ToString() :
                secondFocused.Kind != ConductorCohortFocusedPassKind.Executed ? secondFocused.Kind.ToString() : "non-dispositive");
        }
        else AppendFocusedAttributionEvent(writer, identity, "fallback", "selection-unavailable");

        var firstFull = runner.RunFull(bindings[0], 0, identity, writer, cancellationToken);
        var secondFull = runner.RunFull(bindings[1], 1, identity, writer, cancellationToken);
        return SaveCohortPartitionAttribution(store, identity, bindings, pairFingerprint,
            cohortFailingTests, firstFull, secondFull, writer);
    }

    private AcceptanceCohortReceipt SaveCohortPartitionAttribution(CohortAcceptanceStore store,
        AcceptanceCohortIdentity identity, IReadOnlyList<AcceptanceCohortMemberBinding> bindings,
        string pairFingerprint, IReadOnlyList<string> cohortFailingTests,
        AcceptanceCohortPartitionReceipt first, AcceptanceCohortPartitionReceipt second,
        ConductEventLogWriter writer, string? source = null)
    {
        var classified = ConductorAcceptanceCohortFailingTestAttribution.Classify(cohortFailingTests, first, second);
        var workspace = _cohortWorkspace
            ?? throw new InvalidOperationException("Production acceptance cohort workspace is unavailable.");
        var sharedTests = ConductorAcceptanceCohortMainSuspect.TryDecide(classified, bindings,
            workspace.ExecutionDirectory,
            test => AcceptanceTestSourceResolver.ResolveSourcePaths(workspace.ExecutionDirectory, null, test));
        if (sharedTests is not null)
        {
            var sha = identity.ObservedMainRevision;
            var tests = ConductorAcceptanceCohortMainSuspect.FormatTests(sharedTests);
            var detail = $"main-suspect cohort={identity.Value} shared-failing-tests={sharedTests.Count} tests={tests}";
            var now = _utcNow();
            var events = new PostLandingCanaryEventStore(
                new SqliteRunEventStore(workspace.RunEventStorePath), workspace.RunEventStorePath);
            var appended = events.AppendOnceAsync(PostLandingCanaryEventKind.Failed,
                new PostLandingCanaryEventPayload(PostLandingCanaryEventPayload.CanaryTag,
                    sha, [], ConductorAcceptanceCohortMainSuspect.FailureToken, sharedTests.Count, detail, null, now,
                    SharedFailingTests: sharedTests),
                ConductorAcceptanceCohortMainSuspect.EventId(sha, identity.Value), now).GetAwaiter().GetResult().Appended;
            // Persistence failure must fault the gate before attribution is saved, without a process-local hold.
            // Record the circuit first: a crash before attribution persistence can safely replay the append.
            var receipt = store.SaveAttribution(identity.Value, classified.Outcome, [first, second], pairFingerprint,
                null, [], classified.UnrelatedFailures, ConductorAcceptanceCohortMainSuspect.FailureToken, suppressPair: false);
            if (appended)
                TryAppendGateProgressEvent(writer, goalId: null,
                    $"CANARY_GATE sha={sha} result=failed reason=main-suspect cohort={identity.Value} tests={tests}",
                    eventKind: "canary-gate");
            return receipt;
        }
        var attribution = classified.Outcome;
        var innocentGoalId = attribution switch
        {
            AcceptanceCohortAttributionOutcome.FirstMemberFailed => bindings[1].GoalId,
            AcceptanceCohortAttributionOutcome.SecondMemberFailed => bindings[0].GoalId,
            _ => (GoalId?)null
        };
        return store.SaveAttribution(identity.Value, attribution, [first, second], pairFingerprint,
            innocentGoalId, classified.AttributedMembers, classified.UnrelatedFailures, source);
    }

    private static ConductorCohortFocusedPassResult RunFocusedSafely(IConductorCohortPartitionRunner runner,
        AcceptanceCohortMemberBinding member, int ordinal, AcceptanceCohortIdentity identity,
        ConductorCohortFocusedSelection selection, ConductEventLogWriter writer, CancellationToken cancellationToken)
    {
        try { return runner.RunFocused(member, ordinal, identity, selection, writer, cancellationToken); }
        catch (Exception ex)
        {
            return ConductorAcceptanceCohortFocusedAttribution.Unusable(
                ConductorCohortFocusedPassKind.InfrastructureFailure, $"{ex.GetType().Name}:{BoundCohortDetail(ex.Message)}");
        }
    }

    private static AcceptanceCohortPartitionReceipt FocusedPartitionReceipt(AcceptanceCohortMemberBinding member,
        int ordinal, AcceptanceCohortIdentity identity, ConductorCohortFocusedPassResult result, bool reproduces) =>
        new($"cohort-focused-partition-v1-{identity.Value}-{ordinal}", member.GoalId, ordinal,
            member.CandidateRevision, identity.ObservedMainRevision, result.TreeRevision, identity.ManifestIdentity,
            reproduces ? AcceptanceCohortGateOutcome.Failed : AcceptanceCohortGateOutcome.Passed,
            result.ElapsedMilliseconds, result.TestResultPaths)
        { FailingTestIdentities = result.FailingIdentities, FailedChecks = result.FailedChecks };

    private static void AppendFocusedAttributionEvent(ConductEventLogWriter writer, AcceptanceCohortIdentity identity,
        string result, string reason) => TryAppendGateProgressEvent(writer, goalId: null,
            $"ATTRIBUTION_FOCUSED {identity.Value} result={result} reason={reason} scope=attribution",
            eventKind: "cohort-attribution-focused");

    private sealed class DefaultCohortPartitionRunner(ConductorDriver driver) : IConductorCohortPartitionRunner
    {
        public AcceptanceCohortPartitionReceipt RunFull(AcceptanceCohortMemberBinding member, int ordinal,
            AcceptanceCohortIdentity identity, ConductEventLogWriter writer, CancellationToken cancellationToken) =>
            driver.RunCohortPartition(member, ordinal, identity, writer, cancellationToken);

        public ConductorCohortFocusedPassResult RunFocused(AcceptanceCohortMemberBinding member, int ordinal,
            AcceptanceCohortIdentity identity, ConductorCohortFocusedSelection selection,
            ConductEventLogWriter writer, CancellationToken cancellationToken)
        {
            var workspace = driver._cohortWorkspace
                ?? throw new InvalidOperationException("Production acceptance cohort workspace is unavailable.");
            var verifier = driver._cohortAcceptanceVerifier
                ?? throw new InvalidOperationException("Production acceptance cohort verifier is unavailable.");
            var clock = Stopwatch.StartNew();
            using var partition = GoalWorktrees.CreateAcceptancePartitionWorkspace(
                workspace.ExecutionDirectory, identity.ObservedMainRevision, member, driver._cohortCleanupHooks);
            using var lease = driver.CohortPartitionStableSlotLeaseSource is { } source
                ? source(identity.Value, cancellationToken)
                : driver._parallelAcceptanceAttemptCoordinator.AcquireCohortStableSlotLease(identity.Value, cancellationToken);
            var result = AcceptanceExecutionRunner.RunFocusedVerification(verifier, partition.Path, member.GoalId,
                selection.Request, lease.Environment.BuildPermitIndex, lease, runBaselineArm: false, cancellationToken);
            partition.AssertGoalBranchesUnchanged();
            return ConductorAcceptanceCohortFocusedAttribution.Interpret(result, selection) with
            {
                TreeRevision = partition.TreeRevision,
                ElapsedMilliseconds = checked((long)clock.Elapsed.TotalMilliseconds),
                TestResultPaths = NormalizeCohortTestResultPaths(result.Checks.SelectMany(check => check.TestResultPaths ?? []).ToArray())
            };
        }
    }
}
