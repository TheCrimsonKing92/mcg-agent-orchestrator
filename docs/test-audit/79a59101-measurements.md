# Test-audit pass 1c: operator-supplied lane measurements

Operator-supplied input for goal `79a59101`. These figures were produced by the operator from
the acceptance-gate receipt corpus, which no worker role can reach. They are given here as
tracked data so the goal can cite them without reading any runtime store.

## Method

Every `*goal-worktree-cleanup*.trx` under the acceptance-gate attempt store, parsing each
result's `testName` and `duration`, aggregated per fully qualified test name. Lane totals are
computed by summing result durations grouped by attempt and lane, then taking the largest lane
per attempt as that attempt's critical path.

Corpus: 549 acceptance attempts.

## Lane context

    mean critical path across all lanes      475.5 s
    goal-worktree-cleanup measured mean      404.0 s
    goal-worktree-cleanup declared estimate  274.6 s   (config/acceptance-manifest.json)

`goal-worktree-cleanup` is the largest recurring lane and therefore sets the gate floor. It
shares the exclusive resource key `xunit:GoalWorktreeCleanupHooks` with `Goal lifecycle
commands` (194.3 s measured), so the two serialise and the chain is roughly 598 s.

## Tests exceeding 10 s mean in the lane

All four are in `AcceptanceCohortWorkflowTests`.

| Test | Runs | Mean (s) |
| --- | --- | --- |
| `RetryableLandingHold_PreservesPassingReceipt_AndLaterRelandsIdenticalState` | 69 | 16.29 |
| `ProductionRed_OneFailedMember_RequeuesGreenPeer` | 69 | 11.25 |
| `ProductionBatch_SelectsRunsPersistsLandsAndCleansOneSharedCohort` | 69 | 10.62 |
| `ProductionRed_RunsBothPartitions_AndClassifiesInteraction` | 69 | 10.20 |

## Next tier, 5 to 10 s mean, same class

| Test | Runs | Mean (s) |
| --- | --- | --- |
| `PersistedInfrastructureFailure_UsesBoundedHoldThenOrdinaryFallback` | 69 | 8.76 |
| `CachedPassWithoutEvidence_InvalidatesBeforeLanding` | 69 | 8.49 |
| `PostGateBranchMove_HoldsMembersForFreshProjection` | 69 | 7.87 |
| `StaleMaterialization_PersistsOutcomeAndUsesOrdinaryFallback` | 69 | 6.37 |
| `DisposableWorkspaces_UnderNestedGitWorktree_UseShortTokensAndCleanUp` | 69 | 5.80 |
| `ConstructorRecovery_ReplaysBothMemberLandingEffects` | 51 | 5.76 |
| `MainCasFailure_RestoresIntegrationRef_AndLandsNeitherMember` | 69 | 5.64 |
| `ConstructorRecovery_DefersEntireCohortUntilReplacementLeaseIsReleased` | 18 | 5.01 |

## Class-level shape

Summing per-test means across all `AcceptanceCohortWorkflowTests` entries gives roughly **140 s
per attempt** against the lane's 404 s measured mean — about **35 percent of the lane**.

The other five classes in the lane are comparatively cheap. The largest non-cohort entries are
`GoalWorktreeTestsRemoveCleanup.RemoveSupersededTerminal_ChangedTip_KeepsBranch` at 2.64 s over
145 runs and `GoalWorktreeTestsRemoveCleanup.Keyed_goal_replay_keeps_one_workspace_and_clean_repository`
at 2.56 s over 10 runs. Several `GoalWorktreeTestsAcceptanceLanding` entries measure 0.00 s.

Any material reduction to this lane must come from `AcceptanceCohortWorkflowTests`.

## Caveat on the corpus

These receipts span multiple manifest versions and lane membership has changed over time — goal
`1a270cd6` moved classes between lanes on 2026-08-15. The per-test figures are keyed on test
name and are sound. The tables above are restricted to the six classes currently in the
`Goal worktree cleanup` lane filter; do not treat the raw receipt set as present membership.
