# Gate failed: merge-train-acceptance.db is opened without sharing and collides under parallel tests

The acceptance gate attempt `ae9dccd4-0-20260820184825516` FAILED after about 24 minutes. This is not a flake
and it is not the cohort logic. The store this goal introduced cannot be opened by two processes at once.

## Evidence

Lane `infrastructure tests: Goal lifecycle commands`:

    <Counters total="370" executed="370" passed="364" failed="6" ... />

All six failures carry the same message:

    System.IO.IOException : The process cannot access the file 'merge-train-acceptance.db'
    because it is being used by another process.

Two of the named failures:

    CliCommandTestsPersistentRunnerCommands.ConductLoop_EvictedGoal_LaterIntentKeepsReason
    CliCommandTestsPersistentRunnerCommands.ConductLoop_LiveGoal_ReloadsAndWalks

These are conduct-loop tests. They exercise `ConductorBatchLoop`, which this goal edited, and they now touch
the new store transitively.

## What to fix

`MergeTrainAcceptanceStore` opens `merge-train-acceptance.db` in a way that excludes other readers or
writers. Under the test runner, more than one test process is live at once, so the second one faults.

This is the SAME fault class as backlog `5f21e8ee` and goal `9d804744`, "Goal-operations journal file-share
race faults parallel acceptance attempts". That goal exists because an unshared file handle converted a
healthy acceptance attempt into a failure. This change reintroduces the pattern in a new file.

Pick whichever is correct for the store's semantics and say why in your notes:

1. Open with explicit sharing that permits concurrent readers and a writer, the way a SQLite store normally
   needs to be opened.
2. Give each test an isolated store path so no two tests address the same file.
3. If the store genuinely must be exclusive, serialize the tests that reach it with the existing exclusive
   collection mechanism rather than leaving them parallel.

Option 2 alone would make the gate green while leaving the production race in place. If you choose it, say
explicitly whether production can still hit this with two conductors or a conductor plus a CLI verb, and if
it can, fix that too.

## What NOT to do

Do not delete or weaken the six failing tests. They are catching a real defect.

Do not disable parallelism for the whole Infrastructure suite to make this pass. That would slow every gate
for every goal.

The rest of the change is in good shape: focused evidence on `AcceptanceCohortWorkflowTests` was 32 total,
32 executed, 32 passed at this candidate, and the Reviewer cleared all four previously open findings.
