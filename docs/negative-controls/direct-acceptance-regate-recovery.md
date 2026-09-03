# Direct acceptance re-gate recovery negative controls

This critical operator intervention has no orchestrator goal prefix because the defect blocked the
orchestrator's own acceptance recovery path.

## Stale failed candidate pair

- Mutation: added the regression test before moving superseded-failure normalization ahead of the
  `AcceptanceFailed` status guard.
- RED command: `Invoke-TestSummary.ps1` with
  `Name~AcceptanceFailed_OldCandidatePair_RunsFreshGate`.
- RED receipt: 1 executed, 1 failed because direct acceptance printed `acceptance: not accepted` and
  never printed `superseded failure is historical`.
- GREEN receipt: the five-test focused lifecycle set executed 5 and passed 5 after the fix.

## SQLite merge status laundering

- Mutation: added the regression test before excluding `AcceptanceFailed` from stored verification
  normalization.
- RED command: `Invoke-TestSummary.ps1` with
  `Name~TickMerge_AcceptanceFailedStoreState_RemainsFailed`.
- RED receipt: 1 executed, 1 failed because the persisted status was `Verified` instead of
  `AcceptanceFailed`.
- GREEN receipt: the full `ConductorBatchLoopVerificationReconcileTests` class executed 7 and passed 7
  after the fix.

The safety-side control `AcceptanceFailed_CurrentCandidatePair_StaysBlocked` proves that an unchanged
candidate pair still invokes zero acceptance verifiers and remains `AcceptanceFailed`.

## Persistent preflight replay

- RED command: `Invoke-TestSummary.ps1` with
  `Name~AcceptanceFailed_StalePairWithCancelledTask_PersistsRecovery`.
- RED receipt: 1 executed, 1 failed because persistent preflight aborted before the verifier
  (`RunCount` was 0 rather than 1).
- GREEN receipt: 1 executed and passed after the preflight request carried current branch/main SHAs
  into the transactional replay and revalidated the same transition there.
