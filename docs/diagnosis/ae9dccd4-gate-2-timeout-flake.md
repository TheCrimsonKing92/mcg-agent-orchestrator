# Gate 2 failed on TWO TIMEOUTS under machine contention. Your previous fix WORKED. Do not chase a logic bug.

Attempt `ae9dccd4-0-20260820200404620` failed. Read this before changing anything.

## What failed

Lane `infrastructure tests: Process spawning`:

    <Counters total="608" executed="608" passed="606" failed="2" error="0" timeout="0" ... />

Both failures carry the same message and it is not an assertion:

    System.TimeoutException : dotnet did not exit within 300 seconds.

606 of 608 passed. Two hit a 300-second wall-clock cap.

## Why this is environmental

While this gate ran, the machine was carrying:

- TWO acceptance gates concurrently, this one and `08537608`, each spawning parallel test lanes.
- A conductor batch loop that stopped ticking entirely from 20:05:51 to 21:03:59, roughly 58 minutes, while
  gate threads kept running.

A test whose assertion is "a spawned `dotnet` process exits within 300 seconds" is measuring the machine, not
the code, and it was asked that question during the worst contention of the day.

## Your previous fix worked. Do not undo it.

Gate 1 failed with SIX failures in the `Goal lifecycle commands` lane, every one of them:

    System.IO.IOException : The process cannot access the file 'merge-train-acceptance.db'
    because it is being used by another process.

You fixed that by setting `Pooling = false` on the SQLite connection in `MergeTrainAcceptanceStore`. Gate 2
shows ZERO such failures and a completely different lane failing for a completely different reason. The
file-share defect is resolved.

## What to do

Most likely nothing to the merge-train code. This is an operator-owned re-gate, not a repair round, and the
operator has been told so.

If you want to make one durable improvement, the defensible one is to stop these two tests measuring host
speed: give the exit-wait an explicit, generous, and NAMED budget rather than a bare 300-second literal, or
seam the wait so a test can drive it deterministically. Only do that if you can do it without changing what
the tests actually prove.

## What NOT to do

Do not delete, skip, or weaken the two timing-out tests to make the gate green.

Do not revert or loosen the `Pooling = false` change.

Do not re-open the cohort or landing logic. Focused evidence on `AcceptanceCohortWorkflowTests` was 32 total,
32 executed, 32 passed at this candidate, and the Reviewer cleared all four previously open findings.
