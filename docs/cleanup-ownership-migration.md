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

## Operator continuation: explicit build-storage ownership

`DotnetBuildStorageRoot` identifies an absolute normalized storage namespace; constructing it
does not create directories or acquire capacity. Build creation, stable-slot operations, lease
inspection, rotation, orphan cleanup, cache paths and successful-run cleanup can receive that
value. Default callers resolve the existing environment override once at the operation boundary.
Private path composition receives the resolved value rather than rereading ambient state.
The persisted build-environment and lease formats are unchanged.

Cleanup contexts carry an optional storage-root override. A sweep captures the default only
when the operation starts, then uses the same value for its cleanup-debt path and actual deletion.
The same-goal/two-root control proves removal in one namespace preserves the other's lease
bytes. Its ignored-context arm specifically rejects survival of the intended owning root;
damage to the other or ambient root is checked independently and cannot satisfy that control.
The held-artifact cleanup case now uses explicit ownership instead of mutating the parent
environment. This does not yet eliminate the collection's other environment mutations.

Build-permit acquisition derives sibling locks from the supplied execution-lock directory.
Independent Opus review identified a diagnostic regression in the first implementation:
probing the requesting goal's artifacts attributed every busy permit to one artifact consumer.
Both public acquisition routes reproduced that error with real held locks and a live child.
The correction restores per-slot diagnostic artifact paths within the same owned namespace;
it does not widen capacity or alter lock acquisition. The reviewed follow-up found no concrete
regression. Arbitrary externally fabricated descriptor layouts are outside the constructor
invariants exercised here; the full migration still must audit composition boundaries.

Evidence in the driving repository:

- `cleanup-owned-storage-remove-family`: 55/55 passed, owned exit zero, 806.994 seconds.
- `cleanup-storage-manager-family`: 20/20 passed before the diagnostic-only correction.
- `cleanup-permit-diagnostic-red-control`: both cases failed on the wrong owner PID, with
  an observed test-host exit and no timeout.
- `cleanup-storage-reviewed-lease-family`: 29/29 passed after the correction.
- `cleanup-storage-reviewed-stable-family`: 22/22 passed after the correction.
- `cleanup-storage-reviewed-owner-control`: both tightened ownership cases passed.

The last three controls use frozen test assembly SHA-256
`159B7DCC04B862364575F9F31D23ABCD09836182A6A3671ACD4DB8A0CBE395FD`,
with source/PDB identities recorded separately. The prior full removal-family run predates
the diagnostic correction and tightened assertion; it is not presented as a full run of the
final binary. No acceptance critical-path improvement or complete goal acceptance is claimed.

## Operator continuation: acceptance launch configuration

The acceptance coordinator now accepts an explicit `DotnetBuildStorageRoot` for its real
attempt and cohort acquisitions. The existing transient owned-process launch request carries
that setting to process-start construction. Both internal transport hops require a root argument;
callers must deliberately pass either the setting or null. The persisted attempt record is
unchanged. A storage setting selects execution configuration; it does not assert that a lease
was acquired or grant cleanup authority through on-disk attempt data.

An explicit setting is installed in the owned child's environment after the hermetic scrub.
The child uses its existing build and cleanup default-resolution paths within that namespace.
An unconfigured coordinator does not capture an ambient override into the launch request:
null preserves the existing parent operation defaults and child environment scrub. The launch
control checks this with an ambient collection root that differs from the explicit root.

The coordinator's complete build-manager call census is:

| Call | Namespace source |
| --- | --- |
| `AcquireFirstAvailableStableSlotExecutionLock` | Explicit coordinator setting, or existing default when null |
| `CreateAttempt` | The same coordinator setting, or existing default when null |
| `TryAcquireFirstAvailableBuildPermit` | The resulting environment's execution-lock directory; sibling permits preserve that directory |
| Timeout/count constants and environment-key name | No storage operation |

There is one production launch-request construction and one production start-info invocation;
both pass the explicit setting. The two pre-existing driver fallback coordinators inject a
null-lease delegate and do not configure a storage override; their behavior is unchanged.
CLI acceptance also calls the stable-slot availability/owner APIs; its explicit-root migration
is recorded below. The earlier manager-only survey missed that caller.
CLI abandonment/rotation and heartbeat/load-context projections are separate remaining audit
surfaces; this increment does not claim that every diagnostic projection supports an explicit
in-process namespace.

The two acceptance-contention controls now own temporary storage values without changing the
parent environment, preserving blocked/re-gate and concurrent complete-partition assertions.
The real owned-start success and legacy fast-exit negative fixtures also use explicit launch
configuration, with the normalized root used for construction, assertions and teardown. Their
manual child-root overwrite is removed. Parent isolation still comes from the existing
`DotnetBuildSlots` collection fixture; removing that fixture is outside this increment.

The final frozen test assembly is
`01F893E17C73425C9FF2394210E4813877AE33385CA6E38A8E2E31AF256E1ACB`.
Receipts are `cleanup-launch-required-*` under the driving repository's operator-evidence
directory. Independent Opus review prompted preservation of default scrubbing, use of the
existing transient launch carrier, explicit internal arguments and normalized fixture paths.
Full collection isolation, integrated acceptance and measured gate improvement remain open.

## CLI permit ownership and cleanup fixture responsibilities

The original removal fixture's 54 test methods / 55 cases were moved into seven classes by
responsibility. Member bodies were copied verbatim except the owned-child selector's containing
class; later diagnostic changes retain captured CLI failure output. Manifest discovery preserved
all 219 lane cases after this move. Two new CLI storage controls bring this family to 57 cases.
The existing prefix filter covers the moved classes without a manifest census edit.

Serial validation exposed a missed dependency before concurrency was enabled. One retained
five-role lifecycle failure reached CLI acceptance and returned `SLOTS_BUSY` on the ambient
build permits. The former collection fixture had supplied a process-global isolated root;
removing it exposed the CLI selector's ambient `CreateAttempt`, availability and owner calls.
Other failures without captured acceptance output retain an undetermined mechanism.

CLI acceptance now reads the existing immutable `CleanupContext.Hooks.BuildStorageRoot` for
all three calls. The resulting lease carries the verifier's environment. A custom selector may
still supply a lease, but an explicitly configured namespace rejects a lease whose environment,
artifacts or execution-lock path escapes it and releases that lease before failing. Membership
is lexical and does not resolve filesystem links; it is a consistency check, not authorization.
Persisted acceptance and acceptance-queue forward the same acceptance cleanup context through
the dispatcher, including acceptance's preflight sweep. Default callers retain their prior
configuration behavior. Other persistent-command/host migration surfaces remain open.

The storage family passed 7/7 on source-matched test assembly
`6DF4C2D7A351A6038F3514D3F92CB396980861369C30F0D15103C7FD9530D64B`:
an actual CLI control fills one isolated root's permits, observes its busy outcome, then lands
with an idle independent root and checks the verifier environment. A negative selector control
supplies another root's lease, requires the specific namespace rejection and verifies release.
Receipt: `cleanup-cli-root-storage-control` in the driving repository's operator evidence.
The lifecycle family passed 10/10 in 230.227 seconds, and the full 57-case family passed serially
in 786.747 seconds with seed 20260908 and four configured threads (parallelism disabled).
Both used the same frozen assembly above, with managed-host exit zero and confirmed teardown.
Fresh manifest discovery found 221 cases: all previous 219 plus exactly the two storage controls.
Independent Opus review cleared this bounded increment after inspecting instance hook ownership
and the explicit-root caller census. The matched four-thread collections run passed 57/57 in
381.623 seconds, against the same frozen assembly, roster and seed. Recorded test-result intervals
show maximum overlap of four cases versus one serially. This single pair reduced family elapsed
time by 51.49%; it does not establish internal-operation overlap or a full-gate improvement.
Receipt: `cleanup-cli-root-family-comparison.json` in the driving repository's operator evidence.

The first full 221-case manifest run under the Codex filesystem sandbox passed the migrated 57
cases; the other 164 could not enter their test bodies because the existing collection fixture
could not create its owned LocalLow claim file. One affected case passed on the unchanged frozen
assembly with the required filesystem access. The complete lane is being validated with that
access; neither the failed setup run nor that one passing control proves full-lane acceptance.
Full acceptance and complete collection isolation remain open. Host/heartbeat context projections
and wider child-environment propagation are outside this increment's evidence.
