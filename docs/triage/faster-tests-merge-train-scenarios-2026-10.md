# Merge-train and cohort scenario setup costs

## Measurement ownership

The before column is the operator's mean **sum of per-test TRX durations per full
gate**, in seconds, over main gates `5d0ca6d8`, `cc2c0543`, and `359287b7`
(2026-10-27/28). Tests per gate is the operator's corresponding mean. These are
supplied measurements, not new worker runs. The operator did not supply the main
commit or host identifier for each gate. Source inspection started at
`617daadddcb038cce07f0074e2e1b1c1f1d9fdcf`.

The Tester inspected the existing conductor receipt **R**,
`finding-evidence-22f3701019771228c6b3b692`, for candidate
`814471a4ed99608806b8832b13e8d54ea3e767b7`. Its result file is
`C:/Users/miles/vcs/mcg-agent-orchestrator/.orchestrator/pre-review-evidence-attempts/ea0f333b1aec4d1490c4e359e744ff61/ea0f333b-0-20261028205116612-145e3f320829413ea5e88e343b2cc03f.result.json`.
It records accepted/passed focused evidence, seven successful checks, exit 0,
and no unexecuted selections. The three Infrastructure.Tests TRX files contain
495 passed results; 169 belong to the twelve exact classes below (151 in the ten
changed classes). No worker executed tests or a timing harness.

The after column sums `UnitTestResult.duration` by joining `testId` to
`TestDefinitions.UnitTest.TestMethod.className`, using exact class equality.
Every theory row is included once. Values are rounded to three decimals only
after summation. Counts and outcomes were cross-checked against the receipt's
normalized CTRF files; their millisecond rounding changes class sums by less
than 0.003 seconds. The unchanged deferral and workspace classes are included as
context, not as optimized classes.

Each fixture is constructed empty; template preparation happens lazily inside
the first consuming test for each recipe, under that test's capacity lease, so
preparation is included in the after test-seconds. Fixture disposal happens after
the class and is outside these per-test sums. BackgroundMergeTrainGateTests'
after total includes all three added copy contract/regression tests.

## Before and after

| Class | Before seconds/gate | Tests/gate | After seconds | After tests | Receipt / run |
|---|---:|---:|---:|---:|---|
| MergeTrainRedAttributionReplayScenarioTests | 201.9 | 8.7 | 143.311 | 13 | R / R2 |
| AcceptanceCohortWorkflowTestsTrainRedAttribution | 179.7 | 14.3 | 534.614 | 43 | R / R1 |
| PassedMergeTrainReceiptHoldTests | 158.4 | 11 | 62.304 | 11 | R / L1 |
| AcceptanceCohortWorkflowTestsMergeTrain | 156.2 | 6.7 | 1026.470 | 20 | R / R1 |
| MergeTrainDeferralFixtureEquivalenceTests | 149.5 | 4 | 109.914 | 6 | R / R2 |
| AcceptanceCohortWorkflowTestsLandingAndReceipts | 142.1 | 4.7 | 484.121 | 14 | R / R1 |
| AcceptanceCohortWorkflowTestsGroupedGateAttempts | 142.1 | 4.3 | 950.586 | 13 | R / R1 |
| BackgroundMergeTrainGateTests | 141.6 | 5 | 559.754 | 8 | R / R1 |
| AcceptanceCohortWorkflowTestsTrainImplicatedExclusion | 139.9 | 3.7 | 892.956 | 11 | R / R1 |
| AcceptanceCohortWorkflowTestsFollowerGateStart | 137.6 | 3.7 | 524.698 | 11 | R / R1 |
| MergeTrainBisectSlotLossTests | 137.6 | 7 | 95.073 | 7 | R / R2 |
| AcceptanceCohortWorkflowTestsWorkspacePreparation | 136.2 | 4 | 446.413 | 12 | R / R1 |

All rows cite receipt R above. Run labels identify its exact TRX artifacts:

| Run | Host / executor | UTC execution window | TRX run ID |
|---|---|---|---|
| R1 | MCGAO-RUNNER-1 / mcg-exec-1 | 2026-10-28 20:53:41–21:13:08 | fd6ac3d6-adb3-47eb-b4bf-ee6cde720085 |
| R2 | MCGAO-RUNNER-2 / mcg-exec-2 | 2026-10-28 20:53:46–20:56:13 | 7b3cabd5-a928-4848-9724-7ff01e114760 |
| L1 | SEVENTHSON / local acceptance | 2026-10-28 21:13:33–21:14:46 | 14b34c92-9a21-4a85-b8f0-b67e745bb56b |

Relative to the result file's directory, the TRX paths are:

- R1: `remote-first/f230cf29bbc4f167/fe-12b7b226/summary-Mcg.AgentOrchestrator.Infrastructure.Tests-1-r1-FullyQualifiedName-AcceptanceCohortWorkflowTests-f1746887559584c6.trx`
- R2: `remote-first/f230cf29bbc4f167/fe-3daf6294/summary-Mcg.AgentOrchestrator.Infrastructure.Tests-1-r1-FullyQualifiedName-ConsoleReplacementSourceGuard-07f13123d754337f.trx`
- L1: `ea0f333b-pre-review-20261028205118789-e7bd0a300b8d4510a80d5cdf5f789044-candidate.reviewer-focused-evidence-infrastructure-tests-fullyqualifiedname-orchestratortemprootsourceguardt-c3e2fdd5545e61c6.trx`

The receipt records execution mode/reason
`explicit-focused-mapping+compatible-same-project-batch+unbatched:Infrastructure.Tests=bounded-filter-overflow`.
All three TRX files label TestSettings `default`; their adapter identifies
version 3.2.2. The receipt records filters rather than the original launcher
command or all runtime settings. The managed-runner entry point is
`scripts/Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj`;
this identifies the runner surface, not a reconstructed invocation.
The actual recorded filters follow, including additional classes batched by the
conductor. The abstract-base substring selects other cohort classes too; only
the twelve exact class names contribute to the table.

<details>
<summary>R1: recorded managed-runner filter</summary>

```text
FullyQualifiedName~AcceptanceCohortWorkflowTests|FullyQualifiedName~AcceptanceCohortWorkflowTestsFollowerGateStart|FullyQualifiedName~AcceptanceCohortWorkflowTestsGroupedGateAttempts|FullyQualifiedName~AcceptanceCohortWorkflowTestsLandingAndReceipts|FullyQualifiedName~AcceptanceCohortWorkflowTestsMergeTrain|FullyQualifiedName~AcceptanceCohortWorkflowTestsTrainImplicatedExclusion|FullyQualifiedName~AcceptanceCohortWorkflowTestsTrainRedAttribution|FullyQualifiedName~AcceptanceCohortWorkflowTestsWorkspacePreparation|FullyQualifiedName~AssemblyTempRedirectProjectGuardTests|FullyQualifiedName~BackgroundMergeTrainGateTests|FullyQualifiedName~BuildServerShutdownSourceGuardTests|FullyQualifiedName~CallerFilePathRootSourceGuardTests|FullyQualifiedName~CanaryEngineSurfaceCoverageTests|FullyQualifiedName~CliChildProcessBoundSourceGuardTests|FullyQualifiedName~ConductorLoopHandoffVerificationClockSourceGuardTests|FullyQualifiedName~ConductorRefusedGroupedIdentityGuardTests
```

</details>

<details>
<summary>R2: recorded managed-runner filter</summary>

```text
FullyQualifiedName~ConsoleReplacementSourceGuardTests|FullyQualifiedName~DashboardRemnantSourceGuardTests|FullyQualifiedName~DirectGitLaunchSourceGuardTests|FullyQualifiedName~ExecutionOwnedGroupCloseSourceGuardTests|FullyQualifiedName~ExecutionWorkspacesReferenceGuardTests|FullyQualifiedName~GoalAcceptanceLaneIsolatedRootGuardTests|FullyQualifiedName~GoalAcceptanceVerifierSplitFactParityTests|FullyQualifiedName~GoalPriorityStarvationGuardTests|FullyQualifiedName~HealthInspectorNetworkSourceGuardTests|FullyQualifiedName~LaneIsolatedRootGuardTests|FullyQualifiedName~LegacySparkNameSourceGuardTests|FullyQualifiedName~LineEndingPolicySourceGuardTests|FullyQualifiedName~LockContentionWaitSourceGuardTests|FullyQualifiedName~MergeTrainBisectSlotLossTests|FullyQualifiedName~MergeTrainDeferralFixtureEquivalenceTests|FullyQualifiedName~MergeTrainRedAttributionReplayScenarioTests|FullyQualifiedName~OperatorControlPlaneRemovalGuardTests|FullyQualifiedName~OrchestratorAttemptRootLeakGuardTests
```

</details>

<details>
<summary>L1: recorded managed-runner filter</summary>

```text
FullyQualifiedName~OrchestratorTempRootSourceGuardTests|FullyQualifiedName~OrderingEventSourceGuardTests|FullyQualifiedName~ParallelSharedStateSourceGuardTests|FullyQualifiedName~PassedMergeTrainReceiptHoldTests|FullyQualifiedName~PowerShellLaunchSourceGuardTests|FullyQualifiedName~ProcessStartInfoSourceGuardTests|FullyQualifiedName~ReadOnlyRoleMainIntegrationGuardTests|FullyQualifiedName~RealTimeConditionSourceGuardTests|FullyQualifiedName~RealTimeWindowSourceGuardTests|FullyQualifiedName~RealWorkerProcessGuardTests|FullyQualifiedName~RedirectedStreamDrainSourceGuardTests|FullyQualifiedName~RuntimeAssemblyArchitectureTests|FullyQualifiedName~SlotFixtureCollectionGuardTests|FullyQualifiedName~StopwatchThresholdSourceGuardTests|FullyQualifiedName~TestProcessStopIdentityGuardTests|FullyQualifiedName~WholeTestTimeoutSourceGuardTests|FullyQualifiedName~WorkerSkillCatalogRepositoryGuardTests
```

</details>

## Timing interpretation

The earlier **1,537.1 before seconds/gate versus 5,273.887 after test-seconds**
are not comparable: the before figures are historical full-gate means, whereas
the after figures are from a focused receipt with different selections and test
counts, unspecified baseline hosts/settings, and three after-run hosts. Per-test
TRX durations also include `HostCapacityBoundTestBase.InitializeAsync` slot waits.
The before/after table and receipt R citations above remain historical context
and correctness evidence.

The operator supplied the comparable measurement under the **2026-10-29 00:44Z
owner decision** for `MERGE-TRAIN-TIMING-001`. These are operator measurements;
no worker ran tests or a timing harness. `mcg-exec-2` was taken out of gate
admission and was idle. Base `617daaddd` and candidate `4a445370` ran in separate
worktrees as the executor's runner account, with the same selection each time:
the ten changed classes, excluding the three added `CandidateCopy_` tests.
All **156 tests passed in every run**. The six runs used ABBA order
(base, candidate, candidate, base, base, candidate).

| Metric | Base runs | Candidate runs | Base median | Candidate median | Reduction |
|---|---|---|---:|---:|---:|
| Test wall-clock, TRX start to finish (s) | 647.2, 636.0, 653.9 | 554.4, 564.0, 555.0 | 647.2 | 555.0 | 14% |
| Summed test-seconds | 4672, 4759, 4830 | 4086, 4333, 4273 | 4759 | 4273 | 10% |

Every candidate run was faster than every base run, with about 2% spread within
each arm. Within a class, tests ran serially: the class span equalled its summed
test time, so run wall-clock was set by the largest class.

| Changed class | Base median test-seconds | Candidate median test-seconds |
|---|---:|---:|
| AcceptanceCohortWorkflowTestsTrainRedAttribution | 644.6 | 552.1 |
| AcceptanceCohortWorkflowTestsMergeTrain | 528.8 | 476.1 |
| MergeTrainRedAttributionReplayScenarioTests | 472 | 426 |
| AcceptanceCohortWorkflowTestsLandingAndReceipts | 467 | 417.5 |
| AcceptanceCohortWorkflowTestsGroupedGateAttempts | 461.1 | 408.2 |
| AcceptanceCohortWorkflowTestsFollowerGateStart | 431.6 | 377.1 |
| AcceptanceCohortWorkflowTestsTrainImplicatedExclusion | 428.1 | 377 |
| PassedMergeTrainReceiptHoldTests | 410.9 | 377.1 |
| MergeTrainBisectSlotLossTests | 305.9 | 275.9 |
| BackgroundMergeTrainGateTests | 247.2 | 235.1 |

The operator's dominant-step disposition applies to all changed classes:
"Repository and worktree construction is confirmed as a cost but not the dominant one: sharing it cut each class by 8-14%, so most per-test time is in the production merge-train work each test drives."
This records the operator's interpretation, not a confirmed-dominant phase
measurement. The per-class source-inspection table below retains the original
suspected steps.

Under the operator ruling on criterion 3, this comparison is the timing evidence;
the historical after column may keep citing receipt R. The Reviewer judges the
document against this ruling. The owner decision is to land the goal.

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
| BackgroundMergeTrainGateTests / PlannerConflict_EscalatesWhileAnotherTrainOccupiesAcceptanceWidth | Candidate setup and git launches: CreateReadyTrain at :512 repeats three worktrees; BackgroundGateStartHarness captures launches, and batch ticks use maxIterations 1. | Copy repeated ready-train recipe; keep the distinct blocked-gate scenario fresh and add real-path equivalence coverage. |
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

Candidate `8507934da21e5f0a145e92bbd2884b33ed6915d2` failed
`MergeTrainRedAttributionReplayScenarioTests.ReadOnlyChild_LandedRemainderStillFailsCulprit`
at `LandingExecutor.MergeTrains.cs:240-244` (receipt
`finding-evidence-7849b7da9d55232efdda0f19`): `read-tree -m -u` rejected
`Other.Source.cs` as not uptodate after main's ref advanced. The source-supported
hypothesis is stale copied index stat data: copying files/indexes does not rebind
the cached metadata to the destination, and both Git runners set
`GIT_OPTIONAL_LOCKS=0`, so status checks cannot persist a refresh
(`InfrastructureTestSupport.cs:185`, `src/Mcg.AgentOrchestrator.Execution/Processes/GitCli.cs:122`).
The copy now explicitly runs `update-index --refresh` for the root and each
linked worktree after pointer repair. This adds one Git launch per checkout;
candidate/worktree construction still happens once per recipe. Refresh failures
throw through the existing partial-copy cleanup; no tracked content is reset or
staged. All refresh writes target the copy.

`BackgroundMergeTrainGateTests.CandidateCopy_StaleIndexes_CanUpdateExistingFilesInRootAndLinkedWorktree`
arranges stale metadata with fixed file timestamps, then advances refs and runs
the same two-tree checkout operation on existing tracked files in both checkouts,
without an intervening status/refresh. It asserts the updated contents, clean
checkouts, and unchanged template index bytes, refs and contents. It uses no
wall-clock pacing. Receipt R passes this regression (84.949 s, R1) and the original
failing scenario (10.435 s, R2) on the current candidate. This verifies the repaired
behavior in those cases; it does not isolate the historical failure's cause.
The regression is included in the class's after total and test count.

The candidate `4cef23c8e0178f827a4793442654601d7d44867b` failed pointer
repair at `CohortCandidateTemplate.Copy` with `UnauthorizedAccessException`
on copied worktree `.git` files (receipt
`finding-evidence-0c7220e54f818ad0a1fb79b3`). The suspected mechanism is
Windows rejecting create-mode writes to existing hidden files. Pointer repair
now opens the existing file with `FileMode.Truncate`, preserving its attributes.
`BackgroundMergeTrainGateTests.CandidateCopy_HiddenGitFile_RepairsPointersAndPreservesAttributes`
explicitly arranges the hidden attribute on Windows and checks both repaired
pointers, retained attributes, usable Git state and unchanged template bytes.
Receipt R passes this regression (30.017 s, R1). Its arranged hidden-file case
therefore verifies the repair; the historical diagnosis remains a hypothesis.
This regression is included in the class's after total and test count.

`BackgroundMergeTrainGateTests.CandidateCopy_RealConstruction_PreservesTopologyAndTrainOutcome`
builds the fresh arm through the unchanged base builders. It compares main and
candidate trees, parent trees, changed paths, clean linked worktrees, and the
copy-local non-prunable topology. Both arms execute real RunMergeTrain with
explicit passed receipt, three completed members, zero ejections and one verifier
run, alongside member-outcome equivalence. Mutations of copied main/member
branches leave template refs unchanged; a sibling copy and template store bytes
also witness isolation. Receipt R passes this real-path contract (156.864 s, R1).
The source assertions at BackgroundMergeTrainGateTests.cs:425-488 check those
outcomes, and :500-508 executes RunMergeTrain in both arms with explicit counts.

Receipt R also passes both independent fresh/shared deferral theory rows:
busyRounds 2 (18.510 s) and 3 (23.589 s), in R2. The unchanged test still invokes
RunFreshDeferral separately at MergeTrainDeferralFixtureEquivalenceTests.cs:17;
MergeTrainDeferralSharedFixture.cs:119-130 constructs and cleans a fresh
repository, and :192-208 builds its scenario candidates through the unchanged
base builder. Neither that fixture, its equivalence class, nor the base has a
diff from 617daaddd. The five-scenario reset test also passes (43.655 s).

No existing assertions, Facts, Theories or InlineData rows are removed. No class
is assigned to an IsolatedProcessSpawning collection. The abstract base and both
deferral fixture/equivalence files remain unchanged. Criteria 2 and 4 now have
matching candidate execution evidence; criterion 5's eleven Developer-deferred
classes are all covered by R, so no duplicate evidence request is needed.
Assertion preservation, notes acceptance, collection policy and shipping-source
scope remain Reviewer-owned (criteria 1, 3 and 6). The operator comparison and
criterion 3 ruling above supersede the earlier open timing limitation.
