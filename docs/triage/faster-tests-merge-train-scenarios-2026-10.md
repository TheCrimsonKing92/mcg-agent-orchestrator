# Merge-train and cohort scenario setup costs

## Measurement ownership

The before column is the operator's mean **sum of per-test TRX durations per full
gate**, in seconds, over main gates `5d0ca6d8`, `cc2c0543`, and `359287b7`
(2026-10-27/28). Tests per gate is the operator's corresponding mean. These are
supplied measurements, not new worker runs. The operator did not supply the main
commit or host identifier for each gate. Source inspection started at
`617daadddcb038cce07f0074e2e1b1c1f1d9fdcf`.

Workers do not execute tests, the managed runner, or timing harnesses. The Tester
must request the ten changed concrete classes below, plus
`MergeTrainDeferralFixtureEquivalenceTests`, through Acceptance. Acceptance uses
the managed runner behind `scripts/Invoke-TestSummary.ps1 -Target
tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj`
with those class selectors. This is an execution handoff, not a receipt.

For the after column, the Tester must record the receipt ID, candidate revision,
host, exact invocation/selectors and settings, and sum its per-test durations per
class. Run on the operator's baseline host. Each fixture is constructed empty;
template preparation happens lazily inside the first consuming test, under that
test's capacity lease, so preparation is included in the after test-seconds.
Fixture disposal happens after the class. Include the new real-path contract
test in BackgroundMergeTrainGateTests' after total, and disclose changed test
counts alongside the historical means. These full-gate means are not an
exact-candidate focused-run baseline; do not describe the comparison as a
controlled performance experiment.

## Before and after

`Pending` means no conductor receipt exists yet; it is not zero or a performance
claim. All dominant-step findings below are source-inspection hypotheses. The
Tester must confirm or correct them when entering after values and receipt IDs.
If a class regresses, investigate the measured case before attributing a cause;
overlapping execution alone does not establish contention.

| Class | Before seconds/gate | Tests/gate | After seconds | Receipt / confirmation |
|---|---:|---:|---:|---|
| MergeTrainRedAttributionReplayScenarioTests | 201.9 | 8.7 | Pending | Acceptance / Tester pending |
| AcceptanceCohortWorkflowTestsTrainRedAttribution | 179.7 | 14.3 | Pending | Acceptance / Tester pending |
| PassedMergeTrainReceiptHoldTests | 158.4 | 11 | Pending | Acceptance / Tester pending |
| AcceptanceCohortWorkflowTestsMergeTrain | 156.2 | 6.7 | Pending | Acceptance / Tester pending |
| MergeTrainDeferralFixtureEquivalenceTests | 149.5 | 4 | — | Unchanged; independent-arm rerun required |
| AcceptanceCohortWorkflowTestsLandingAndReceipts | 142.1 | 4.7 | Pending | Acceptance / Tester pending |
| AcceptanceCohortWorkflowTestsGroupedGateAttempts | 142.1 | 4.3 | Pending | Acceptance / Tester pending |
| BackgroundMergeTrainGateTests | 141.6 | 5 | Pending | Acceptance / Tester pending |
| AcceptanceCohortWorkflowTestsTrainImplicatedExclusion | 139.9 | 3.7 | Pending | Acceptance / Tester pending |
| AcceptanceCohortWorkflowTestsFollowerGateStart | 137.6 | 3.7 | Pending | Acceptance / Tester pending |
| MergeTrainBisectSlotLossTests | 137.6 | 7 | Pending | Acceptance / Tester pending |
| AcceptanceCohortWorkflowTestsWorkspacePreparation | 136.2 | 4 | — | Unchanged; real setup is asserted behavior |

## Source inspection

Line references below describe the edited candidate. Paths are relative to
`tests/Mcg.AgentOrchestrator.Infrastructure.Tests/` unless stated otherwise.
The common original cost is `AcceptanceCohortWorkflowTests.cs:157-170`:
`CreateWorktreeCandidate` calls the real `GoalWorktrees.Ensure` and then git add,
commit, and rev-parse. `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.Create.cs:9`
owns Ensure's additional branch, worktree and cleanliness probes. The base seed
repository and migrated database already copy templates at
`GoalWorktreeTests.cs:305-312`; store migration is therefore not a newly removed
per-case cost. Production projection, train/cohort materialization, rebases,
landing and cleanup still execute per test.

| Class / inspected principal scenario | Suspected dominant step and evidence | Change |
|---|---|---|
| MergeTrainRedAttributionReplayScenarioTests / IncidentReplay_EjectsAndFailsOnlyReachCulprit | Repository/candidate setup and git launches: WithTrain at :298, PrepareReachBase at :372 and PrepareReachMember immediately after it populate source reach and make multiple commits per member. SequenceAcceptanceVerifier at WithTrain executes no real gate. | Copy each repeated count/shared recipe; ownership-only singleton stays fresh. |
| AcceptanceCohortWorkflowTestsTrainRedAttribution / OwnedTimeout_SavesFailedReceiptAndImplicatesItsMember | Candidate setup and git launches: WithTrain at :371 seeds unchanged/guard/shared sources, then two or three member worktrees. Results are supplied by SequenceAcceptanceVerifier. | One template per member count; attribution, failure parsing and RunMergeTrain remain real. |
| PassedMergeTrainReceiptHoldTests / PendingHumanInputHoldsReceiptOnlyReplayUntilKernelAnswer | Candidate setup plus real train materialization: CreatePassedTrain at :476 amends the seed, builds three branches and optionally calls CreateMergeTrainWorkspace. That materialization and its assertions are behavior required for receipt replay. | Copy candidate portion only; retain per-test materialization, receipt stores and assertions. |
| AcceptanceCohortWorkflowTestsMergeTrain / ExactTestedMergeTrainCommit_LandsThreeGoalsWithPerGoalCoverage | Candidate setup plus production train git work: theory at :61 repeats the manifest, source-size authority and three candidate recipe across eleven rows; SequenceAcceptanceVerifier supplies the gate. | Copy only that repeated recipe; other tests retain their distinct construction and all landing paths remain real. |
| MergeTrainDeferralFixtureEquivalenceTests / Deferral_SharedAndFreshFixtures_ProduceEquivalentResults | Fresh-arm repository/candidate setup plus production train execution: :14-16 invoke both arms; MergeTrainDeferralSharedFixture.cs:116-130 rebuilds fresh. Reset_FiveOrderedScenarios_RestoreStateWithoutRebuilding at :40 executes five scenarios. Slot delays are completed tasks, not paced waits. | Unchanged by design; both busyRounds 2 and 3 retain an independently built fresh arm and all five scenarios remain. |
| AcceptanceCohortWorkflowTestsLandingAndReceipts / RetryableLandingHold_PreservesPassingReceipt_AndLaterRelandsIdenticalState | Candidate and real cohort/landing git work: theory at :727 repeats two candidates, then materializes a cohort and seeds an exact receipt. FakeAcceptanceVerifier fails if a forbidden rerun occurs. Store-only tests at :18 have no candidate setup. | Copy only the four-row retry recipe; retain real receipt creation/replay, stores, and other scenarios. |
| AcceptanceCohortWorkflowTestsGroupedGateAttempts / AdmissionWritesOwnedAttemptBeforeReturningWithoutRunningVerifier | Candidate setup and git launches: CreateReadyMembers at :399 builds two/three candidates. Process launch is injected and the verifier is scripted; no actual gate child is removed. | One template per member count; registry, launch ownership and receipt replay remain per-test. |
| BackgroundMergeTrainGateTests / PlannerConflict_EscalatesWhileAnotherTrainOccupiesAcceptanceWidth | Candidate setup and git launches: CreateReadyTrain at :458 repeats three worktrees; BackgroundGateStartHarness captures launches, and batch ticks use maxIterations 1. | Copy repeated ready-train recipe; keep the distinct blocked-gate scenario fresh and add real-path equivalence coverage. |
| AcceptanceCohortWorkflowTestsTrainImplicatedExclusion / PassingRemainder_ExcludesEjectedRevisionFromBothSelectors | Candidate setup and production train git work: WithTrain at :200 builds two/three candidates and uses SequenceAcceptanceVerifier. | One template per member count; ejections, implication stores and selector assertions unchanged. |
| AcceptanceCohortWorkflowTestsFollowerGateStart / StartBindsFollowerPlanAndHoldsOnlyFollower | Candidate setup and real follower-workspace git work: CreateScenario at :222 builds two worktrees; Scenario.Enable injects child launch. | Copy non-conflict recipe only in this class. Keep the singleton conflict case fresh. Optional fixture argument defaults to fresh for external callers in FollowerGateCarry/Stop. |
| MergeTrainBisectSlotLossTests / FailedFirstGate_BusyBisectKeepsReceiptAndImplicatesBeforeLease | Candidate setup and git launches: WithTrain at :184 builds three worktrees. BisectSlotVerifier scripts results; cohortStableSlotRoundDelay returns Task.CompletedTask. | Copy the repeated three-member recipe; real slot acquisition/loss, receipt retention, and bisect remain. |
| AcceptanceCohortWorkflowTestsWorkspacePreparation / DeveloperDispatchIntegration_CleanDivergence_MergesMainAndLeavesGoalWorktreeClean | Real worktree creation/integration and git launches: :21 onward performs Ensure, divergent commits and IntegrateMainBeforeDeveloperDispatch and asserts exact integration/cleanliness. Signal waits at :521 and :531 have hang-only 30s bounds. | Unchanged: setup/integration is the behavior under test; no real-time pacing to replace. |

## Copy contract and remaining evidence

`CohortCandidateTemplate.cs` is the single new standalone helper, consumed as
`IClassFixture<CohortCandidateTemplate>`. Only immutable prepared repository
bytes and recipe identities are shared within a class. Each copy owns its Git
objects/refs/indexes, linked worktrees, migrated store bytes and cleanup root.
Each test constructs its own kernel, goals and verification records. Goal IDs
are generated once per fixture recipe and reused only in its independent copies;
different fixture instances generate independent IDs. Failed lazy preparation
remains a failed recipe, and failed copies are deleted before rethrowing.

The copy repairs both absolute worktree pointers inside its own tree; relative
commondir entries keep their topology. No Git write is directed at the source
template. No gate/time seam or shipping source change is introduced.

The candidate `4cef23c8e0178f827a4793442654601d7d44867b` failed pointer
repair at `CohortCandidateTemplate.Copy` with `UnauthorizedAccessException`
on copied worktree `.git` files (receipt
`finding-evidence-0c7220e54f818ad0a1fb79b3`). The suspected mechanism is
Windows rejecting create-mode writes to existing hidden files. Pointer repair
now opens the existing file with `FileMode.Truncate`, preserving its attributes.
`BackgroundMergeTrainGateTests.CandidateCopy_HiddenGitFile_RepairsPointersAndPreservesAttributes`
explicitly arranges the hidden attribute on Windows and checks both repaired
pointers, retained attributes, usable Git state and unchanged template bytes.
Execution and confirmation of this diagnosis remain Acceptance-owned; include
this added regression case in the class's after total and test count.

`BackgroundMergeTrainGateTests.CandidateCopy_RealConstruction_PreservesTopologyAndTrainOutcome`
builds the fresh arm through the unchanged base builders. It compares main and
candidate trees, parent trees, changed paths, clean linked worktrees, and the
copy-local non-prunable topology. Both arms execute real RunMergeTrain with
explicit passed receipt, three completed members, zero ejections and one verifier
run, alongside member-outcome equivalence. Mutations of copied main/member
branches leave template refs unchanged; a sibling copy and template store bytes
also witness isolation. Acceptance must execute this contract before relying on
the optimization.

No existing assertions, Facts, Theories or InlineData rows are removed. No class
is assigned to an IsolatedProcessSpawning collection. The abstract base and both
deferral fixture/equivalence files remain unchanged. After values and dominant
step confirmation remain Tester-owned until the conductor receipt is available.
