# Test audit c6cdba9b

Measured order is reproduced by `scripts/Get-TestCostRanking.ps1`. This pass applies the five audit questions in `docs/test-design-discipline.md`; “expensive region” means the work between entry to the bounded or repeated operation and its release, not nearby setup.

## GoalDependency_EscalatedPredecessor_DependentNeverRuns

- **Claim:** after predecessor A exhausts its dispatch retry and escalates, dependent B never advances far enough to create a workspace.
- **Expensive region:** five conductor iterations include blocked-goal rechecks; the default blocked-recheck path sleeps for 30 seconds even though the test only needs the decisions on either side of the wait.
- **Deletion consequence:** a regression could admit B after A escalates and create B's workspace.
- **Disposition: reduce subject (executed).** Inject `sleepFunc: _ => false`. The real clock is not part of the claim; the same driver, retries, iteration decisions, escalation count, and workspace assertion remain.

## GoalAcceptanceVerifier_real_process_shards_keep_receipts_in_attempt_artifacts_after_releasing_build_lease

- **Claim:** one prebuild feeds two independently filtered, overlapping MTP processes after the build lease is released; both write attempt-owned TRX receipts and all stable-slot leases become available.
- **Expensive region:** a build plus two real MTP launches previously loaded and discovered the full Infrastructure test assembly before each ran one rendezvous test.
- **Deletion consequence:** no test would cross the prebuild/released-lease/concurrent-real-process boundary, so slot retention, single-process fallback, or receipt custody regressions could pass.
- **Disposition: reduce subject (executed).** The manifest now builds and launches the checked-in `RealProcessShardProbe`, whose two tests provide only the required rendezvous. All build-count, process-id, overlap signal, executable, receipt-path, and lease assertions remain in the verifier test.

## RunEventMaintenanceCadence_self_defers_when_database_writer_is_busy

- **Claim:** cadence attempts maintenance, projects a busy store result as deferred rather than failed, and records the outcome in the conduct journal.
- **Expensive region:** the cadence invokes the real SQLite maintainer under a real writer lock; the store tries both oversized and aged pruning, each reaching a locked `BEGIN IMMEDIATE` command timeout.
- **Deletion consequence:** store-level deferral could remain correct while the cadence misclassifies it or omits the journal record.
- **Disposition: keep as-is.** This is the caller/projection layer. An injected maintainer could reduce its subject later, but adding that production seam and proving the real timing mechanism are outside this no-`src/` pass.

## SqliteRunEventStore_maintenance_defers_when_database_write_lock_is_active

- **Claim:** a real SQLite writer lock yields `Deferred`, reason `database-busy`, and zero deletions.
- **Expensive region:** maintenance performs oversized and then aged pruning; each path attempts `BEGIN IMMEDIATE` while the external writer owns the database lock.
- **Deletion consequence:** the store could block, fail, or delete data instead of returning its busy contract, while a mocked cadence test stayed green.
- **Disposition: keep as-is.** It is the lowest layer that can exhibit the real lock contract. The observed 30+30-second mechanism remains provisional until a focused timing receipt distinguishes both commands.

### Duplicate question

The maintenance tests are **not duplicates**. They pin one invariant at two necessary layers: `SqliteRunEventStore_maintenance_defers_when_database_write_lock_is_active` owns real-lock deferral and zero deletion; `RunEventMaintenanceCadence_self_defers_when_database_writer_is_busy` owns caller projection and journaling. Both survive pass 1.

## TerminalGoalSweep_conduct_loop_early_exits_print_blocker

- **Claim:** max-iterations-zero, zero-duration, and a pre-existing stop file still surface the same completed-but-unmerged sweep blocker exactly once before the persistent CLI exits.
- **Expensive region:** each case creates and commits a real acceptance repository/worktree, constructs persistent CLI state, runs terminal diagnosis, then takes an early-exit path.
- **Deletion consequence:** an early-exit reorder could suppress the only operator-visible blocker before any conductor tick.
- **Disposition: reduce subject (not executed in pass 1).** The integration case already proves ordinary CLI blocker surfacing, but the three early exits are owned by the persistent CLI wrapper. `ConductorBatchLoop.Run` checks all three exit conditions before its injected `measuredSweep`, so replacing this with a direct loop would not preserve the claim. A future reduction needs a narrow CLI construction seam; adding production code is out of scope here.

## GoalAcceptanceVerifier_partition_verdict_cache_invalidates_on_candidate_or_main_sha_change

- **Claim:** changing either candidate-tree identity or main SHA invalidates every partition verdict rather than reusing stale green results.
- **Expensive region:** three verifier runs previously enumerated all 17 checked-in Infrastructure lanes, producing 51 fake partition executions for a cache-key decision.
- **Deletion consequence:** stale partition greens could be reused after candidate or main changes.
- **Disposition: reduce subject (executed).** A purpose-built two-lane manifest makes six executions prove that every lane reruns after both key changes; the pass and cumulative-call assertions are unchanged.

## GoalAcceptanceVerifier_partition_verdict_cache_backstop_forces_full_rerun

- **Claim:** one cached reroll increments the counter, the configured backstop forces every partition to rerun, a red lane makes the aggregate red, and the counter resets.
- **Expensive region:** the first and forced verifier runs previously enumerated 17 lanes each, for 34 fake partition executions.
- **Deletion consequence:** green partition receipts could be reused indefinitely and conceal a later red partition.
- **Disposition: reduce subject (executed).** Two lanes are sufficient to prove full-rerun breadth, red aggregation, and counter reset while retaining the exact receipt assertions.

## ConductorSelfRelaunch_real_binary_build_self_check_and_handoff

- **Claim:** the real App build publishes a content-addressed successor, its real self-check validates startup state, authority transfers, and the successor emits start before its first conductor decision.
- **Expensive region:** a real App build, marker update, run-directory resolution, successor self-check process, handoff process, and first loop decision.
- **Deletion consequence:** build-to-binary marker wiring, startup protocol, or real handoff could break while delegate-level tests pass.
- **Disposition: keep as-is.** The App binary is the smallest artifact that crosses this integration boundary.

## ConductorSelfRelaunch_real_handoff_failure_stops_successor_and_reacquires_incumbent_lease

- **Claim:** failed real handoff verification stops the successor, reacquires the incumbent lease, and reports successful rollback.
- **Expensive region:** `ConductorSelfRelaunch.Create` first repeats the full App build, marker publication, run-directory copy, and self-check before reaching the handoff rollback behavior.
- **Deletion consequence:** a failed transfer could orphan the successor or leave neither process holding authority.
- **Disposition: reduce subject (follow-up).** `TryRelaunch` accepts an immutable prepared successor, but this pass found no existing fixture that shares only built immutable content while keeping state, leases, logs, and process ownership per test. Introduce that fixture only with focused lifecycle evidence; retain the current test meanwhile.

## ConductorSelfRelaunch_real_self_check_failure_keeps_incumbent_authority

- **Claim:** corrupt state is classified as a self-check failure, handoff is not attempted, and the incumbent retains its lease.
- **Expensive region:** a full real App build and run-directory preparation precede the corrupt-state self-check.
- **Deletion consequence:** corrupt state could reach handoff or cause the incumbent to surrender authority.
- **Disposition: decompose (executed).** The claim is split into a direct `ConductorSuccessorSelfCheck.Run` corrupt-state classification test and a `TryRelaunch` typed preparation-failure test that asserts handoff is untouched and the real incumbent lease remains held. The retained real-build success test continues to own binary/CLI wiring.

## Explicit exclusions

`InvokeIsolatedDotnet_reuses_prebuilt_test_assembly_and_dependency_directory` (backlog `9e9b1302`) and `Known-green fixture runs from an isolated landed worktree despite a dirty operator checkout` (backlog `2918058d`) were not changed or re-audited here.
