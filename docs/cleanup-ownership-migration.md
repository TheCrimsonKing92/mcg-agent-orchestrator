# Cleanup ownership migration

This table is the completion inventory for cleanup ownership. A row remains open until its listed
legacy path has no production or test caller. `WorktreeCleanupContext` is immutable for one host or
command operation; `GoalWorktreeCleanupHooks` is immutable for one cleanup operation.

| Capability / state | Former semantics | Owner and lifecycle | Verification / migration state |
| --- | --- | --- | --- |
| `BuildServerShutdown` | Mutable static delegate | Immutable hook for one cleanup operation | Production uses the fixed record default; test arrangements now build independent fixed records in both cleanup families |
| `ResetSandboxAcl` | Mutable `SandboxAclHelper` static | Immutable hook for one cleanup operation | Production uses the platform-specific fixed record default; test arrangements now build independent fixed records in both cleanup families |
| `TryKillRecordedProcess` | Mutable static delegate | Immutable hook for one cleanup operation | Production uses the fixed record default; removal tests now inject independent fixed records |
| `DeleteDirectory` | Mutable static delegate | Immutable hook for one cleanup operation | Production uses `DeleteDirectoryWithRetry`; test arrangements now build independent fixed records in both cleanup families |
| `DeleteDirectoryForCleanup` | Mutable static delegate | Immutable hook for one cleanup operation | `TerminalGoalSweep` and cleanup APIs accept the owned hook; loop-policy and cleanup-family callers pass owned records |
| `RunWorktreeRemove` | Mutable static delegate | Immutable hook for one cleanup operation | Production uses `DefaultRunWorktreeRemove`; removal tests now inject independent fixed records |
| `RunWorktreePrune` | Mutable static delegate | Immutable hook for one cleanup operation | Production uses `DefaultRunWorktreePrune`; removal tests now inject independent fixed records |
| `FindLockHoldersForCleanup` | Mutable static delegate | Immutable hook for one cleanup operation | Production derives the default from the record's snapshot hook; test arrangements now build independent fixed records in both cleanup families |
| `ProcessCommandLineSnapshot` | Mutable `ProcessCommandLineSnapshotForCleanupTests` static | Immutable hook for one cleanup operation | **Migrated:** the unused static seam is removed; production uses `ProcessCommandLines.SnapshotByNames` and lock-holder tests inject the fixed hook |
| `CleanupWarningSink` | Mutable static delegate | Immutable hook for one cleanup operation | CLI/status and terminal sweep receive it from their context; cleanup-family callers inject owned sinks, and the landing-order test observes durable debt after state commits |
| `CleanupElapsedMilliseconds` | Mutable static delegate | Immutable hook for one cleanup operation | Production uses no elapsed override; deterministic cleanup-family arrangements now snapshot the value into an owned record |
| `CleanupUtcNow` | Mutable static delegate | Immutable hook for one cleanup operation | Scheduler and state cleanup read the operation hook; CLI status and recovery reports forward their context, and cleanup-family arrangements use owned records |
| `CleanupBackoffDuration` | Mutable static value | Immutable hook for one cleanup operation | CLI status test migrated; deterministic cleanup-family arrangements now snapshot the value into an owned record |
| `CleanupBudgetExhaustedBackoffDuration` | Mutable static value | Immutable hook for one cleanup operation | Production uses the fixed one-minute record default; deterministic cleanup-family arrangements now snapshot the value into an owned record |
| `CleanupOptions` | `ConfigureCleanup` changed process-wide policy | `WorktreeCleanupContext` owns a validated fixed record value | Context is built from `WorktreeCleanupConfiguration.Load(AppContext.BaseDirectory)`; configuration arrangements are per-test builder state snapshotted by Build |
| `CleanupAttentionStoreDirectory` | `ConfigureCleanup` changed a process-wide normalized path | `WorktreeCleanupContext` owns a normalized fixed record value | Program binds the workspace orchestrator directory; no production fallback to configuration statics |
| Scheduler `Options`, gate, and `LastSweepByDirectory` | Static policy and process-wide directory cadence map | `GoalWorktreeOrphanSweepScheduler` instance in the context | Instance owns its lock and map; Acceptance must exercise two-context isolation |
| Acceptance-cohort cleanup debt | `RecordAcceptanceCohortCleanupNeeded` selected live `Default` | Cohort, partition, and merge-train workspace carry the conductor's immutable hooks through materialization and disposal | Production conductor paths pass their owning hooks; direct/test callers retain a compatibility fallback until all test seams migrate |

## Production caller ownership

| Caller | Owning context | Current state |
| --- | --- | --- |
| Program startup and worker-process startup cleanup | Program-created context from `Load(AppContext.BaseDirectory)` and workspace orchestrator directory | Passes its scheduler explicitly |
| CLI command dispatch and cleanup status | One dispatcher-created context | Status reads `CleanupContext.Hooks` |
| CLI readiness, recovery, next, conduct, lifecycle, and subscription-ready terminal sweeps | The command's `CleanupContext` | Each terminal sweep receives the same explicit hooks as its scheduler |
| Persistent conduct startup, global reconcile, and acceptance preflight terminal sweeps | One locally loaded context per operation | Terminal sweep and cadence scheduler share that instance |
| Dashboard hosted sweep | DI singleton context loaded from the configured application base directory | Hosted service receives the singleton scheduler |
| Acceptance cohort, partition, and merge-train materialization | The conductor previously selected a process-wide cleanup policy during cleanup-debt recording | The conductor's `CleanupContext.Hooks` is carried by each disposable workspace | Production callers pass their operation hook; nullable overload fallbacks construct fixed defaults only for direct callers and tests |
| Nullable cleanup-hook overloads and `TerminalGoalSweep.Run` fallback | Compatibility fallback for direct callers and tests | **Open:** remove only after all callers pass explicit hooks |

The `GoalWorktreeCleanupHooks` xUnit collection remains serialized because its fixture mutates the
parent-process `MCG_DOTNET_ISOLATED_ROOT`; individual cases also retain process-wide environment and registry effects.
It must remain until child-process environment and distinct git-root/process-registry isolation have
Acceptance receipts.

## Parked-branch reassessment

The unlanded `c5c6640258a4e12eb854d78381dfb3238d5dd0fc` migration cannot be carried forward
as-is. Its test builder derives from `GoalWorktreeCleanupHooks.Default`, which was a live adapter
over the mutable static seams. The current record has fixed defaults and `ForConfiguration` creates
an explicitly validated, normalized operation value. Reintroducing the old adapter would make an
explicitly injected test hook observe another test's mutation and would regress this ownership
migration. Any future mechanical test migration must build independent fixed records and pass them
through the owning operation; it must not restore a global compatibility source.

## Operator continuation: public cleanup paths

`CliWorkspaceRemoveForceTerminalCleanupBypassesEscalatedBackoff` now constructs a
fixed deferring record and a separate recovering record, then passes the latter
through `CliExecutionContext.CleanupContext`. Recovery restores only the real
delete delegate; ACL, build-server shutdown and lock-holder discovery remain
suppressed in both phases, preserving the original test's operating conditions.
The existing escalation, backoff, directory-removal and attention-clear assertions
remain in place.

`RemoveTerminalKeepsConcurrentPublicOperationsAndAttentionStoresIsolated` makes
two public `RemoveTerminal` operations rendezvous inside their independent deletion
hooks. It verifies separate invocation paths, backoff and attention stores, and
goal attribution. This proves overlapping operations through the public cleanup
entry point; it does not establish parallel safety of the entire test collection.

The operator's build and focused control receipts are under
`.orchestrator/operator-evidence/rearchitecture-20260906/cleanup-public-context-preserved-*`
in the driving repository. The frozen test assembly's PDB matches the modified
source. Independent Opus review `cleanup-cohort-contract-review.json` identified the
recovery suppression mistake above before this increment was committed.

Remaining completion work after the hook migration:
remove parent environment mutation, prove the whole collection's isolation, remove
its unnecessary serialization, and measure the resulting acceptance critical path.
Full-class and collection evidence belongs after that migration; these two focused
controls are not a completion or acceptance verdict for this goal.

## Operator continuation: fixed hook caller migration

The mutable cleanup static members and `ConfigureCleanup` are removed. Cleanup tests use
one arrangement builder per xUnit instance; `Build` captures delegate identities, ACL adapter,
elapsed-time factory, durations, options and attention directory into an independent record.
Further builder arrangement cannot change an already-built record. Defaults come directly
from a fresh production hook record, including real shutdown, lock discovery and platform ACL
behavior. Generic fixture deletion uses `DeleteDirectoryWithRetry` directly.

Public-path migration includes the capturing worktree service, conductor cleanup-debt recording,
CLI acceptance cleanup and recovery reports. The acceptance failure test proves its injected
delete callback ran before asserting the blocker. The conductor test proves cleanup-needed
recording occurred while deletion and lock-holder probes stayed at zero. The landing-order
observer now records the awaited goal-state transaction commit as well as snapshot saves.

Independent Opus reviews found dropped hook injections, unrelated fixture changes and incomplete
transaction observation; those findings were corrected. Operator receipts include
`cleanup-reviewed-orphan-family` (16/16), `cleanup-observation-recovery-control` (1/1), and
`cleanup-observation-landing-order-control` (1/1), and `cleanup-observation-remove-family` (51/51 in 354.572 seconds, owned exit zero, no timeout). Source identity is recorded separately; these
are affected-path evidence, not full acceptance or proof of a shorter acceptance critical path.

The composition audit still must cover dashboard recommendation, health, triage and historical
recovery reports that currently construct fixed defaults. Recovery and supervisor paths that
explicitly disable cleanup-backoff reading do not require a clock for that disabled read.
Parent environment/CWD/registry isolation, removal of justified-only-after-isolation collection
constraints, whole-family integration acceptance and comparable gate timings remain open.