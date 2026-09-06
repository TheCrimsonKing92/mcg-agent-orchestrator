using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

public sealed class ConductorBatchLoopTestsRetryReservationReadmit : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsRetryReservationReadmit(ITestOutputHelper output) : base(output)
    {
    }

    [Xunit.Fact]
    public void ExpiredReservationReadmitsOnceWithoutDurableTaskChange()
    {
        var now = DateTimeOffset.Parse("2026-09-05T15:48:00Z");
        var (kernel, goal) = ConductorDriverTests.SimpleGoal("Readmit an expired retry reservation");
        var task = goal.Tasks.Single();
        var dispatchAt = now.AddMinutes(-1);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("reviewer", "review", "C:\\tmp", dispatchAt));
        var ownerReceipt = new RetryAdmissionReceipt(
            "reservation-expiry-receipt",
            RetryCause.EnvironmentApparatusFailure,
            new RetryContextFingerprint(RetryContextFingerprint.CurrentSchemaVersion, "same-candidate"),
            RetryAdmissionDecision.Allowed,
            RetryAdmissionRoute.ReservationLease,
            PaidRouteClassification.Paid,
            dispatchAt,
            now,
            ReservationOwnerId: "exited-round",
            ReservationLeaseExpiresAt: now.AddMinutes(1));
        kernel.ApplyPreparedRetryAdmission(
            goal.Id,
            task.Id,
            new RetryAdmissionResult(RetryAdmissionDecision.Allowed, ownerReceipt));
        var contenderReceipt = ownerReceipt with
        {
            ReceiptId = "later-contender-receipt",
            Decision = RetryAdmissionDecision.Prevented,
            RecordedAt = now.AddSeconds(1),
            ReservationOwnerId = "contender",
            ReservationLeaseExpiresAt = now.AddMinutes(5)
        };
        kernel.ApplyPreparedRetryAdmission(
            goal.Id,
            task.Id,
            new RetryAdmissionResult(RetryAdmissionDecision.Prevented, contenderReceipt));

        var taskStateBeforeExpiry = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id).Tasks.Single());
        var snapshots = new List<string>();
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            startRecordedDispatches: _ => DispatchStartOutcome.EmptyBatch(
                $"Prepared retry reservation is owned until {ownerReceipt.ReservationLeaseExpiresAt:u}."));

        var summary = new ConductorBatchLoop(utcNow: () => now).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ =>
            {
                snapshots.Add(JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id).Tasks.Single()));
                now = now.AddMinutes(2);
                return false;
            });

        Xunit.Assert.Equal(2, summary.Ticks);
        Xunit.Assert.Equal(2, snapshots.Count);
        Xunit.Assert.All(snapshots, snapshot => Xunit.Assert.Equal(taskStateBeforeExpiry, snapshot));
        var readmissions = goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("retry reservation expired", StringComparison.Ordinal)).ToArray();
        var readmission = Xunit.Assert.Single(readmissions);
        Xunit.Assert.Contains(task.Id.Value[..8], readmission.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains(ownerReceipt.ReceiptId, readmission.Message, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(contenderReceipt.ReceiptId, readmission.Message, StringComparison.Ordinal);
    }
}
