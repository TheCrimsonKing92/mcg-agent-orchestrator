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
assembly with the required filesystem access. The complete lane subsequently passed 221/221 in
2365.314 seconds with that access, owned host exit zero and no timeout. Receipt:
`cleanup-cli-root-manifest-authorized-control`. This validates that lane on its frozen candidate;
it does not prove full acceptance or validate subsequent source changes.
Full acceptance and complete collection isolation remain open. Host/heartbeat context projections
and wider child-environment propagation are outside this increment's evidence.

## CLI worktree service ownership and shutdown reachability

A bounded diagnostic run replaced only `DefaultBuildServerShutdown`'s body with a record/no-op.
The actual 57-case family passed, recording nine default-callback invocations across eight tests.
This establishes callback reachability, not a cause for any gate failure. In particular, the real
force-terminal CLI test supplied a shutdown no-op but still reached the default callback. The
default CLI worktree adapter omitted its caller's cleanup hooks. Existing filesystem assertions
passed despite this omission.

`CliExecutionContext.Worktrees` now resolves an explicitly context-bound default adapter unless
the caller injects a custom service. The getter does not cache: the adapter reads the final
init-only `CleanupContext`. `Ensure`, timed and untimed `Remove`, and `RemoveTerminalNow` forward
that context's hooks. The force-terminal command reaches `RemoveTerminalNow` through the existing
workspace handler. No production assignment copies a `Worktrees` adapter into another context.
The adapter is internal, and the only additional former singleton reference was the test
acceptance-evidence forwarder.

The regression cases observe callback ownership through both explicit roots, normal and timed
removal, orphan creation recovery, and the actual forced-terminal CLI command. Orphan creation
uses the typed `DeleteDirectoryForCleanup` hook; ordinary removal uses `DeleteDirectory`.
Seven other directly invoked cleanup fixtures explicitly record shutdown requests while preserving
their existing directory, branch, debt, budget and recovery assertions. Those seven fixture
changes isolate test effects; they are not regressions for the CLI adapter omission. Default
production shutdown behavior remains present. Callback path equality is not proof that the real
SDK shutdown is scoped to a worktree: it invokes `dotnet build-server shutdown`, and its working
directory alone establishes no SDK-server ownership. Backlog `623883f6` retains this broader issue.

The first normal build passed; its 60-case family run completed 58 passed and two failed in
564.845 seconds. Both failures were in new tests: creation recorded the wrong deletion hook, and
an operator-selected 5000ms timed-removal budget returned a branch-kept result. The first test
was corrected to observe the typed hook. The cause of the second result remains undetermined;
the ownership test now exercises the timed overload with the repository's normal cleanup budget,
without changing production budgets or claiming a latency improvement. Both owner contexts now
perform actual removal and assert that each recorder receives only its own path.

Normal and negative runtimes are frozen independently. The negative control restores the four
adapter hook omissions while replacing only the default SDK shutdown effect with a no-op;
production source is then restored byte-for-byte. The expected failure set is the four adapter
regression cases, with the remaining nine workspace-command cases passing. Both arms compile
identical regression source; six PDB/source checks establish normal and mutated production
identity plus the matching test source (`cleanup-service-source-comparison.json`). Diagnostic
and negative binaries are excluded from acceptance.

The final normal arm passed 13/13 in 278.063 seconds; the negative arm passed nine and failed
exactly the four expected cases in 277.734 seconds. Every negative failure was the empty caller
recorder assertion, not a setup, timeout or branch assertion. Managed hosts 34444 and 52904
confirmed exit with codes zero and two respectively, with no timeout. The test assembly itself
was identical in both arms (`89D81643F14B6064D3DCE39A8D123903E8FF0638D0D6E70B15A41E26A27D317B`),
and the verifier confirmed the same 13-case roster. Receipts: `cleanup-service-owner-paired-results.json`
and `cleanup-service-source-comparison.json`. These are discriminating CLI ownership results;
they are not a fresh 60-case family pass or full acceptance. Full goal completion remains unproven.

## Acceptance evidence uses the execution owner's storage root

The context-bound CLI adapter now passes its build-storage root to the acceptance evidence
builder. The builder requires this argument; an explicit null retains legacy ambient resolution.
Previously the CLI could execute against its configured root while reporting a missing ambient
root. This was a diagnostic projection defect, not evidence of an incorrect gate verdict.

The real CLI storage-isolation case now checks that the reported root is present and equals the
verifier's execution root while retaining its busy alternate-root and ambient-absence checks.
The seven storage cases and the legacy-null provenance case passed (8/8). Both managed hosts
exited with code zero, confirmed exit, and no timeout. Four PDB/source checks match the changed
builder, adapter, and test files. Test assembly SHA256:
`0CE1CDE121BCA08C2EE4CC26A6C8A911D5808664DC5C79AC67B0A6A1FE1F522B`.
Receipts are `cleanup-evidence-root-storage-control/receipt.json` and
`cleanup-evidence-root-provenance-control/receipt.json` under the operator evidence directory.
Anthropic review informed the required argument and whitespace-independent root-line check.
Its inference that post-verification reporting cannot affect completion latency was incorrect:
reporting remains inside that wait. No causal connection to the separate synchronization failure
has been established, and this patch does not change its deadlines or claim to resolve it.

## Verification-phase database probe owns its lifetime

The acceptance concurrency fixture now invokes `CliPersistentStateRunner.ExecuteCommand`, the
entry point that owns acceptance transaction routing. The old fixture manually built a context
and called the lower-level handler. It started acceptance on a background task and could dispose
events and delete the repository after an assertion failed while that task was still active.

The verifier callback now performs the independent-connection read and write synchronously,
captures their exceptions and durations, and returns only after they finish. Assertions run
outside production verifier exception handling. The original one-second read/write bounds and
real merged-file assertion remain. Unrelated five-second entry and ten-second completion waits
are removed with their events and background task; the managed test host bounds total execution.
`LoadAsync` opens a connection and `SaveAsync` always acquires `BEGIN IMMEDIATE`, including for
an unchanged snapshot. Their implementation has no repository-instance serialization lock, so
the reviewer's hypothetical same-instance deadlock is not supported by the inspected source.

The normal focused case passed: read 7.0224ms, write 4.6071ms, actual acceptance merge observed.
A separately frozen production mutant holds an immediate transaction on the state database only
across the actual verifier invocation, disposing it before subsequent acceptance persistence.
With the identical test DLL, read succeeded in 35.3403ms; write failed in `BeginWriteAsync` with
SQLite error 5 after 33913.0234ms. The post-callback `Assert.Null(writeFailure)` failed as intended,
and the actual merge still completed. Both hosts exited with confirmed codes zero/two and no
host timeout. This demonstrates writer exclusion at the real verification seam, not a cause for
the earlier distinct startup/completion timing failures.

The production source was restored byte-for-byte and normal App outputs rebuilt. Normal and
mutated App PDBs match their respective sources. Shared test DLL SHA256:
`77D8755DE9DED70AECEED257AAA95749A31C5EE20695DB16969AD60294D6D329`.
Receipts: `cleanup-lock-paired-results.json`, `cleanup-lock-normal-app-source.json`,
`cleanup-lock-negative-app-source.json`, and `cleanup-lock-callback-source.json` under the operator
evidence directory. `Verify-CleanupLockPair.ps1` checks the paired outcome and source restoration.
The normal acceptance/landing family subsequently passed 36/36 in 565.800 seconds, with host
35048 exit zero confirmed and no timeout (`cleanup-lock-callback-landing-family/receipt.json`).
The separately validated rebase/merge root migration passed 31/31 in 760.774 seconds
(`cleanup-explicit-root-rebase-control/receipt.json`); no rebase source changed after that run.
These family receipts close the previous 35/36 family result at this checkpoint. They do not prove
whole-collection isolation, the remaining caller migration, full acceptance, or the performance goal.

## Alternate CLI entry points and remaining fixture-root fallback

Five additional fixture calls now pass an explicit cleanup context: persistent conduct for a
completed goal, workspace rebase, policy-blocked acceptance, and two backlog read/help dispatcher
calls. These use the existing `acceptanceCleanupContext` or `cleanupContext` parameter. The
sanctioned build passed. Actual conduct/landing and rebase controls passed (58.692s and 27.233s),
with confirmed clean host exits and no timeout. Both changed test files match the frozen test DLL
`18917FD52CB569DC82550A74B6C4EDE2CC64D8E4E017AA11DCA48F708FD384B2`.
Receipts: `cleanup-entrypoints-conduct-control` and `cleanup-entrypoints-rebase-control`.
These are focused checks after the five-call change; the earlier 36/31 family receipts remain
qualified to their prior checkpoint.
The two backlog calls and policy-blocked call were build-verified after this change; they await
the next affected-family run. Anthropic review found no concrete blocker for this checkpoint.

At this checkpoint, the base removal and sweep helpers still permitted a null `BuildStorageRoot`;
the repair and its verification are recorded below. The remaining environment-mutating fixtures
and collection-owned environment baseline retain their guards. Async console capture is already local to the async context; its implementation
does not reassign `Console.Out` per test and is not evidence for adding another serialization guard.

## Repository-bound fixture cleanup helpers

The seven base helpers that receive an execution directory now preserve an explicit storage-root
override or derive the existing fixture root from that directory. The path-only backoff lookup
remains unchanged: its argument is an arbitrary cleanup path, and backoff keys do not use build
artifact storage. Hooks are immutable snapshots; the record copy changes only `BuildStorageRoot`.
`CreateIsolatedCleanupContext` combines and normalizes paths without creating directories. Its
context and scheduler are non-disposable managed objects; scheduler construction validates policy
and allocates instance state but starts no work and registers nothing globally. Discarding that
unused scheduler therefore holds no external resource. Repeated construction derives the same root.

Two new storage regressions use actual artifact cleanup. With the same goal ID in independent
repositories, cleaning the first removes its artifacts and leaves the second lease metadata
byte-for-byte intact; cleaning the second then removes its artifacts. The override control deletes
the explicitly selected root's artifacts while preserving the fixture-default lease metadata.
Before the helper repair, the first test failed because the first artifact directory survived even
though Remove reported completion; the override control passed. Both pass after the repair with
identical new-test source. The red failure precedes the second-root assertion, so it does not itself
prove preservation of that other root.

The complete removal family passed 62/62 (60 existing plus two new), 314.193 seconds. The orphan
and ephemeral sweep family passed 16/16, 60.977 seconds. Both managed hosts exited zero with
confirmed exit and no host timeout. The first orphan attempt failed all 16 tests during collection
fixture construction because the operator sandbox denied its LocalLow claim file; no test body
ran. The identical frozen runtime passed with that filesystem access. This is an observed access
failure, not a product-test failure or a contention diagnosis.

The current runtime hash is
`51116149D57DF894517DA5452D79DA770D879B351A1BF3BE2BDC8748DE2677E0`.
Evidence under `.orchestrator/operator-evidence/rearchitecture-20260906`:
`cleanup-helper-root-red-control`, `cleanup-helper-root-green-family`,
`cleanup-helper-root-green-orphan`, `cleanup-helper-root-green-orphan-host-access`, and the
red/green source-checksum reports. Anthropic reviewed the actual diff in
`cleanup-helper-root-final-review-network.json`; its lifetime and coverage conditions were checked
against the definitions and executions above. The first review transport returned ConnectionRefused;
the subsequent authorized network call completed normally.

The inheritance inventory contains 21 classes. This checkpoint executes the seven RemoveCleanup
classes and OrphanEphemeralSweep. The other 13 are AcceptanceCohortWorkflowTests,
GoalWorktreeReducedFixtureTests, GoalWorktreeTestsAcceptanceRetry, GoalWorktreeIsolatedDotnetTests,
GoalWorktreeAcceptanceContentionTests, and GoalWorktreeTests{CreationResolution,
RebaseMergeMaterialization,GitHelperDiagnostics,AcceptanceLanding,RebaseMerge,SeedIsolation,
SqliteTooling}, plus GoalWorktreeTestsCleanupHookDelegates.
Existing earlier acceptance/rebase receipts remain qualified to their prior checkpoints. Direct
Ensure calls and host/context construction still need the full caller audit; no collection guard,
lane exclusion, or full acceptance requirement is removed by this checkpoint. No throughput claim
is made from these timings.

## Driver-owned acceptance coordinators preserve the configured namespace

The production `ConductorDriver` constructor accepted cleanup hooks but constructed both its
acceptance and focused-evidence coordinators without their existing `buildStorageRoot` argument.
They consequently resolved the ambient namespace while cohort cleanup used the supplied hooks.
The constructor now captures its immutable hooks before creating the coordinators and supplies
their shared `BuildStorageRoot` to both. Null remains null; attempt-record directories remain
separate and build-pool capacity is unchanged.

This is a wiring correction at the existing owner. The coordinator stores the root at construction,
uses it in both `AcquireCohortStableSlotLease` and `AcquireAttemptStableSlotLease`, and includes it
in `ConductorParallelAcceptanceOwnedProcessLaunch`. `BuildOwnedProcessStartInfo` applies it to the
owned child's environment after hermetic scrubbing, preserving the namespace for that child and
its build descendants without mutating the parent environment. The existing configured-root
controls exercise real permit acquisition and the child launch record/start-info contract; they
do not by themselves establish a fresh external child round trip for this constructor change.

The existing three-member production merge-train fixture now passes an explicit cleanup context
instead of changing the process-wide build root. Its verifier records the actual lease root,
artifacts path and execution-lock path; all must be inside the supplied namespace, alongside the
existing one-gate/three-landings assertions. The first attempt stopped earlier at a null train
selection and cannot attest root behavior. A diagnostic was added to report candidate projections
if that prerequisite fails again. The earlier selection failure's cause remains undetermined.

The next execution reached the intended assertion: all three paths belonged to the collection's
ambient LocalLow root instead of the supplied fixture root. That red host exited two with no
timeout. After only the production constructor correction, the same test DLL passed the root
assertions and original landing assertions; host 33256 exited zero, no timeout, 48.966 seconds.
Red host 39152 took 39.139 seconds. These are correctness controls, not a performance comparison.
The identical test DLL in both controls has SHA256
`6E1D9C1CDF413A990427759F50274CCA29B458A74C5F890192B62D6C4604075E`.
The changed App DLL and test source have matching PDB checksums. Five existing ownership cases
also passed: both configured-root permit-wait arms, child launch propagation, blocked-attempt
re-gating, and complete partition sets for two independent verified goals.

Evidence under the operator directory: `cleanup-coordinator-root-diagnostic-control` (the intended
red), `cleanup-coordinator-root-green-control`, `cleanup-coordinator-root-green-ownership-controls`,
and corresponding source/runtime manifests. `cleanup-coordinator-root-red-control` is the earlier
inconclusive selection failure. Cross-family review is in `cleanup-coordinator-root-review.json`;
its ordinary-lease and child-propagation questions were checked at the source sites and controls
above. The review's uncertainty about whether the merge-train verifier uses the coordinator is
resolved by `RunMergeTrain` calling `AcquireCohortStableSlotLease` and the executed red/green pair.
Production focused-evidence constructor forwarding is source-verified here; its full-path
integration and the remaining ambient fixture migrations remain part of the open goal.

### Four additional collection memberships removed (2026-09-09)

Removed the legacy cleanup collection from BlockingVerifier, RebaseMerge,
RebaseMergeMaterialization, and OrphanEphemeralSweep after checking their scoped
roots, instance hooks, repository discovery boundaries, and console capture.
Cross-family review is recorded in cleanup-remaining-guards-review.json; the
orphan scanner deliberately remains rooted in its owning repository, not its
build-storage directory.

The managed mixed-family run cleanup-migrated-families-parallel passed 113/113,
zero skips, exit 0 with confirmed teardown and no timeout or cleanup diagnostic.
It ran collection parallelism with five threads and seed 20260909 against binary
8FC0A669194F1486483F734ADA1DCD5FE45A95EABAB96D7D83D3D99A011CA384;
remaining-guard-binding source receipts bind all four changed files to that build.
The lifecycle explicit-root negative control failed on ambient fallback and its
restored positive passed 1/1 before this run. Remaining collection members and
manifest exclusion keys still require migration; this is not final acceptance.

### Remaining CLI fixture root ownership and status forwarding (2026-09-09)

Nineteen dispatcher invocations across the three cleanup-hook CLI classes now
receive workspace-owned build roots; existing custom hooks are retained. The
verdict-carry-forward driver receives its own root too. The obsolete ambient
fixture assertion now checks the explicit root; the real CLI pinned-slot
negative control remains the behavioral ownership proof. Collection memberships
and cross-lane exclusion keys remain pending their final migration checks.

Managed cleanup-final-roots-managed ran 43 cases: 42 passed and the status
backoff assertion failed. Status discarded CleanupContext.Hooks when reading
backoff, so remaining wait used wall-clock time instead of the supplied clock.
Forwarding those same hooks through PrintGoalCleanupBackoffStatus restored
43/43 passing, zero skips, confirmed exit0 and clean teardown in
cleanup-status-forwarding-managed. Production PDB/source bindings were checked;
the test assembly is unchanged between the failing and passing runs (SHA
90E87EF4015709F7B121193FAB9B70575A78181CA84BCC8531AC03AA1B29946E).
Anthropic review cleanup-status-forwarding-review found no concrete blocker.
Earlier root-review compilation/path hypotheses are resolved by successful
build and the passing root test; no configuration or ownership contract was
weakened to get green. These focused checks do not replace final acceptance.

### Preserve the real GitRunner serialization boundary (2026-09-09)

Further source audit found that MainCasFailure_RestoresIntegrationRef_AndLandsNeitherMember
mutates LandingExecutor.GitRunner, a remaining process-wide seam unrelated to
cleanup hooks. Its complete method moved unchanged into
AcceptanceCohortWorkflowTestsGitRunner under the precise LandingGitRunner
nonparallel collection. The other five cohort classes remain parallel-capable.
LandingExecutorTests still retains its existing nonparallel protection.

The corrected48-case run cleanup-git-runner-guard-managed passed48/48, no skips,
confirmed exit0, no timeout or cleanup diagnostic, in6m18.538s. Separate source
bindings and exact-method-preservation receipts were checked. TRX timestamps
show one guarded case and zero overlaps with the other47 cases. The installed
xUnit4 collection contract documents DisableParallelization against any other
tests. Cross-family review's inheritance/fixture concerns were checked against
the abstract fact-free helper base and successful build; resolution is recorded
in cleanup-git-runner-review-resolution.md.

The earlier43% serial/parallel comparison describes the earlier candidate only.
It does not establish performance or safety of this corrected candidate. Final
scope-wide acceptance and fresh comparable timing remain outstanding. Do not
remove the precise guard until GitRunner itself has per-operation ownership.
