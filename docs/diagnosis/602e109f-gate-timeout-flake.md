# Gate failed on ONE 300-second timeout in a real-process lane. Not your change. Do not chase it.

Attempt `602e109f-0-20260820211610446` failed. Read this before changing anything.

## What failed

Lane `infrastructure tests: Process spawning`:

    <Counters total="608" executed="608" passed="607" failed="1" error="0" timeout="0" ... />

The single failure is not an assertion:

    System.TimeoutException : dotnet did not exit within 300 seconds.

607 of 608 passed. Every other lane in the gate passed with zero failures.

## Why this is environmental and not yours

That lane contains real-process tests that spawn an actual conductor binary and wait for it to exit,
including:

    ConductorSelfRelaunch_real_binary_build_self_check_and_handoff
    ConductorSelfRelaunch_real_handoff_failure_stops_successor_and_reacquires_incumbent_lease
    ConductorSelfRelaunch_real_successor_process_failure_is_classified_as_self_check

I could not isolate which of them failed from the TRX attribute ordering, but the shape is clear: a test
whose assertion is "a spawned dotnet process exits within 300 seconds" is measuring host throughput.

It was asked that question under heavy load. While this gate ran, the machine was carrying another
acceptance gate concurrently, and the conductor batch loop was stalled from 21:16:42 until max-duration
terminated it at 22:16:01.

**This is the THIRD gate failure today from the same lane and the same message.** Goal `ae9dccd4` failed the
same way roughly an hour earlier with TWO timeouts in this lane. Neither goal touches process-spawn timing.
Your change is to dispatch-result recognition in `BackgroundDispatchRunner` and
`DispatchFailureClassifier`, which has nothing to do with how long a spawned process takes to exit.

## What to do

Most likely nothing. This is an operator-owned re-gate, not a repair round, and the operator has been told
so. The gate cannot distinguish "your change broke this" from "the host was saturated", which is why you
were dispatched at all.

## What NOT to do

Do not delete, skip, weaken, or add retries to the timing-out test to make the gate green. It is not
wrong, it is just being asked a question about the machine.

Do not touch the dispatch-recognition change. Do not re-open the acceptance criteria. Do not add a
timeout-tuning change to this goal - that belongs to a separate flaky-lane item, not here, and mixing it in
would widen this goal's file scope and risk a collision.
