# Orchestrator State Model Specification

Status: normative for implementation work after 2026-07-08.

This document resolves the current ambiguity around orchestrator state. The rule is simple: durable state is written by a small set of named state transitions, and every dashboard, CLI view, conductor tick, and repair command must either call those transitions or be explicitly identified as an emergency repair.

## Decisions

| Area | Decision | Rationale |
| --- | --- | --- |
| Authority | `Goal.Status` and `TaskSpec.Status` are the authoritative persisted states. `GoalLifecycleState`, dashboard buckets, readiness stages, health labels, and loop stop summaries are read-only projections over the persisted statuses plus external facts. | The incidents came from treating projections and statuses as peers. A projection may explain why a goal is blocked or clean, but it must not become a second truth source. |
| Escalated state | Escalation is a waiting state, not a terminal state. It is represented by an open `HumanInputRequest` and `Goal.Status = WaitingForHuman`; if scoped to a task, that task is `WaitingForHuman`. The loop may stop because every active goal is terminal or waiting, but "escalated" is not a durable terminal status. | Operators can answer, dismiss, park, cancel, or supersede an escalation. Calling it terminal caused loops to stop without defining the resume transition. |
| Artifact lifecycle | `dispatch.json`, heartbeat, stdout, stderr, and exit-code files are consumed-once evidence inputs. Their normalized truth is `TaskDispatchRecord`, `TaskProcessRecord`, and `TaskVerificationRecord` in the snapshot/store. After a successful consume, files are retained only for audit and display; they must never re-drive a second state mutation unless the stored cursor/token says they have not been consumed. | Re-reading artifacts after a crash caused duplicate rewind/reconcile behavior. The store must record whether evidence was applied. |
| Concurrency | State writes use an optimistic lease token: load snapshot version, compute one transition, save only if the version is unchanged, then retry from a fresh snapshot on conflict. No writer may hold an in-memory goal across external process execution and then save over newer CLI or tick changes. | Mid-tick reconciliation and CLI verbs both need to work. Last-writer-wins is the source of lost task/goal rewinds. |
| Direct SQLite repair | The SQLite tool may repair persisted fields only through documented emergency commands. Each repair must append a `GoalPolicyDecision` timeline event naming the repaired field, old value, new value, and reason. It must not silently edit status, dispatch, process, or verification records. | Repair needs to exist for operator recovery, but silent store edits are indistinguishable from corruption. |
| Landing | Landing is the only writer that may move an already verified goal through accepted post-merge completion. It writes `Goal.Status = Completed` only after acceptance/merge/dogfood-record obligations for that goal boundary have passed or been explicitly recorded as not applicable. | `Completed` must mean the orchestrator is done with the goal, not merely that all worker tasks passed. |

## Persisted Fields And Owners

The persisted state currently appears in `GoalSnapshot`, `TaskSnapshot`, `HumanInputRequestSnapshot`, and durable auxiliary stores such as run events and dogfood log entries. This table names who may write the state in normal operation.

| Field | Authoritative meaning | Allowed writers and timing |
| --- | --- | --- |
| `Goal.Status` | Operator-visible durable goal control state: `Draft`, `Active`, `WaitingForHuman`, `Parked`, `Verified`, `Completed`, `Failed`, `Cancelled`, `Superseded`. | Classifier/intake creates `Draft` and may activate only through delegation. CLI verbs may cancel, supersede, park, recover, retry, answer/dismiss human input, or manually verify through kernel transitions. Tick reconcile may reopen stale terminal-with-dispatchable-work to `Active`, normalize premature `Completed` to `Verified`, or set waiting when it creates a human request. Landing may set `Completed`. SQLite tool only for logged emergency repair. |
| `TaskSpec.Status` | Durable task execution state: `Pending`, `Assigned`, `Running`, `WaitingForHuman`, `Completed`, `Failed`, `Cancelled`. | Classifier/planner creates `Pending` tasks. Delegation assigns `Assigned`. CLI and dashboard task verbs may report progress, retry, redelegate, cancel, manually verify, or request/answer input through kernel transitions. Tick reconcile may set `Running` when recording dispatch/process, `Completed`/`Failed` after consumed exit evidence, or `Assigned` when requeueing an interrupted dispatch. SQLite tool only for logged emergency repair. Landing must not mutate task status except via a documented terminal sweep before cleanup. |
| `Goal.Timeline` | Append-only audit log of state decisions, task events, dispatch/process evidence, verification, and repairs. | Every writer that changes a persisted goal/task/human-input field must append a same-transaction event. Tick reconcile, classifier, CLI verbs, and landing append through kernel methods. SQLite repair must append a `GoalPolicyDecision`. No writer may delete or reorder timeline entries; corrections are new entries. |
| `TaskVerificationRecord` / `LastVerification` / `VerificationHistory` | Normalized verification evidence for manual checks and dispatch exits. `VerificationHistory` is append-only; `LastVerification` points to the latest applied record or is cleared only when retry semantics intentionally make the task dispatchable again. | CLI/dashboard `verify-*` commands may append manual verification. Tick reconcile appends dispatch-exit verification exactly once per consumed exit artifact. Classifier may not write verification. Landing reads verification but does not create it except for documented acceptance evidence records outside task verification. SQLite repair may append only if reconstructing lost evidence from immutable logs, and must log the repair. |
| `TaskDispatchRecord` / `LastDispatch` | The latest dispatch intent for a task: worker, command, directory, model, prompt, complexity, base/result commits, sandbox facts. | Tick reconcile or CLI/dashboard dispatch verbs write it when a task is `Assigned`, or when refreshing a recorded dispatch that is `Running` with no process. Result/base commit and sandbox fields may be updated by the dispatch runner before or during result consumption. Retry/requeue clears it to make a fresh dispatch legal. SQLite repair only for logged reconstruction. |
| `TaskProcessRecord` / `LastProcess` | The latest background process observation for a dispatch: process ids, log paths, exit path, start/complete, exit code, cancellation. | Start-dispatch writes the initial running process. Tick reconcile/refresh writes completion/cancellation observations. Cancel-dispatch writes `WasCancelled = true` and `Task.Status = Cancelled`. Retry/requeue clears stale process state after confirming no live owned process remains. SQLite repair only for logged reconstruction or cancellation correction. |
| `TaskExecutionRecord` / `LastExecution` | API-only in-process model execution result, not subscription dispatch evidence. | API task runner writes when a non-file API run completes. Retry clears it. Tick dispatch/process reconcile and landing must not write it. |
| `Task.AssignedAgentId` | Current worker identity for role assignment. | Activation/delegation, redelegation, and reassign commands write it. Tick reconcile, verification, and landing do not change it. |
| Verification plans and retry counters | Policy and retry bookkeeping for acceptance and redispatch decisions. | Classifier/refiner may set verification plans at task creation/refinement. CLI verbs may update plans and acknowledge subscription-limit reviews. Tick reconcile may increment empty-output and subscription-limit retry state when classifying dispatch results. Retry clears only the fields specified by retry semantics. |
| `HumanInputRequestSnapshot` fields | Durable escalation/wait record. Open requests are the source of waiting behavior. | CLI/dashboard/conductor may create requests when a spec, risk, provider, or recovery decision is needed. Answer/dismiss verbs complete requests. Stale-wait policy may auto-answer or dismiss only requests marked auto-defaultable/dismissible. SQLite repair only for logged emergency close/reopen. |
| `Goal.RefinedSpec`, `SourceBacklogItemId`, dependencies, acceptance failure summary | Goal metadata and acceptance-gate memory. | Classifier/refiner writes refined spec, source backlog link, and dependencies. Acceptance gate writes/clears latest acceptance failure. Tick reconcile and landing may read them but should not mutate except through the acceptance gate. |
| Run events, lifecycle event files, dogfood log records | Secondary audit streams. They are not status authority. | Tick pusher, lifecycle event writer, and landing append. They may be rebuilt or repaired from authoritative snapshots when possible. Readers must not infer a different goal/task status from these streams. |

## Authoritative State And Projections

`Goal.Status` answers "what may the orchestrator do to this goal next?" It is the control state. `TaskSpec.Status` answers the same question for an individual task.

`GoalLifecycleState` answers "where does this goal appear in the conductor lifecycle right now?" It is derived from `Goal.Status`, task statuses, `LastDispatch`, `LastProcess`, open human input, workspace facts, merge facts, dogfood-record facts, and cleanup facts. It must be recomputed on read and must not be persisted as a competing status.

When persisted control state and derived lifecycle disagree, repair the persisted control state through a named transition. Do not special-case the projection to hide the disagreement. For example, a goal with `Goal.Status = Completed` and an `Assigned` task is invalid persisted state; the repair transition is to reopen it to `Active` with a timeline event, or to cancel/supersede the task and record why.

## Legal Task Transitions

Normal task flow:

```text
Pending -> Assigned -> Running -> Completed
```

Allowed off-path transitions:

| From | To | Writer | Conditions |
| --- | --- | --- | --- |
| `Pending` | `Assigned` | Classifier/delegation or CLI redelegation | A role-matched available agent is selected. |
| `Assigned` | `Running` | Tick reconcile or dispatch CLI | A dispatch is recorded, or a recorded dispatch process starts. |
| `Running` | `Completed` | Tick reconcile, API runner, or CLI progress | Exit/manual/API evidence passes verification and no open task-scoped human request remains. |
| `Running` | `Failed` | Tick reconcile, API runner, or CLI progress | Exit/API evidence fails, worker result has a hard blocker, or empty-output/no-evidence policy rejects success. |
| `Running` | `Cancelled` | CLI/dashboard cancel or goal stop preflight | The owned process was cancelled or confirmed not live and the cancellation is recorded. |
| Any non-terminal | `WaitingForHuman` | CLI/dashboard/conductor | An open task-scoped `HumanInputRequest` is created. |
| `WaitingForHuman` | `Running` | Answer/dismiss human input | The task has a running process or recorded dispatch to resume. |
| `WaitingForHuman` | `Assigned` | Answer human input | No running process remains and no passing verification exists. |
| `WaitingForHuman` | `Completed` | Answer human input | A passing verification already exists and the human answer removed the last blocker. |
| `Failed` / `Cancelled` / `Completed` | `Assigned` | Retry/recover CLI or tick recovery | No live owned process remains; latest verification/dispatch/process/execution are cleared as specified; downstream invalidation is applied when retrying upstream work. |
| `Running` | `Assigned` | Tick recovery only | The process is confirmed detached/interrupted, artifacts are not consumable, and the retry/requeue event records the evidence. |

Illegal transitions must fail fast, not silently coerce:

- `Assigned` directly to `Completed` without verification, unless an explicit manual completion command records a passing verification in the same transaction.
- `Running` to `Assigned` while a tracked process is live.
- Any retry of `WaitingForHuman` while an answer-required request remains open.
- Any mutation of a `Completed` task by landing.

## Legal Goal Transitions

Normal goal flow:

```text
Draft -> Active -> Verified -> Completed
```

Allowed off-path transitions:

| From | To | Writer | Conditions |
| --- | --- | --- | --- |
| `Draft` | `Active` | Classifier/delegation, CLI, or tick activation | At least one task is assigned or dispatchable. |
| `Active` | `WaitingForHuman` | CLI/dashboard/conductor | At least one open goal- or task-scoped human input request exists. |
| `WaitingForHuman` | `Active` | Answer/dismiss/stale-wait policy | All open requests for the goal are completed and at least one task is not completed/cancelled. |
| `WaitingForHuman` | `Verified` | Answer/dismiss/stale-wait policy | All open requests are completed and all task verification gates pass. |
| `Active` | `Verified` | Acceptance/verification gate | Every task gate passes and no open human input remains. |
| `Verified` | `Completed` | Landing | Acceptance, integration, dogfood recording, and cleanup obligations have completed or been explicitly waived. |
| `Active` / `WaitingForHuman` / `Verified` | `Parked` | CLI/dashboard/operator policy | Operator records a reason; open waits are completed with the park reason. |
| `Parked` | `Active` | CLI recover/resume | Operator records a resume reason and dispatchable work exists. |
| `Active` / `WaitingForHuman` / `Parked` / `Verified` | `Cancelled` or `Superseded` | CLI/dashboard operator command | No owned process is live, or cancellation has been recorded first. |
| Any terminal-with-dispatchable-work except `Completed` after landing | `Active` | Tick recovery or CLI recover | A stale terminal status conflicts with non-terminal tasks; repair event records old status and reason. |
| `Completed` | `Verified` | Tick recovery or CLI repair | Completion was premature: task gates pass but landing/cleanup is incomplete. |

`Failed` is a goal-level terminal control state only when the operator intentionally fails the goal or a deterministic policy declares it unrecoverable. A failed task alone does not require `Goal.Status = Failed`; it may leave the goal `Active` with a repair action.

Loop stop condition: a bounded `conduct --loop` may stop when every selected goal is either durable-terminal (`Completed`, `Failed`, `Cancelled`, `Superseded`) or waiting/parked with no automatic transition available. The stop reason may say "all done or waiting"; it must not imply escalations are terminal.

## Artifact Lifecycle

Dispatch artifacts are evidence envelopes, not the source of truth:

1. `RecordTaskDispatch` persists dispatch intent before a process may start.
2. `RecordTaskProcessStarted` persists process identity and artifact paths.
3. Heartbeat/stdout/stderr/exit files may be read by refresh/reconcile while the process is running or after it exits.
4. When an exit is classified, the writer appends exactly one `TaskVerificationRecord`, updates `TaskProcessRecord.CompletedAt` and `ExitCode`, and transitions task status.
5. The writer records a consume marker in durable state. Until a dedicated marker exists, the tuple `(LastProcess.ExitCodePath, LastProcess.CompletedAt, LastVerification.Command, LastVerification.CompletedAt)` is the minimum idempotency key: if it matches, the artifact has already been consumed.
6. After consumption, artifact files remain audit material. Cleanup may delete them only after their normalized records and any bounded stdout/stderr previews or paths are durable.

Re-derivation is allowed only for display and repair. It must not create a second task transition unless the stored consume marker shows the artifact was never applied.

## Concurrency Rule

All state mutations must be serialized through optimistic snapshot versioning:

1. A writer loads the current snapshot and version.
2. It validates the transition against the state it loaded.
3. It writes the new snapshot, timeline event, and any event-store append in one transaction if the version is unchanged.
4. On conflict, it discards the computed mutation, reloads, and re-evaluates the transition from current state.

Tick reconcile must split long work into short transactions. It may record "dispatch started" before launching a process, release the lease while the process runs, and later reacquire the lease to consume exit evidence. CLI verbs use the same version rule. SQLite repair bypasses only through emergency commands that still append repair evidence.

## Incident Checks

The 2026-07-07/08 incidents should be tested against this model before implementation is accepted:

- Closed backlog items `eb71c8b8`, `64848d84`, and `a9126041` must map to either a legal transition or a logged repair, not a projection-only fix.
- Open/active incident items `c7344bf2`, `51022b21`, and `59698301` must identify the unowned field or transition they exposed and add the missing writer guard.
- Goal `694f8eae`, task `7084e833`, must not be able to double-rewind: once a dispatch exit artifact is consumed into verification/process/task status, a later tick may display it but cannot reapply it without an unconsumed marker.
- A goal with terminal `Goal.Status` and `Assigned`/`Running` tasks is invalid persisted state. The next tick or repair command must either reopen to `Active` or convert tasks to a justified terminal status in the same transaction.

## Implementation Requirements

- Add a single transition service or equivalent kernel boundary for every status write. Callers may be CLI verbs, dashboard actions, tick reconcile, classifier/refiner, or landing, but they must not each implement separate status logic.
- Make every persisted-field write produce a timeline or event-store entry in the same transaction.
- Add idempotency for dispatch artifact consumption before broadening automatic refresh/reconcile.
- Treat projections as read models. Tests should assert projections from snapshots, not write projected states back into snapshots.
- Add adversarial tests for illegal transitions, lease conflicts, stale terminal-with-work repair, waiting/escalation resume, and consumed dispatch artifacts.
