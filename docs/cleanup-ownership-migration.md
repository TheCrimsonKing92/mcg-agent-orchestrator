# Cleanup ownership migration

This table records the required destination of every process-wide cleanup seam. It is deliberately
kept with the implementation so a caller cannot be migrated without an owner and verification.

| Capability / state | Former semantics | Owner and lifecycle | Verification |
| --- | --- | --- | --- |
| Cleanup options; attention-store path | `ConfigureCleanup` set process-wide values, with relative path normalized | `WorktreeCleanupContext` creates fixed `GoalWorktreeCleanupHooks` once per host operation | `WorktreeCleanupConfigurationTests` covers validation and normalization |
| Build-server shutdown; ACL reset; process termination | Mutable `GoalWorktrees` delegates via live `Default` | Explicit `GoalWorktreeCleanupHooks` carried by cleanup operation | Existing cleanup-hook tests; remaining static callers must migrate before deletion |
| Directory deletion (boolean and reasoned); worktree remove/prune | Mutable delegates via live `Default` | Explicit immutable hooks | Remove/sweep cleanup tests |
| Lock-holder lookup; process command-line snapshot | Mutable lookup and test snapshot seam | Explicit immutable hooks, including snapshot injection | Lock-holder cleanup tests |
| Warning sink; elapsed and UTC clocks | Mutable diagnostics/time seams | Explicit immutable hooks; scheduler reads the hook UTC clock | Cleanup warning and due-sweep tests |
| Ordinary and budget-exhausted backoffs | Process-wide durations | Explicit immutable hooks | Cleanup-needed recovery tests |
| Orphan-sweep options and last-sweep map | Static scheduler options and map | `GoalWorktreeOrphanSweepScheduler` instance owned by `WorktreeCleanupContext` | Scheduler construction and two-context isolation controls |
| Acceptance-cohort workspace remover | Static cohort seam | Cohort workspace operation context | Cohort cleanup tests |

Production caller ownership: Program startup owns one context; dashboard DI owns one hosted-service
context; command dispatch creates a fixed context for its command; conductor and reconcile sweeps
create an operation context. The remaining legacy static seam is retained only while affected tests
are converted to explicit hook records; it is not a production configuration path after this slice.
