# Subscription verification terminal contracts

Owning invariant: a dispatched subscription Reviewer or Tester can complete only
from its own captured terminal role contract. A valid contract can still report a
blocker, inconclusive verification, or an Acceptance-owned obligation; its existing
outcome owner decides those reports. Process exit, commits and receipts support a
contract and cannot replace one.

The assessment extends Core's existing WorkerResultBlockers normalization,
role-field requirements, finding parser, criterion parser and typed test statuses.
Execution calls the same assessment. Other callers retain their lenient parser.
Verification records and snapshots carry the recording source and originating
dispatch timestamp. Manual/API recording remains explicit; historical subscription
records without sufficient attempt identity fail closed at consumption.

## Promotion inventory and deferred verification

| Promoter or source | Guard / evidence owner | Regression or preserved control |
| --- | --- | --- |
| Exit-zero introductory output | Classifier before success rules, real runner recording | SubscriptionVerificationCompletionTests.ExitZeroIntroductionDoesNotCompleteOrAdmitAcceptance; WorkerDispatchTestsSubscriptionTerminalContract.BackgroundDispatchRunnerSubscriptionReviewerIntroductionRequiresTerminalContract |
| Committed result, copied candidate, pass-text, deferral and nonempty-output fallbacks | Common classifier assessment before all promoters, including authored rejection recovery | SubscriptionVerificationTerminalContractTests.IntroductionCannotBorrowCommitPassReceiptOrDeferral; CopiedCandidateCommitDoesNotReplaceWorkerTerminalEvidence |
| Loose WORKER_RESULT files and retained stderr contracts | Runner requires captured complete stdout before reconciliation, committing or completion | SubscriptionVerificationCompletionTests.StderrAndLooseContractsCannotRepairCurrentStdout; WrapperFailureCannotReconcileFromRetainedContract |
| Child-zero/wrapper-failure and hung-wrapper commit/pass rescue | Runner's completion/reconciliation and rescue checks require the same assessment | WrapperFailureCannotReconcileFromRetainedContract; existing WorkerDispatchCompletionClassifierTests wrapper decision table |
| Open-ended or opener-free blocks; malformed role fields | Final closed block, role-specific required fields and existing typed parsers | FinalUnfinishedBlockCannotBorrowEarlierContract; MalformedReviewerFieldsCannotBePromoted; UnavailableAuthorityCannotBorrowRetainedContract |
| Acceptance-owned success override | Kernel only admits a valid report to finding merge or deferred-evidence override | Existing structured finding/Acceptance binding tests; ValidTesterBlockerRemainsAReportedFailure |
| Terminal-goal completion, verified-success persistence and answered-input completion | Classifier first, incoming success claims cleared, direct completion guarded | Runner partial-output test; FalsePersistedVerdictCannotPassRestoredGatesOrDuplicateReplay |
| Snapshot restore, completed-process suppression and both acceptance consumers | Consumers reassess captured evidence independently of stored success flags | FalsePersistedVerdictCannotPassRestoredGatesOrDuplicateReplay; CapturedContractSurvivesSnapshotWithoutLosingSourceAndAttempt |
| Stale completion and current review-context attachment | Timestamp/artifact guard before runner application and kernel context attachment | OldAttemptCannotAcquireCurrentCandidateOrVerdict; DelayedOutcomeCannotReplaceNewProcessWithoutContextTelemetry |
| Convergence ledger replay and finding supersession | Invalid stored subscription reports excluded; recovery cannot turn incomplete reports into Developer findings | InheritedCandidateReuseRequiresValidOriginalSubscriptionContract; existing finding-currency and review-recovery tests |
| Intentional unchanged-candidate reinstatement without a current dispatch | Reassess the original stored attempt, then retain existing identity/input/criteria checks | InheritedCandidateReuseRequiresValidOriginalSubscriptionContract; UnchangedCandidateReinstatementRuleTests |
| Tester pass, not-run and inconclusive | Evidence-bearing pass; not-run stays inconclusive; explicit inconclusive keeps its owner | TesterStatusKeepsItsMeaningEvenWithCommit; TesterPassWithoutEvidenceIsMalformed |
| Valid no-change Reviewer and evidence-reference Tester | Required role fields; no file-change requirement added | ValidNoChangeContractCompletesWithReceiptReference |
| API/manual/operator verification | Explicit recording source bypasses subscription worker framing | ManualVerificationAfterSubscriptionAttemptRemainsManual; NonSubscriptionDispatchRetainsItsExistingContract; OperatorIntentAdjudicationTests |

Missing, malformed, unreadable and stale contracts retain distinct typed assessment
statuses and use one incomplete outcome. Kernel owns two automatic same-role report
repairs; exhaustion requires operator action. Recorded interruption retains its
three-retry owner. Alternating missing/malformed reports does not restart repair
counts; interruption and inconclusive rounds do not reset that count either.
Incomplete reporting also participates in the existing transient counter.

Explicit provider cancellation lines are recognized only as complete provider
diagnostic lines, never quoted worker prose. Existing failed-turn records are
recognized in captured stderr for these attempts, including a terminal failure
after an earlier completed turn. Cancellation diagnostics survive snapshot replay.
The supplied native Grok session's full cancellation envelope is not packaged:
these fixtures do not claim to reproduce or parse that exact native envelope.
Its partial stdout is rejected regardless, and complete stderr is retained.
Transport-specific cancellation vocabulary remains provider-usability follow-up
d8ff610b90644be487b00e5b42f8dbd5; no permission changes or paid start are required.

## Scope accounting under 107b7e930ec941dbbb364cf910161623

Developer retains committed-change, worker-build, verified-no-change and
deferred-no-change semantics. Planner retains its durable plan contract and
Researcher its durable research artifact contract. Ideation remains read-only.
Their shared generic committed/pass/nonempty-output fallbacks are not tightened
in this slice. Follow-up for umbrella 107b7e930ec941dbbb364cf910161623: inventory
those roles' terminal/output obligations against the shared fallbacks and add
role-specific negative controls before changing their completion semantics.
OtherRolesDoNotAcquireVerificationContractRequirements pins this boundary.

## Execution ownership

Developer builds the changed production and test projects only. Acceptance must
execute the listed tests with nonzero selection and inspect reached-path checks.
Run existing TesterInconclusiveRoundInputsReaderTests, UnchangedCandidateReinstatementRuleTests,
WorkerDispatchCompletionClassifierTests, ConductorDriverTestsContractRepairBounds
and OperatorIntentAdjudicationTests as positive controls. Existing authoritative
Acceptance-obligation finding tests must remain green.

The existing introduction regression, now in WorkerDispatchTestsSubscriptionTerminalContract,
uses APIs present at the base candidate and can demonstrate a behavioral
failure there. For the new tests that use the added typed record fields, retain
that compilation scaffolding while disabling the production assessment guards
for a behavioral negative control; a compile failure is not such evidence.

Final exact-candidate cross-family review and full normal acceptance belong to the
operator. No worker execution or independent-review receipt is claimed here.

## Focused candidate-red repair

Supplied receipt `finding-evidence-f9289a6fe38b0676d77dc160` for candidate
`2db34cb36bb2945589ae66e77bff0adba86b7115` names the stale-process fixture's
"already running; refresh or cancel it before retrying" exception and legacy
Tester assertions expecting Completed or Failed while the actual status was Assigned.
The stale-process fixture now records cancellation before retry and starts its
replacement after the recorded retry timestamp. It verifies that the delayed
report is valid before proving it cannot replace the new process without context telemetry.

The existing status parser accepts evidence-bearing Tester passes separated by a
hyphen, en dash, em dash or colon. Terminal assessment retains those forms while
rejecting empty, `none` and placeholder evidence. Positive wrapper/native controls
retain their em dash reports. TRX/count positive fixtures now put `pass` first;
dirty-worktree and ignored-artifact fixtures supply valid contracts so they still
exercise their original guard. Missing, loose-file and malformed-report controls
assert same-role repair, retained exit/child diagnostics, no verified-success
record, and closed verification and acceptance gates. Empty reports retain the
shared transient counter but use the contract-repair outcome for subscription
verification roles; no launch cause is inferred from an unknown child exit.

Execution remains deferred to Acceptance: SubscriptionVerificationCompletionTests,
SubscriptionVerificationTerminalContractTests and WorkerDispatchTestsWorkerResultClassification.
Developer build evidence does not establish that these behavioral tests passed.

## Structural pre-check repair

The runner delegates bounded apparatus-hold observation and escalation to
DispatchApparatusHoldRecorder. The kernel delegates authoritative finding deferral
and dispatch-context attachment to VerificationFindingEvidencePolicy. Both retain
the original bodies and recording order; completion classification remains with
the existing classifier and kernel. The introduction regression moves intact into
WorkerDispatchTestsSubscriptionTerminalContract with its original EnvMutation collection.
All three affected source-size ceilings
are lowered to the measured sizes in SourceSizeRatchet.

## Core dispatch fixture and ownership repair

Retained native attempt `c5b71650-0-20261013011639132-65942691d33f444792fe16f1767189f9`
on candidate `7c6223af180a4fe5f22f28742528bbbe43add7b8` executed 99
DispatchExecutionTests cases: 59 passed and 40 failed. Its Core TRX is
`.orchestrator/pre-review-evidence-attempts/c5b71650e17d4863b2e5b41bbce882d5/c5b71650-pre-review-20261013011645676-bfb53c2be6124dcbbb85f8cecd84a8cf-candidate.reviewer-focused-evidence-core-tests-fullyqualifiedname-dispatchexecutiontests.trx`.
The owning Core sources and fixtures are unchanged between that candidate and
the incoming `e05d9c62ea9edf2ab5035c20fd63484076efe92f` candidate.

The retained authoritative-operator-criterion case reports "Expected: Completed;
Actual: Assigned". Its verification omitted DispatchStartedAt, so attempt
assessment rejected it before criterion ownership could be evaluated. Positive
subscription fixtures now supply that timestamp; WorkerResultStdout includes
the required criteria_verdicts field. The incomplete-criteria coverage test uses
an empty parsed array to reach its original missing-criterion gate. Three record
equality controls now explicitly expect the dispatch provenance stamp.

Raw blocking findings and not-met criterion reports are structurally complete
contracts. The parser must leave their semantic disposition to the existing
kernel convergence, waiver and ownership gates. Otherwise an authoritative
waiver or Acceptance-owned finding becomes format repair before its owner is
consulted. ReviewerUnmetCriterionUsesAuthoritativeWaiver pins both waived and
unwaived outcomes. Existing blocking-finding, operator-waiver and retry-cap
assertions remain; the Acceptance-owned finding theory now uses subscription
dispatches and asserts valid framing before proving pending ownership remains.
The unknown-verdict and malformed-findings controls retain typed malformed
rejection, no verified success, same-role repair and closed verification gates.

Execution remains deferred. Request whole-class Core selection
FullyQualifiedName~DispatchExecutionTests and Infrastructure selection
FullyQualifiedName~WorkerDispatchTestsWorkerResultClassification, plus
SubscriptionVerificationTerminalContractTests and
SubscriptionVerificationCompletionTests for the changed assessment seam.
Build success is compilation evidence only; final cross-family candidate review
and acceptance execution remain operator/Acceptance-owned.

## Two remaining Core fixture repairs

Supplied receipt `finding-evidence-2db280ba8ef9f42334f7497e` on candidate
`455fbedd70260e309254c7e0c97a4502c0dca3a4` reports failures in
`DispatchExecutionTests.GetOpenAdvisoryReviewFindingsDoesNotFallBackAfterLatestReviewFails`
and `RecordDispatchExecutionResultReviewerMergePreservesRoundsWithEqualTimestamps`.
The first fixture now supplies malformed findings JSON: empty severity is deliberately
normalized to Blocking by FindingSeverityJsonConverter, so it is a valid blocking
report rather than format repair. An assessment precondition pins the malformed input.

The equal-completion-time fixture advances its injected clock before recording
the first completion and retains that dispatch's original start time. Its second
dispatch therefore has a distinct attempt timestamp without changing either
completion timestamp. Previously TaskSpec.RecordDispatch advanced the second
attempt by one tick, making its reported start stale and leaving it Running.
Assertions now require both valid stored contracts, both retained rounds, distinct
attempt identities, and the existing omitted-open-finding violation with F-1 retained.

Execution of both named methods and the whole DispatchExecutionTests class remains
deferred to the conductor. No production completion or parser semantics change here.

## Full Core gate retry repair

Retained acceptance receipt
`c5b71650-0-20261013053605667-063eb11edd7c44e781a19bcdead12c95.core-tests.trx`
for incoming candidate `7c6beb68f5f6793d4e917e8df0360992ed751c1c`
executed 3,280 tests: 3,271 passed and nine failed. The receipt lives under
`.orchestrator/acceptance-gate-attempts/c5b71650e17d4863b2e5b41bbce882d5/`.
ReviewerAttestationCriterionNumberTests reported "Expected: Completed; Actual: Assigned"
or "Expected: Failed; Actual: Assigned". These positive fixtures omitted
DispatchStartedAt; they now bind the actual attempt and assert valid terminal framing
before checking criterion registration and informational extra attestations.
Two TaskProcessTests record-equality failures differed only in EvidenceSource;
their expected records now include the dispatch provenance stamp.

The missing-contract branch bypassed two existing fault details. Authentication
still classified correctly, but CliProviderRefusalClassificationTests could not find
the provider-specific remediation. That branch now uses the same authentication
summary builder as the ordinary authentication branch. Explicit conductor detachment
was intercepted before its bounded three-retry interruption owner, leaving the
DetachedRoundWithoutWorkerResultRecordingTests retry timestamp unset and its
detached-round count zero. A current-attempt Missing assessment plus recorded
graceful detachment now reuses that existing interruption outcome and retry owner.
Malformed, unreadable and stale reports remain on the contract-repair path;
provider fault evidence retains precedence. Neither outcome can promote success.

Detached fixtures now bind their actual process start, exercise Reviewer and Tester,
and pin the interruption backoff, retained non-success verdict and fourth-round
exhaustion. DetachmentCannotReplaceMalformedOrStaleContract pins the distinct
assessment and contract-repair owner. The classifier's detached-prose control now
expects interruption for both verification roles, while its ordinary/running-process
controls still require contract repair and valid detached contracts still pass.

Execution remains deferred to Acceptance: ReviewerAttestationCriterionNumberTests,
DetachedRoundWithoutWorkerResultRecordingTests, TaskProcessTests,
CliProviderRefusalClassificationTests, DispatchOutcomeClassifyTestsDetachedWithoutWorkerResult,
SubscriptionVerificationTerminalContractTests and SubscriptionVerificationCompletionTests.
Developer build evidence establishes compilation only. Exact-candidate cross-family
review and normal acceptance remain operator-owned.
