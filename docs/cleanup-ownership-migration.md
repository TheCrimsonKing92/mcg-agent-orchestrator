# Cleanup ownership migration

This table is the completion inventory for cleanup ownership. A row remains open until its listed
legacy path has no production or test caller. `WorktreeCleanupContext` is immutable for one host or
command operation; `GoalWorktreeCleanupHooks` is immutable for one cleanup operation.

| Capability / state | Former semantics | Owner and lifecycle | Verification / migration state |
| --- | --- | --- | --- |
| `BuildServerShutdown` | Mutable static delegate | Immutable hook for one cleanup operation | Production uses the fixed record default; test writers remain in the two large cleanup families |
| `ResetSandboxAcl` | Mutable `SandboxAclHelper` static | Immutable hook for one cleanup operation | Production uses the platform-specific fixed record default; test writers remain in the two large cleanup families |
| `TryKillRecordedProcess` | Mutable static delegate | Immutable hook for one cleanup operation | Production uses the fixed record default; test writers remain in `GoalWorktreeTestsRemoveCleanup` |
| `DeleteDirectory` | Mutable static delegate | Immutable hook for one cleanup operation | Production uses `DeleteDirectoryWithRetry`; test writers remain in the two large cleanup families |
| `DeleteDirectoryForCleanup` | Mutable static delegate | Immutable hook for one cleanup operation | `TerminalGoalSweep` and cleanup APIs accept the owned hook; loop-policy test migrated in this change |
| `RunWorktreeRemove` | Mutable static delegate | Immutable hook for one cleanup operation | Production uses `DefaultRunWorktreeRemove`; test writers remain in `GoalWorktreeTestsRemoveCleanup` |
| `RunWorktreePrune` | Mutable static delegate | Immutable hook for one cleanup operation | Production uses `DefaultRunWorktreePrune`; test writers remain in `GoalWorktreeTestsRemoveCleanup` |
| `FindLockHoldersForCleanup` | Mutable static delegate | Immutable hook for one cleanup operation | Production derives the default from the record's snapshot hook; test writers remain in the two large cleanup families |
| `ProcessCommandLineSnapshot` | Mutable `ProcessCommandLineSnapshotForCleanupTests` static | Immutable hook for one cleanup operation | Added to the hook record; production uses `ProcessCommandLines.SnapshotByNames`; lock-holder tests already inject it |
| `CleanupWarningSink` | Mutable static delegate | Immutable hook for one cleanup operation | CLI/status and terminal sweep receive it from their context; loop-policy test migrated, while landing and cleanup-family writers remain |
| `CleanupElapsedMilliseconds` | Mutable static delegate | Immutable hook for one cleanup operation | Production uses no elapsed override; deterministic cleanup-family writers remain |
| `CleanupUtcNow` | Mutable static delegate | Immutable hook for one cleanup operation | Scheduler and state cleanup read the operation hook; CLI status test migrated, cleanup-family writers remain |
| `CleanupBackoffDuration` | Mutable static value | Immutable hook for one cleanup operation | CLI status test migrated; deterministic cleanup-family writers remain |
| `CleanupBudgetExhaustedBackoffDuration` | Mutable static value | Immutable hook for one cleanup operation | Production uses the fixed one-minute record default; deterministic cleanup-family writers remain |
| `CleanupOptions` | `ConfigureCleanup` changed process-wide policy | `WorktreeCleanupContext` owns a validated fixed record value | Context is built from `WorktreeCleanupConfiguration.Load(AppContext.BaseDirectory)`; configuration-family writers remain |
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
parent-process `MCG_DOTNET_ISOLATED_ROOT` and cleanup tests still mutate the listed legacy statics.
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
