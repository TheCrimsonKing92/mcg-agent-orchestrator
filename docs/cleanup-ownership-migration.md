# Cleanup ownership migration

This table is the completion inventory for cleanup ownership. A row remains open until its listed
legacy path has no production or test caller. `WorktreeCleanupContext` is immutable for one host or
command operation; `GoalWorktreeCleanupHooks` is immutable for one cleanup operation.

| Capability / state | Former semantics | Owner and lifecycle | Verification / migration state |
| --- | --- | --- | --- |
| All behavior hooks (build server through warning sink) | Mutable `GoalWorktrees` test seam, formerly read by live `Default` | Per-operation `GoalWorktreeCleanupHooks` | Production defaults are fresh immutable records; the legacy test seam is no longer observed by production and remains only until test migration |
| Command-line snapshot | Test-only mutable `ProcessCommandLineSnapshotForCleanupTests`, formerly read by live `Default` | Per-operation `ProcessCommandLineSnapshot` | Production uses `ProcessCommandLines.SnapshotByNames`; migrate lock-holder tests before static removal |
| Clocks and both backoffs | Mutable `GoalWorktrees` test seam, formerly read by live `Default` | Per-operation clock/backoff hook members; scheduler reads the same hook | Production defaults are fresh immutable records; migrate deterministic tests before static removal |
| Cleanup policy | `ConfigureCleanup` test compatibility writes process-wide options | `WorktreeCleanupContext` creates fixed `CleanupOptions` from `WorktreeCleanupConfiguration.Load(AppContext.BaseDirectory)` | Production configuration is fixed in the context; migrate configuration tests before static compatibility removal |
| Attention-store location | `ConfigureCleanup` test compatibility writes a normalized path | Fixed `CleanupAttentionStoreDirectory` in the owning context | Production configuration is fixed in the context; migrate configuration tests before static compatibility removal |
| Sweep policy and cadence | Static scheduler options plus directory-keyed last-sweep map | `GoalWorktreeOrphanSweepScheduler` instance in the owning context | Instance holds its own options, lock, and map; two-context isolation requires Acceptance evidence |
| Acceptance-cohort cleanup debt | `RecordAcceptanceCohortCleanupNeeded` selected live `Default` | Cohort, partition, and merge-train workspace carry the conductor's immutable hooks through materialization and disposal | Production conductor paths pass their owning hooks; direct/test callers retain a compatibility fallback until all test seams migrate |

## Production caller ownership

| Caller | Owning context | Current state |
| --- | --- | --- |
| Program startup and worker-process startup cleanup | Program-created context from `Load(AppContext.BaseDirectory)` and workspace orchestrator directory | Passes its scheduler explicitly |
| CLI command dispatch and cleanup status | One dispatcher-created context | Status reads `CleanupContext.Hooks` |
| CLI readiness, recovery, next, conduct, lifecycle, and subscription-ready terminal sweeps | The command's `CleanupContext` | Each terminal sweep receives the same explicit hooks as its scheduler |
| Persistent conduct startup, global reconcile, and acceptance preflight terminal sweeps | One locally loaded context per operation | Terminal sweep and cadence scheduler share that instance |
| Dashboard hosted sweep | DI singleton context loaded from the configured application base directory | Hosted service receives the singleton scheduler |
| Acceptance cohort, partition, and merge-train materialization | The conductor path selected `GoalWorktreeCleanupHooks.Default` during cleanup-debt recording | The conductor's `CleanupContext.Hooks` is carried by each disposable workspace | Production callers are migrated; nullable compatibility fallback remains for direct tests |
| Nullable cleanup-hook overloads and `TerminalGoalSweep.Run` fallback | Compatibility fallback for direct callers and tests | **Open:** remove only after all callers pass explicit hooks |

The `GoalWorktreeCleanupHooks` xUnit collection remains serialized because its fixture mutates the
parent-process `MCG_DOTNET_ISOLATED_ROOT` and cleanup tests still mutate the listed legacy statics.
It must remain until child-process environment and distinct git-root/process-registry isolation have
Acceptance receipts.
