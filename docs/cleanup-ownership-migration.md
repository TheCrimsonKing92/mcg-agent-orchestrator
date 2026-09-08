# Cleanup ownership migration

This table is the completion inventory for cleanup ownership. A row remains open until its listed
legacy path has no production or test caller. `WorktreeCleanupContext` is immutable for one host or
command operation; `GoalWorktreeCleanupHooks` is immutable for one cleanup operation.

| Capability / state | Former semantics | Owner and lifecycle | Verification / migration state |
| --- | --- | --- | --- |
| Build-server shutdown | Mutable `GoalWorktrees.BuildServerShutdown`, read by live `Default` | Per-operation `GoalWorktreeCleanupHooks.BuildServerShutdown` | Explicit hook injection; legacy static remains for unmigrated tests |
| ACL reset | Mutable `SandboxAclHelper`, read by live `Default` | Per-operation `ResetSandboxAcl` | Explicit hook injection; legacy static remains for unmigrated tests |
| Recorded-process termination | Mutable `TryKillRecordedProcess`, read by live `Default` | Per-operation `TryKillRecordedProcess` | Explicit hook injection; legacy static remains for unmigrated tests |
| Boolean directory deletion | Mutable `DeleteDirectory`, read by live `Default` | Per-operation `DeleteDirectory` | Explicit hook injection; legacy static remains for unmigrated tests |
| Reason-preserving directory deletion | Mutable `DeleteDirectoryForCleanup`, read by live `Default` | Per-operation `DeleteDirectoryForCleanup` | Explicit hook injection; legacy static remains for unmigrated tests |
| Worktree removal | Mutable `RunWorktreeRemove`, read by live `Default` | Per-operation `RunWorktreeRemove` | Explicit hook injection; legacy static remains for unmigrated tests |
| Worktree prune | Mutable `RunWorktreePrune`, read by live `Default` | Per-operation `RunWorktreePrune` | Explicit hook injection; legacy static remains for unmigrated tests |
| Lock-holder lookup | Mutable `FindLockHoldersForCleanup`, read by live `Default` | Per-operation `FindLockHoldersForCleanup` | Explicit hook injection; legacy static remains for unmigrated tests |
| Command-line snapshot | Test-only mutable `ProcessCommandLineSnapshotForCleanupTests`, read by live `Default` | Per-operation `ProcessCommandLineSnapshot` | Hook member exists; migrate the two lock-holder tests before static removal |
| Cleanup warning sink | Mutable `CleanupWarningSink`, read by live `Default` | Per-operation `CleanupWarningSink` | Explicit hook injection; `ConductorBatchLoopTestsLoopSchedulingPolicy` remains an out-of-collection mutator |
| Elapsed clock | Mutable `CleanupElapsedMilliseconds`, read by live `Default` | Per-operation `CleanupElapsedMilliseconds` | Explicit hook injection; legacy static remains for unmigrated tests |
| UTC clock | Mutable `CleanupUtcNow`, read by live `Default` | Per-operation `CleanupUtcNow`; scheduler reads the same hook | Explicit hook injection; legacy static remains for unmigrated tests |
| Ordinary cleanup backoff | Mutable `CleanupBackoffDuration`, read by live `Default` | Per-operation `CleanupBackoffDuration` | Explicit hook injection; legacy static remains for unmigrated tests |
| Budget-exhausted backoff | Mutable `CleanupBudgetExhaustedBackoffDuration`, read by live `Default` | Per-operation `CleanupBudgetExhaustedBackoffDuration` | Explicit hook injection; legacy static remains for unmigrated tests |
| Cleanup policy | `ConfigureCleanup` wrote process-wide options | `WorktreeCleanupContext` creates fixed `CleanupOptions` from `WorktreeCleanupConfiguration.Load(AppContext.BaseDirectory)` | Validation and configured-hook normalization are covered by `WorktreeCleanupConfigurationTests`; delete `ConfigureCleanup` after callers migrate |
| Attention-store location | `ConfigureCleanup` wrote a normalized process-wide path | Fixed `CleanupAttentionStoreDirectory` in the owning context | Relative-path normalization is covered by `WorktreeCleanupConfigurationTests`; delete static path after callers migrate |
| Sweep policy and cadence | Static scheduler options plus directory-keyed last-sweep map | `GoalWorktreeOrphanSweepScheduler` instance in the owning context | Instance holds its own options, lock, and map; two-context isolation requires Acceptance evidence |
| Acceptance-cohort cleanup debt | `RecordAcceptanceCohortCleanupNeeded` hardcodes live `Default` | Cohort workspace operation must receive explicit hooks | **Open:** thread hooks through the cohort API before removing `Default` |

## Production caller ownership

| Caller | Owning context | Current state |
| --- | --- | --- |
| Program startup and worker-process startup cleanup | Program-created context from `Load(AppContext.BaseDirectory)` and workspace orchestrator directory | Passes its scheduler explicitly |
| CLI command dispatch and cleanup status | One dispatcher-created context | Status reads `CleanupContext.Hooks` |
| CLI readiness, recovery, next, conduct, lifecycle, and subscription-ready terminal sweeps | The command's `CleanupContext` | Each terminal sweep receives the same explicit hooks as its scheduler |
| Persistent conduct startup, global reconcile, and acceptance preflight terminal sweeps | One locally loaded context per operation | Terminal sweep and cadence scheduler share that instance |
| Dashboard hosted sweep | DI singleton context loaded from the configured application base directory | Hosted service receives the singleton scheduler |
| Acceptance cohort failed-materialization path | No scoped context reaches this API | **Open:** it still selects `GoalWorktreeCleanupHooks.Default` |
| Nullable cleanup-hook overloads and `TerminalGoalSweep.Run` fallback | Compatibility fallback for direct callers and tests | **Open:** remove only after all callers pass explicit hooks |

The `GoalWorktreeCleanupHooks` xUnit collection remains serialized because its fixture mutates the
parent-process `MCG_DOTNET_ISOLATED_ROOT` and cleanup tests still mutate the listed legacy statics.
It must remain until child-process environment and distinct git-root/process-registry isolation have
Acceptance receipts.
