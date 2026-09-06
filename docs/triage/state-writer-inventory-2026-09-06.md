# State writer inventory for the runtime migration

Inspected against `fe0923a3b7789dc5d8f259a0a1078cb0a533bf39`. This is a migration input, not a change to write authority. The [system audit](system-architecture-audit-2026-09-06.md) separates persistent hosting from the choice of one or several writers.

## Current owners and protocols

| Owner / entry point | Current protection | Migration consequence |
|---|---|---|
| Generic CLI command, `CliPersistentStateRunner.ExecuteCommand` at 287/343 | Whole-kernel transaction, optionally atomic state/outbox | Split explicit queries first. Mutations need equivalent version/recompute and state/effect-intent atomicity before shortening the transaction. |
| Conductor tick checkpoints, same file at 995/1035 | Goal snapshot baseline comparison, delta merge or conflict rejection; durable result rebases the working kernel | Preserve the critical dispatch `RejectConflict` path. A returned merged snapshot is authoritative; the stale tick kernel is not. |
| Goal-scoped task mutation at 1973, process refresh at 3867, single-goal persistence at 4716 | `TransactGoalStateAsync`, including human-input state and goal-version retries | Reuse this protocol where human-input state belongs to the transition. Do not replace it with a goal-only write that loses the associated records. |
| Post-creation changes at 3794, acceptance result at 4038, merge guard at 4175 | Goal-only optimistic transaction | Keep callback computation free of external effects: the callback can run again after a conflict. |
| Goal create/replacement at 3535/2321 | Whole-kernel transaction with atomic outbox | Extract command ownership without splitting state and required delivery intent into separate commits. |
| Backlog intake/acceptance queue checkpoints at 2079/4236; generic nontransactional fallback at 1745 | Repository `SaveAsync` | Still separate mutation paths; a headless host does not automatically retire them. Inventory callers and their baselines before changing semantics. |
| Dashboard `DashboardStateService`, `DashboardEndpointServices.cs` at 23–214 | Per-process semaphore plus repository transaction when supported; full load/save fallback otherwise | Semaphore ownership does not fence the CLI or runtime. Extract application operations and their storage protocol; leave HTTP adaptation in the dashboard. |
| `GoalRefinementWorkCoordinator` at 178/336 | Goal transaction for attachment, whole-kernel outbox transaction for another update path | Preserve revision/attachment checks and atomic publication intent during application extraction. |
| Project initialization and demo seeding | Direct repository save into their selected workspace | Creation/seeding is distinct from ongoing workflow ownership. Verify workspace identity and initialization guards before allowing these into a persistent runtime composition. |
| SQLite operator utility: `set-goal-status` and `requeue-task` | Reads and constructs snapshots before acquiring `BEGIN IMMEDIATE`; writes by id, increments version without comparing the read version | Confirmed lost updates for both verbs; fix tracked as `9829af20`. A supported offline applier must protect the entire read/derive/write operation and preserve direct recovery. |
| Database maintenance/conversion | Separate maintenance contracts in `StateDatabaseMaintenance`, `StateDatabaseOfflineConversion` and `SqliteMaintenanceContracts` | Preserve their fencing/schema rules. Do not mistake their existence for an offline workflow-command applier. |

Core/Infrastructure retain their flat public namespaces. These ownership boundaries do not require a separate database or assembly for every row.

## What live receipts establish

The five `operator-rearchitecture-*.out.log` files with merge receipts in this run contain 18 `TICK_MERGE` lines: 16 `MERGED` and two `SKIPPED` critical-dispatch conflicts. Current `FormatTickMergeReceipt` emits goal, disposition and a reason. The merged cases all say a tick delta was reapplied to a fresh row; they do not identify changed fields, a competing writer or the decision that was recomputed. This sample establishes that the merge path is exercised, and that critical conflict rejection is exercised. It cannot rank conflicting fields or justify a sole-writer decision. There is no denominator here for a conflict rate.

The recovery-utility experiment is a different, controlled result. With the same actual utility and owned synthetic WAL databases, an already-committed unrelated objective survived the serialized arm. In the overlapping arm, another connection held the writer while the utility read the older committed snapshot, then committed before the utility acquired its transaction. The utility exited zero, advanced the version and restored the older snapshot objective, leaving it inconsistent with the indexed objective. Both `set-goal-status` and `requeue-task` reproduced this behavior independently. Raw identities, paired outcomes and the reproducer are retained under `.orchestrator/operator-evidence/rearchitecture-20260906/` in `maintenance-writer-probe/`, `maintenance-requeue-probe/` and `Probe-MaintenanceWriter.py`. No live state was changed.

## Decision boundary

Continue the current migration with explicit read capabilities and goal-scoped, versioned transitions. Preserve the existing intent inbox and direct offline operations while their respective protocols are hardened. Publish and supervise a headless runtime independently of this authority choice.

Before expanding inbox-only recovery, the implementation must provide a maintenance applier that can acquire and prove exclusive authority, apply supported commands with the runtime down, and release authority safely. Before declaring multiple writers the permanent design, every writer above must obey the same relevant version, replay and atomicity rules. Neither target is complete today. Add attributed changed-field/merge evidence to the existing receipts and evaluate the concrete ownership-transfer cost; do not decide by counting these 18 generic log lines.
