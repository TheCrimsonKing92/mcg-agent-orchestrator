# Unchanged-candidate verdict reinstatement

Execution owner: Acceptance/conductor. The subscription Developer is instructed to defer .NET
build and test execution. No behavioral RED or GREEN receipt has been produced in this worker round.

Primary regression: `ConductorDriverTestsUnchangedCandidateReinstatement.InheritedResetAfterFailedDeveloperRetryReinstatesPassingTesterAndReviewer`.
Its fixture asserts that both verifications were initially preserved by the Developer retry, then
reset by the failed/no-commit reconciliation, before closing Developer and advancing the driver.

Negative control: in `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.UnchangedCandidate.cs`,
temporarily replace the local `kernel` assignment in `TryRefuseUnchangedCandidateDispatch` with
`AgentOrchestratorKernel? kernel = null;`. This disables reinstatement without changing the original
hold path. Run only the primary regression through the repository managed test runner. Expected RED:
the assertion that `result.Outcome` is `ConductorAdvanceOutcome.Executed` instead observes
`ConductorAdvanceOutcome.Held` (with the prior passing Tester verdict). Retain the actual assertion
output; a compile failure or fixture failure is not the negative-control receipt.

Restore the production assignment and run both new test classes plus all seven existing compatibility
classes named in the objective. Record executed counts, exit codes and receipt paths here through the
Acceptance evidence owner. The compatibility classes' assertions remain unchanged.

For the eligibility controls, independently remove the corresponding guard from
`UnchangedCandidateRule.EvaluateReinstatement` and run its focused control: latest inherited provenance
(`DirectRetryAfterInheritedResetCannotReinstate`), role (`OtherRolesRemainIneligible`), identical candidate
(the `Evaluate` identity comparison, `AnyChangedCandidateComponentPreventsReinstatement`), new input
(the `Evaluate` `HasNewInput` call, `AcceptedFeedbackIsNewInputAndPreventsReinstatement`), same task
(`PriorVerdictFromAnotherTaskCannotBeReinstated`), criteria hash
(`ChangedEffectiveCriteriaAfterVerdictsBlocksReinstatement`), and passing verdict
(`NeedsWorkReviewerVerdictStaysHeldAfterInheritedReset`). Run each arm separately and restore its
guard before the next arm; expect the control's null/held assertion to fail. These executions remain
deferred, and no static inspection is claimed as RED/GREEN evidence.
