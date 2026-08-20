# Two tests assert opposite things about `currentGoal` after a throw — the `finally` is too coarse

Operator diagnosis, 2026-08-20. Read this before changing `CliCommandDispatcher.ExecuteCommand` again.

## The failing test

    CliCommandTestsPersistentRunnerCommands.PersistentRunnerGoalCreateRejectsCompetingBacklogLinkAtomically
    tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.PersistentRunnerCommands.cs:2128

    Assert.Null() Failure: Value is not null
    Expected: null
    Actual:   Goal { AuthoritativeBrief = ... Text = Create one linked goal ... }

## Why this candidate caused it

The current change wraps the handler call so the `ref` outputs are assigned on every exit path:

    try { changed = CliCommandHandlers.Execute(parts, context); }
    finally
    {
        // Assign even when Execute throws: goal-create can hold later on SPEC_REFINEMENT_PENDING.
        agents = context.Agents;
        workerProfiles = context.WorkerProfiles;
        currentGoal = context.CurrentGoal;
    }

That fixed `Cli_simple_goal_with_dispatch_creates_goal_and_defers_worker_until_refined`, which asserts
`NotNull(currentGoal)` after a `SPEC_REFINEMENT_PENDING` throw. It broke the test above, which asserts
`Null(currentGoal)` after a competing-backlog-link rejection.

Both assertions are correct. They describe different situations:

| Throw | Was a goal created? | Correct `currentGoal` |
|---|---|---|
| `SPEC_REFINEMENT_PENDING` | YES - created, then held pending refinement | the created goal |
| competing backlog link rejected | NO - the whole create is rejected atomically | null |

So the question is not "should we assign on throw" but "did the operation actually produce a goal". A
blanket `finally` cannot answer that; it assigns whatever `context.CurrentGoal` happens to hold.

## What to do

Make the assignment conditional on the operation having actually created or resolved a goal, rather than
unconditional on exit. Options, in rough order of preference:

1. Have the rejection path clear `context.CurrentGoal` before throwing, so `finally` propagates null
   correctly and the atomicity is expressed where the rejection is decided.
2. Assign only for the exception types or conditions where a goal genuinely exists - the pending-refinement
   hold - and leave the outputs untouched otherwise.
3. Give `context` an explicit flag for "a goal was committed", and gate the assignment on it.

Option 1 keeps the atomicity guarantee next to the code that owns it, which is where a reader will look.

## Do NOT

Do not weaken either assertion. `Assert.Null` here is the ATOMICITY guarantee - if a competing backlog link
is rejected, no goal may be observable to the caller. `Assert.NotNull` in the other test is the
pending-refinement contract. Making either test agree with current behaviour would delete a real invariant.

Do not revert the `finally` wholesale either; it fixed a real defect where the exception escaped before the
ref assignment.

## Verification

Both of these must pass together, and they are the discriminating pair:

    CliCommandTestsPersistentRunnerCommands.PersistentRunnerGoalCreateRejectsCompetingBacklogLinkAtomically
    CliCommandTestsSubscriptionDispatchCommands.CliSimpleGoalWithDispatchCreatesGoalAndDefersWorkerUntilRefined

This was the only failure in gate attempt 9f64cd98-0-20260820104858058 - every other lane was green.
