# Behavioral RED/GREEN negative controls for bbee6e09

Executed by the operator on 2026-10-02 against candidate `463399ab486dc9d14c57f52d0f3da260c1650cdf`
(head of `goal/bbee6e09`). Each arm ran in its own temporary detached worktree outside the goal worktree,
both at that commit; the RED worktree received the one mutation below and the GREEN worktree was left
unchanged. Both arms used the same runner command and test project, so they differ only by the production
source below. Receipts are copied to `.orchestrator/operator-evidence/bbee6e09/`.

This record answers the Tester's RED-proof blocker for criterion 1. The conductor's `revert-src` arm could
not settle it: reverting all of the goal's source removes `TaskSpec.LatestRetryInherited` and the
two-argument `ConductorDriver.OverrideCandidateIdentityResolverForTests`, which the test class uses, so the
test project cannot compile against reverted source. The mutation here keeps every new type and member and
disables only reinstatement, so the inherited-reset Tester or Reviewer is held exactly as on main. It is the
negative control the Developer's earlier plan for this record named.

Runner command (from `docs/operator-runbook.md`), run from each temporary worktree root:
`scripts/Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter "FullyQualifiedName~ConductorDriverTestsUnchangedCandidateReinstatement"`

## Inherited-reset reinstatement

- Test class: `ConductorDriverTestsUnchangedCandidateReinstatement`
  (`tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsUnchangedCandidateReinstatement.cs`),
  kept at the candidate version in both arms.
- Mutation (RED), disabling reinstatement in `TryRefuseUnchangedCandidateDispatch`:
  - `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.UnchangedCandidate.cs`
    - before: `var kernel = _unchangedCandidateReinstatementKernel ?? _cohortKernel ?? _conductorTickKernel;`
    - after: the same line followed by `kernel = null;`

  With no kernel the reinstatement loop is skipped and the existing `UnchangedCandidateRule.Evaluate` hold
  path runs unchanged. No test-compatibility shim was needed: the mutated source builds, and the test file
  compiles unchanged.

- RED (mutated source): build succeeded; total 8, failed 4, passed 4. Every failure is an assertion, not a
  compile or runner error:
  - `InheritedResetAfterFailedDeveloperRetryReinstatesPassingTesterAndReviewer(withSpec: True)` and
    `(withSpec: False)` failed at line 21 with `Assert.IsType() Failure: Value is not the exact type`
    (expected `ConductorAdvanceOutcome.Executed`; the driver held instead).
  - `RepeatedUpstreamRetryKeepsOriginalVerdictProvenance` failed at line 105 with
    `Assert.IsType() Failure: Value is not the exact type` (expected `Executed` on the first upstream retry
    round; the driver held).
  - `NeedsWorkReviewerVerdictStaysHeldAfterInheritedReset` failed at line 148 in `AssertReinstated` with
    `Assert.Equal() Failure: Values differ` (expected the passing Tester `Completed`; it stayed reset). This
    fact reinstates the passing Tester before asserting the needs-work Reviewer stays held, so it depends on
    reinstatement too.
  - Passed under RED, as expected, because they assert hold or dispatch behavior that the mutation leaves
    alone: `ChangedEffectiveCriteriaAfterVerdictsBlocksReinstatement`,
    `DirectUnchangedContextRepeatTesterRetryStaysHeld`, and both cases of
    `ChangedCandidateOrNewInputKeepsDispatchBehavior`.
  Receipt: `.orchestrator/operator-evidence/bbee6e09/reinstate-red.trx`
- GREEN at candidate (unmutated): build succeeded; total 8, passed 8, failed 0.
  Receipt: `.orchestrator/operator-evidence/bbee6e09/reinstate-green.trx`

The arms disagree on every reinstatement fact, and GREEN does not reproduce any RED message. The mutation
existed only in the temporary worktree; the committed candidate is unchanged apart from this record.
