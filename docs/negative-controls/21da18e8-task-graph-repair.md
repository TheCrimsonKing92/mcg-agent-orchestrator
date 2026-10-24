# Task graph repair: bounded persistence controls

## Lifecycle creation and admission regression (residual bootstrap finding)

Execution is Acceptance-owned and pending. Select `LifecycleGoalEpicInheritanceTests` and
`GoalRunGraphAdmissionTests`, with nonzero discovery/execution and no skips.
The persistent-runner theory exercises both lifecycle commands with real SQLite persistence,
fake workspace work and a fake final admission callback. It asserts durable creation before
workspace work, committed graph visibility at admission, retained stopped progress, and unchanged
unrelated goal/request state. The creation-failure theory forbids a replay journal for an uncommitted goal.

Single-source negative control: in `src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Goals.Lifecycle.cs`,
replace this exact block (only the first lifecycle checkpoint):

```csharp
    context.PersistCheckpoint(context.Kernel);
    goal = context.Kernel.GetGoal(goal.Id);
    context.CurrentGoal = goal;
    if (existingGoal is null)
```

with:

```csharp
    // Negative control: publish creation and start workspace work before committing creation.
    goal = context.Kernel.GetGoal(goal.Id);
    context.CurrentGoal = goal;
    if (existingGoal is null)
```

Select `LifecycleGoalEpicInheritanceTests.PersistentRunner_CommitsBeforeNativeAdmission_AndRetainsStoppedProgress`
(both theory cases). Require successful compilation and assertion RED:
`Lifecycle creation must be committed before workspace work.` Restore and require GREEN on the same cases.
An exception during setup, a nested-writer timeout, or zero selection does not count as RED.

`GoalRunGraphAdmissionTests.ActiveStateWrite_RejectsBeforeAdvanceOrLaunch` enters a real
state write transaction with an already committed graph, isolating the service's ownership guard
without waiting for a nested creation writer. The fake launcher must never run. This is an authored
regression, not an executed reproduction or an attribution of the August incident.

Source ownership: `CliPersistentStateRunner.ExecuteCommand` routes both lifecycle commands to
`CliLifecycleGoalExecution` before the default whole-state transaction. Its checkpoints reuse
`CliCommandCheckpoint` and the existing scoped creation owner; no raw replacement path or new
transaction/admission authority is introduced. Reviewer must independently check the final candidate.

## Retry-scoped launch ownership control

Residual finding `ac3-repair-compat-historical-launch-ownership`: launch receipts now retain
`dispatched_at` and repair compares them to the current `LastDispatch.DispatchedAt`. Retry
clears that identity; historical launch decisions remain consumed for replay protection.
Operator-intents schema v4 adds the nullable column without guessing attempt identities for
legacy rows. A legacy row conservatively protects an existing dispatch, and releases its
hold after reset. Kernel process/execution/status compatibility checks remain unchanged.

Select `Infrastructure.Tests:GraphRepairAdmissionTests` and `StoreSchemaRegistryTests`.
`AdmittedWorker_ProtectsOwnership_ThenRetryAllowsPredecessor` holds a real isolated child,
rejects new predecessors/prerequisites, applies a compatible insertion, observes exit and
completion against the original task, retries, reloads, then applies a Researcher predecessor.
`LaunchReceipt_CurrentAttemptOnly_RetryClearsOwnership` isolates durable launch ownership
from process metadata, covers a different later attempt and legacy unbound receipts.
`OperatorIntents_V3Upgrade_PreservesLaunchHistoryWithoutInventingAttempt` upgrades an actual
v3 table and preserves its consumed decision without fabricating a timestamp.

Native Acceptance owns execution. For a compiling single-source negative control in
`src/Mcg.AgentOrchestrator.Execution/Persistence/TaskGraphRepairAdmission.cs`, replace:

```csharp
            if (currentAttempts.TryGetValue(taskId, out var at) &&
                (reader.IsDBNull(1) || DateTimeOffset.Parse(reader.GetString(1)) == at))
                result.Add(taskId);
```

with:

```csharp
            result.Add(taskId);
```

This restores historical launch ownership while preserving the new schema and public call
shape. Require nonzero execution and the live-worker regression's assertion RED:
“Exited worker launch history must not prevent predecessor repair after retry reset.”
The receipt-only theory must also reject historical ownership after reset. Restore the source
and require GREEN for the same selection. Compilation/setup failure and zero selection are
not RED. This Developer lane supplies implementation and builds only, with no new test receipts.

## Inbox, replay and final native admission candidate

`CliCommandTestsGraphRepairConductCheckpoint.SingleGoalWatch_ProgressSave_AdvancesFollowingNativeCriticalBaseline` targets the actual single-goal **watch loop**. The fixture enqueues a compatible Reviewer insertion after Planner so the inbox prewalk must checkpoint progress before dispatch. It intercepts `CheckpointGoalSnapshotsAsync` (the real SQLite containment path), commits another goal's request while paused, and compares the next critical baseline to the raw durable target before deliberately rejecting that start and writing the supported stop file. Assertions require a successful `Saved` or `Merged` commit, repaired order after both checkpoints and unchanged unrelated state. Command exit/fault competes with the progress event. Its candidate GREEN is reported in receipt `finding-evidence-e6eeef743fe47dfe559aa037`; the acceptance-owner mutation in that receipt also passed, for the source reasons below.

### Acceptance baseline advancement control (current)

Receipt `finding-evidence-e6eeef743fe47dfe559aa037` at `f1925abd5f53c58e5babf6d58ea9ddd224cad214` reports the watch test passing on both the candidate and the dictionary-copy mutation. This is a non-discriminating control, not RED. `IsConductLoop` routes `conduct --watch` to `ExecuteConductLoopOutsideTransaction`, whose `tickBaselines` are independent of `CliAcceptanceCheckpoint`. In addition, copying only the acceptance progress dictionary leaves `RecordDurableBaseline` active. Do not repeat that mutation against the watch test.

Acceptance progress saves now delegate baseline advancement to `CliAcceptanceCheckpoint.RecordDurableBaseline`, as do externally committed snapshots and critical-save results. The fallback writer in `CliCommandCheckpoint` remains for other command owners; acceptance supplies the callback and cannot take that fallback. This gives the native control one source mutation that disables every advancement of the acceptance critical baseline, without changing the baseline submitted by `PersistCritical` or touching persistence itself.

Select `Infrastructure.Tests:CliCommandTestsGraphRepairConductCheckpoint`. Its new theory `AcceptanceOwner_ProgressSave_AdvancesNativeCriticalBaseline` covers both the owner's progress-save delegate and its externally committed snapshot callback. Each case uses real SQLite, pauses after a proven durable progress commit, preserves an unrelated goal request, and observes the exact baseline submitted to `SaveGoalSnapshotsWithMergeAsync` with `RejectConflict`. The initial and post-progress snapshots must differ. The probe deliberately rejects the critical checkpoint before process launch; baseline equality is asserted before the expected rejection exception. The existing watch test stays separate actual-caller coverage and is expected to remain GREEN under this acceptance-only mutation.

The acceptance owner's single-source native control targets `src/Mcg.AgentOrchestrator.App/Cli/CliAcceptanceCheckpoint.cs`. Exact unique old_text:

```csharp
        _baselines[snapshot.Id] = snapshot;
```

Exact new_text disabling acceptance baseline advancement:

```csharp
        // Negative control: keep the acceptance critical baseline at its load-time snapshot.
```

Require compilation, nonzero selection, and assertion RED in both new theory cases: “One invocation must advance its baseline before the native critical checkpoint.” Restore and execute the same selector GREEN. Setup failure, timeout, a different assertion, or a passing watch test alone does not satisfy this control. Native Acceptance owns execution; this round supplies source/test changes and build evidence only. The existing `CliCommandTestsPersistentRunnerCommandsGraphRepair.Checkpoint_OwnProgress_AllowsFollowingStrictCheckpoint` should also reject under this mutation; retain that selector for regression coverage and the previously demonstrated raw acceptance Persist control. No prior passed control is reopened by this documentation correction.

Actual selectors added in this round: `CliCommandTestsGraphRepairIntent`, `CliCommandTestsGraphRepairReplay`, `GraphRepairAdmissionTests`. Keep all prior writer-family and acceptance-caller selectors/controls below. The intent class proves pending-only CLI submission, ordered simulation referencing an earlier insertion, zero graph/progress mutation on typed rejection, and event-gated stale-holder preservation through real SQLite/restart. Replay covers explicit/default identity before commit, after graph+outcome commit with lost inbox completion, restart/recovery, conflicting key reuse and a fresh explicit later operation. The admission class calls the final TaskIntentAdmission.Launch seam: acknowledged-unapplied repair, changed committed graph, fresh repaired first role, unrelated goal, rejection/expiry release, shared serialization and preserved admitted fixture worker completion.

For the final-admission native negative control, use source path `src/Mcg.AgentOrchestrator.Execution/Persistence/TaskIntentAdmission.cs`. Exact unique old_text:

```csharp
        if (includeGraphRepair)
            receipt ??= ReadGraphSuppression(conn, tx, reference, decisionKey, validateCommittedGraph);
```

Exact new_text:

```csharp
        // Negative control: omit pending-repair and committed-graph admission checks.
```

Select `GraphRepairAdmissionTests.AcknowledgedUnappliedRepair_StopsAtFinalLaunchCallback` and `GraphRepairAdmissionTests.AppliedRepair_InvalidatesOldPreparation_FreshPreparationUsesRepairedFirstRole`. Require compiling assertion RED: respectively “Acknowledged unapplied repair must prevent the final launch callback.” and “Changed committed graph must prevent the stale final launch callback.” Callback-count invariants are asserted before exception shape. Restore the exact source and require GREEN on the same selectors. Compilation/setup failure or zero selection is not RED. Native Acceptance owns mutations and execution; Developer does not change refs or execute test hosts.

The original writer-family source mutations below remain necessary for the full preservation claim. None of these controls has been executed in this Developer lane; Reviewer must independently assess the combined candidate and native receipts. Operator live proof remains pending under its own requirement.

Status: Developer authored the bounded persistence corrections and regression tests through brief v4. Acceptance owns all execution, including controls; Reviewer owns independent source/evidence assessment. No reproduction of the historical incident, test pass, RED receipt, or full graph-repair completion is claimed here.

## v4 writer-family candidate and controls

Select `CliCommandTestsGraphRepairWriterFamilies` and `CliWriterPersistenceBoundaryTests`, requiring nonzero discovery and execution. The family class drives real SQLite and actual runner commands, with a one-shot save callback after external/sweep computation. The callback intercepts both the corrected merge and legacy captured-state transaction so the same interleaving survives source reversion. No timing sleeps or real worker launches occur.

- `ActualCaller_AppendBeforeSave_PreservesAuthoritativeGraph`: four cases, startup (max-iterations zero), goal-recovery, readiness-repair and next. Append immediately before the holder saves; commit an unrelated goal's human request; reload. The append/role identity and byte-equal unrelated goal/request must survive. Mutating reports fail loudly; startup reports stored/submitted evidence through attention, saves the second independent terminal repair, and completes startup.
- `SupervisorApplySafe_AppendBeforeSave_FailsLoudlyAndPreservesGraph`: actual supervisor with a fixture recoverable dispatch failure and alternate agent. An append at save survives and the skipped intended redelegation fails explicitly.
- `LandingOwner_Conflict_PreservesEvidenceAndGuardsRecovery`: four cases, mark-landed and startup with append or independent notes. A real isolated Git candidate is merged once. Journal landing evidence must already exist when a save callback commits either an appended task or independent notes. The terminal save rejects, retaining evidence and stored state. Actual goal-recovery must complete the same covered graph without another Git landing, while an appended uncovered graph remains active with the appended task pending.
- `OwnedCheckpoint_RepeatedSaves_CarriesRequestsAndRejectsUnownedChanges`: repeated checkpoints advance one baseline, persist a target request, and reject a later non-target request mutation before even the changed target saves.

Run three separate **single-source** controls against `src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs`, restoring between them:

1. In `RunStartupSweep`, replace `PersistSweepChanges(sweepKernel, sweepCheckpoint, workspace.OrchestratorDirectory);` with `foreach (var savedGoal in sweepKernel.Goals) PersistSingleGoalSnapshot(stateRepository, sweepKernel, savedGoal.Id);`. Select the startup theory case above.
2. In `ExecuteGoalMarkLandedWithPromptBudget` local Persist, replace `ownedCheckpoint.Persist(checkpoint);` with `PersistSingleGoalSnapshot(stateRepository, checkpoint, goalId, cancellation.Token);`. Select the mark-landed append case.
3. In `ExecuteSingleGoalCommandWithoutTransaction`, replace the local expression `void Persist(AgentOrchestratorKernel checkpoint) => ownedCheckpoint.Persist(checkpoint);` with `void Persist(AgentOrchestratorKernel checkpoint) => PersistSingleGoalSnapshot(stateRepository, checkpoint, goalId);`. Select all three report theory cases plus supervisor above.

Each mutation restores the old captured-graph write at the actual caller, leaving the one-shot gate intact. Required RED after compilation is **Concurrent append must survive the actual writer-family checkpoint and reload.** Check the actual assertion; setup failure, zero selection, a compiler error, or merely the compiled-reference guard failing is not the preservation RED. Restore each mutation and require the same selection GREEN. Also request full-class candidate GREEN and ordinary full acceptance. These are authored control instructions, not executed receipts. All prior v2/v3 and original inbox/admission/replay controls remain required; the family controls cannot replace them.

## Preserved probe analysis

Before this correction, `ExecuteBacklogIntakeOutsideTransaction` and `ExecuteAcceptanceQueueOutsideTransaction` loaded a whole kernel and supplied checkpoint/final-save delegates calling raw `SaveAsync`. `SqliteOrchestratorStateRepository.WriteSnapshotAsync` visits all goals; `UpsertGoalRowAsync` replaces a differing snapshot without the holder's load-time version. Write-time SQLite serialization alone cannot protect the earlier load/mutate interval. That source analysis motivated the old raw-save/baseline-aware diagnostic theory; it was never executed by this worker and is not historical-cause evidence.

The intentionally failing diagnostic theory is replaced by invariants at the actual corrected CLI routes. See [writer audit](../analysis/21da18e8-cli-writer-audit.md) for current owners, caller exclusions, conflict policy, the v4 family corrections, and the pending original admission-scope disposition. Full AC2 still requires actual native controls and combined-candidate review.

## Candidate tests for native focused evidence

Request `GraphRepairPersistenceTests` and require nonzero selection. Use the exact method list below and the executed discovery count:

- `Command_StaleHolder_PreservesCommittedGraph`: two theory cases, backlog-intake and acceptance-queue. Pause the actual CLI load after the old kernel is materialized, commit the five-role graph and unrelated progress through a separate real SQLite transaction, then run the CLI's checkpoint/final-save path and reopen. Assert role order, inserted identities and unrelated progress. The queue case exercises its final-save path with no ready goals.
- `BacklogIntake_CommitFailure_RetainsOnlyCommittedGoals`: two theory cases, first or second commit failure. Inject failure at the real creation transaction's precommit hook. Assert no intake mark before commit, no orphan goal/outbox/source claim after rollback, and retention of earlier committed batch goals/marks/claims with explicit partial-failure identities.
- `AcceptanceQueue_MultipleCheckpoints_PreservesRepairAndVerdicts`: actual two-goal queue, isolated Git repositories/worktrees, fake acceptance verifier and isolated build-slot seam; mutate an independent goal graph during the first verification. Require both verifications and per-goal checkpoints, persisted completed goals, candidate-bound durable gate-passed journal entries, and repair/progress preservation after reload. No real model calls or test hosts are started by the verifier.
- `Checkpoint_RepeatedMerge_RebasesAndCarriesHumanRequests`: real merge persistence adapter used by both callers; require independent notes, baseline rebasing, a second checkpoint, human requests, no unchanged resubmission and unrelated progress retention.
- `Checkpoint_GraphConflict_FailsWithBothStatesAndRebases`: real merge rejection after a changed graph; require non-success exception with submitted/stored evidence, in-memory rebase and durable graph preservation.
- `Checkpoint_NewGoal_CommitsItsHumanRequests`: real scoped creation transaction carries a new goal's human requests.
- `BacklogIntake_ExistingGoal_PreservesLegacyNoOp`: replay of keyless legacy intake returns false and retains exactly the original goal, rather than enabling request-key exception behavior.

All waits/interleavings are explicit callbacks. SQLite databases are unique per case; CLI/environment use follows the existing collection. Acceptance must report discovered/executed counts, failed assertion names/messages and actual receipts; build/setup failure, zero selection and skipped cases do not prove the invariant.

## Exact single-source negative control

Tester/Reviewer should submit a structured native negative-control evidence request for source path:

`src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs`

At **both** `ExecuteBacklogIntakeOutsideTransaction` and `ExecuteAcceptanceQueueOutsideTransaction`, replace this exact statement (currently two occurrences):

```csharp
void Persist(AgentOrchestratorKernel checkpoint) => persistCheckpoint(checkpoint);
```

with the pre-fix persistence delegate:

```csharp
void Persist(AgentOrchestratorKernel checkpoint) =>
    stateRepository.SaveAsync(checkpoint).GetAwaiter().GetResult();
```

Keep `persistCheckpoint` declaration and all other source unchanged. This is one source-file mutation restoring the raw checkpoint AND final-save behavior for both owners; the unused local is not a setup failure. Select exactly `GraphRepairPersistenceTests.Command_StaleHolder_PreservesCommittedGraph` (both theory cases). The first repair assertions run in setup before the old holder resumes; the post-command invariant precedes API-call-count assertions. Predicted RED assertion:

`Committed five-role graph must survive the stale-holder checkpoint and reload.`

Both cases should reach and fail that invariant after successful compilation. If a different assertion, transaction/setup error or host failure occurs, investigate and do not count it as the required RED. Then restore the source and require the same two cases GREEN. Also execute the full class on the candidate and normal acceptance; retain candidate/source bindings for GREEN → compiling mutated assertion RED → restored GREEN. Workers must not run these tests, mutate Git refs, or manufacture control receipts.

This control proves only the corrected two-owner overwrite mechanism. Typed inbox submission/application/replay, final process admission, other writer exclusions, independent review and Operator live-conductor proof remain separate obligations. No gate weakening or substitute unit-test narrative discharges them.

## Acceptance caller control (brief v3)

Select `CliCommandTestsPersistentRunnerCommandsGraphRepair.Acceptance_GateInterleaving_PreservesGraphAndJournal` (five theory cases, require actual nonzero execution). Each case drives the real `acceptance` runner and a real SQLite store with a fake, event-gated verifier. During the verifier pause a second transaction appends a task or records independent notes, and records an unrelated goal's human request. Reload checks graph identities/order and byte-equal serialized unrelated goal state/requests before checking error contracts.

The passing/no-append case requires Completed and candidate-paired gate-passed/passed journal entries after Finalize and the following checkpoint. Failing/no-append requires Verified with a retained candidate-bound controlled-gate failure and failed journal evidence: standalone acceptance records the verdict without performing conductor failure routing. Passing/append requires guarded landing abort while preserving the passing gate receipt and branch work. Failing/append requires loud skipped-checkpoint evidence with both submitted/stored state, with the graph retained. The fifth case seeds a new predecessor directly in the store to prove acceptance's task-set guard rejects landing; it is not evidence that typed repair submission rejects that incompatible predecessor. No typed graph-repair compatibility implementation is claimed by these tests.

In the single source file `src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs`, inside **ExecuteAcceptanceOutsideTransaction.Persist only**, replace the exact line:

```csharp
        void Persist(AgentOrchestratorKernel checkpoint) => checkpoints.Persist(checkpoint);
```

with:

```csharp
        void Persist(AgentOrchestratorKernel checkpoint) => PersistSingleGoalSnapshot(stateRepository, checkpoint, goalId);
```

Leave the checkpoint collaborator, baselines, guards and every other caller unchanged. This rebinds Persist to the pre-fix captured-state owner. On the failing-gate/append case the raw save should erase the compatible append; the **first** failed assertion must be:

`Compatible appended task must survive the actual acceptance checkpoint and reload.`

That invariant is checked before the expected rejection contract, so a missing exception cannot masquerade as RED. Successful compilation and that precise assertion failure are required; build/setup/host failures do not count. Restore the line and execute the same selector GREEN. Then execute both full persistence classes and ordinary acceptance on the exact candidate. The supporting `Checkpoint_OwnProgress_AllowsFollowingStrictCheckpoint` test exercises the invocation-scoped collaborator's progress and RejectConflict persistence with a shared map and target human request; it does not substitute for the requested same-invocation actual single-goal conduct sequence, which remains an explicit evidence gap. `Checkpoint_NonTargetMutation_RejectsBeforeAnyWrite` covers non-target progress and request-only mutations on a full reload, requiring both rows remain unchanged.

No GREEN or RED execution receipt exists from this Developer lane. Tester/Reviewer must request these through the native structured finding-evidence protocol. Operator live proof, combined audit review and the other criteria remain required even if this control passes.
