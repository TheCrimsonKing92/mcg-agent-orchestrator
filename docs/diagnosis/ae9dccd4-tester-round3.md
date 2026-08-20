# Tester round 3 — the new criteria tests exist and FAIL. Dispatch discarded on format again.

Preserved because the dispatch was rejected with `reason=verification-pattern-unmatched` for reporting
`tests: fail`. The content is correct.

## Evidence

Focused run at HEAD `177a232e`:

    AcceptanceCohortWorkflowTests   total 31   failed 3   passed 28

Failing:

    ProductionMergeTrain_OneGateAttemptLandsThreeMembers
    ProductionMergeTrain_RedNewest...
    (third name truncated in the captured line)

## What this means

Findings 4 and 5 from the first Tester round said criteria 1 and 2 had NO tests — nothing called
`RunMergeTrain` at all. The Developer added them. They now exist and they fail.

That is progress, not regression: the criteria are finally being exercised, and the implementation does not
yet satisfy them. Criterion 1 is "one gate attempt, three landings"; criterion 2 is the red-newest
drop-and-eject path. Both are still open work, now with a failing test naming each.

Do NOT make these pass by weakening them. They encode the acceptance criteria of this goal.

## Standing findings

The other blocking items from `docs/diagnosis/ae9dccd4-tester-findings.md` remain relevant. As of round 2
the Tester considered these still unproven rather than fixed:

- `RunMergeTrain` now catches `InvalidOperationException` and maps stale to `StaleBinding`, but that catch
  is unproven until a test drives a stale materialization and asserts the typed fallback. A `catch` that has
  never been exercised on a loop-crash path is trusted on faith.
- `RecoverPreparedLandings` and `RecoverMergeTrainLandingEffects` now exist, but `FinalizeLanding` still
  synthesizes in-memory coverage and leaves state prepared until completion — the persistence half is not
  done.
