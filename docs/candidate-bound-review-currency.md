# Candidate-bound review currency

## Owning invariant

A failed Tester or Reviewer verdict remains actionable until either that verifier is explicitly retried for a replacement result or a completed, successful Developer retry proves committed output and produces a result candidate different from the verdict's reviewed candidate. A verifier retry clears only the current pointer; its historical findings remain durable. Developer repair proof requires the verification's `ReviewedCommit`, the Developer dispatch `BaseCommit` and `ResultCommit`, the retry checkpoint (`LatestRetryAt`), and successful Developer completion. The dispatch base may be an intervening integration candidate; it must still differ from the result to prove committed output. A later timestamp alone is not proof. Unknown identities, failed Developer work, and verified no-change rounds preserve the verdict and its obligations.

## Decision path

Before this change, `VerifyingFindingCurrency` removed historical findings after any later committed Developer verification, while `ConductorDriver.BuildVerifyingFindingTrigger` independently treated the failed Reviewer's needs-work verdict as current. The conductor could therefore choose a Developer retry and then ask the convergence builder to build it from an empty projected ledger, producing `ERR_REVIEW_NEEDS_WORK_WITHOUT_OPEN_FINDINGS`.

After this change, `VerifyingFindingCurrency.Evaluate` owns one disposition and the matched repair identity for finding projection, conductor retry routing, and kernel reconciliation. A retry-cleared verifier is explicitly non-current, and both completed-repair and later-verification dispositions re-admit a retained failed verifier. Reconciliation uses the matched repair rather than whichever Developer task happens to be iterated, preserves parked/waiting goals for their explicit operator transition, and never clears a verifier's live process; process refresh or cancellation retains ownership of that transition. The historical verification and findings remain in `VerificationHistory`, so the re-admitted Tester and fresh Reviewer retain the prior obligations as review context. Current malformed or genuinely empty needs-work results still use the existing bounded contract-repair/terminal path.

## Remaining scope

The historical path that allowed a failed Reviewer to survive the earlier retry-time downstream invalidation is still unproven; normalization intentionally repairs that persisted boundary without claiming a cause. Backlog `ed49451e02fa40fc820bea162f689a1e` remains open for the atomic recovery bridge, generalized no-change repair receipts, clarification/unpark semantics, and the other requirements outside this slice. Full acceptance and independent Anthropic review remain downstream gates.
