# Candidate-bound review currency

## Owning invariant

A failed Tester or Reviewer verdict remains actionable until a completed, successful Developer retry proves that it repaired the verdict's reviewed candidate and produced a different candidate. Proof requires the verification's `ReviewedCommit`, the Developer dispatch `BaseCommit` and `ResultCommit`, the retry checkpoint (`LatestRetryAt`), and successful Developer completion. A later timestamp alone is not proof. Unknown identities, failed Developer work, and verified no-change rounds preserve the verdict and its obligations.

## Decision path

Before this change, `VerifyingFindingCurrency` removed historical findings after any later committed Developer verification, while `ConductorDriver.BuildVerifyingFindingTrigger` independently treated the failed Reviewer's needs-work verdict as current. The conductor could therefore choose a Developer retry and then ask the convergence builder to build it from an empty projected ledger, producing `ERR_REVIEW_NEEDS_WORK_WITHOUT_OPEN_FINDINGS`.

After this change, `VerifyingFindingCurrency.Classify` owns one disposition for both finding projection and conductor retry routing. Kernel completion reconciliation and conductor tick normalization use its `SupersededByCompletedRepair` result to clear only the stale failed task state. The historical verification and findings remain in `VerificationHistory`, so the re-admitted Tester and fresh Reviewer retain the prior obligations as review context. Current malformed or genuinely empty needs-work results still use the existing bounded contract-repair/terminal path.

## Remaining scope

The historical path that allowed a failed Reviewer to survive the earlier retry-time downstream invalidation is still unproven; normalization intentionally repairs that persisted boundary without claiming a cause. Backlog `ed49451e02fa40fc820bea162f689a1e` remains open for the atomic recovery bridge, generalized no-change repair receipts, clarification/unpark semantics, and the other requirements outside this slice. Full acceptance and independent Anthropic review remain downstream gates.
