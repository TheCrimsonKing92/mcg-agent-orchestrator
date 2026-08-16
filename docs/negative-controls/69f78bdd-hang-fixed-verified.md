# The hang is fixed, verified by execution at `a4982266`

Follows `69f78bdd-hangs-loop-scheduling-test.md`, which bisected two 40-minute acceptance timeouts
to a single hanging test.

## Before and after

| Class | At `34aab425` | At `a4982266` |
| --- | --- | --- |
| `ConductorBatchLoopTestsLoopSchedulingPolicy` | **hung past 540 s**, and the single method hung past 180 s | **31 tests, 24.8 s, all pass** |
| `ConductorBatchLoopTestsParallelAcceptance` | 49 tests, 1 m 16 s, **1 failed** | **49 tests, 1 m 48 s, all pass** |

Run directly against the built apphost in a disposable detached checkout, same filters the lane
uses. The second row matters as much as the first: the earlier single failure in
`ParallelAcceptance` is also gone, so the whole
`infrastructure-tests-dotnet-build-slots` lane is clean at this candidate.

## Why it works

`ConductorBatchLoopContinuesTickingWhileGoalRefinementIsInFlight` previously depended on the eager
`TryLaunchFirstPending` call this goal removed, so nothing put refinement in flight and the wait
had no bound. The fix makes the test observe the durable outbox directly:

    var refinementMessage = Assert.Single(
        await repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind));
    Assert.Equal(GoalRefinementWorkCoordinator.MessageId(currentGoal!.Id), refinementMessage.Id);

    using var refinementDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var refinementTask = Task.Run(() => GoalRefinementWorkCoordinator.ProcessAsync(
        currentGoal.Id, refinementDeadline.Token));
    Assert.True(refiner.Entered.Wait(TimeSpan.FromSeconds(15)),
        "Durable refinement work did not start.");

It asserts the outbox message exists and is the right one, drives it deliberately, and bounds both
the processing deadline and the entry wait. That is the correct shape for this goal: the test now
exercises the mechanism the goal introduced rather than the one it deleted, and it can fail rather
than hang.

## Note for acceptance

The two prior acceptance failures on this goal were `acceptance-check-timeout` at 40 minutes, not
test failures. An earlier operator note wrongly attributed the first of them to temp-reaper
overhead; that attribution is retracted in the preceding document. The cause was this hang, and it
is fixed.
