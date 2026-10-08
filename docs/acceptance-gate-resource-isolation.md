# Acceptance gate resource isolation

`exclusiveResourceKeys` in `config/acceptance-manifest.json` are admission constraints for shards in one
`GoalAcceptanceVerifier` invocation. They do not coordinate simultaneous goal gates and therefore cannot
make a host-global resource safe. A host-global resource must have its own cross-process lease or be moved
under a per-process/per-run identity.

The checked-in keys remain conservative until their former lane partners pass the real-process negative
control below. xUnit `DisableParallelization` means a serial collection cannot run alongside **any** other
collection in the same test process. The filter split puts those collections in separate MTP processes that
do run concurrently, and a per-collection key serializes only lanes declaring that same key; it does not
reproduce xUnit's process-wide exclusion. Each row therefore states the narrower cross-process invariant
that must hold before the split is safe. In-process xUnit behavior is supporting design evidence, not proof
that separate MTP processes do not share a resource.

| Collection / current key | Guarded identity and lifecycle | Required cross-process invariant | Source boundary | Adjudication | Required negative control |
| --- | --- | --- | --- | --- | --- |
| `CliProcessEnvironment` / no cross-shard key | Process-wide environment variables the persistent-runner and dispatch CLI fixtures overwrite: worker sandbox enablement and dispatch-start suppression. No fixture and no static hook replacement remain. | Environment mutation stays process-local and is restored; every repository root, build storage root, and attention store is per-operation. | `AssemblyInfo.cs::CliProcessEnvironmentCollection`, `CliCommandTests.cs::ClearWorkerSandboxEnv`, `CliCommandTestsPersistentRunnerCommands*`, `CliCommandTestsSubscriptionDispatchCommands` | **Keep unkeyed.** All twelve members select into `Goal lifecycle commands` only, so the collection spans one lane and needs no cross-shard key; environment variables are process-local across MTP shards, which the definition declares with `ProcessLocalTestCollection`. This replaces the cleanup-hook row below. | In the full gate, require nonzero `Goal lifecycle commands` count, complete TRX, restored parent environment, and no dispatch started by a suppressed-start test. |
| ~~`GoalWorktreeCleanupHooks` / `xunit:GoalWorktreeCleanupHooks`~~ | Superseded. Static cleanup-hook replacements became per-operation `WorktreeCleanupContext` ownership, and the GUID-rooted dotnet fixture became an explicit per-operation `DotnetBuildStorageRoot`. | n/a | Removed from `AssemblyInfo.cs` and from both lanes in `config/acceptance-manifest.json` | **Removed.** The guarded state no longer exists, so the removal gate below does not apply to it. What the collection also happened to provide — a bound on how many host-capacity-bound tests ran at once — is not a resource key and is now `HostCapacityTestBudget`. See the goal `5daaa1db` amendment. | n/a |
| `EnvMutation` / `xunit:EnvMutation` | Process environment and other process-wide test seams, restored by per-test scopes. | Environment, current-directory, and static mutations stay process-local and are restored; every path, port, store, or named OS object visible across processes is unique or independently leased. | `AssemblyInfo.cs::EnvMutationCollection`, `WorkerDispatchTestsDispatchPreparation`, `WorkerDispatchTestsModelSelectionEnvMutation` | **Retain.** Environment variables are process-local across MTP shards, but the complete lane membership has not been cleared of fixed paths, ports, or named OS resources. | In a disposable worktree under representative host load, run `Worker profiles` and `Worker dispatch fixtures` concurrently for 50-100 clean iterations and compare the parent environment before/after; require no fixed-path, process, port, or store leakage. Any flake retains the key; remove no other key in the same change. |
| `ProcessSpawning` / `xunit:ProcessSpawning` | Child processes plus process registries, launcher configuration, temporary repositories, and any endpoints they own. | Every child, PID registry, launcher file, repository, endpoint, and handle has a unique owning run and is drained, joined, and removed before that run ends. | `AssemblyInfo.cs::ProcessSpawningCollection`, `ConductorDriverTests`, `DispatchProcessHostTests`, `ProcessTreeGuiSuppressionTests` | **Retain.** Process creation is not itself shared state, but the broad collection has not been proven free of shared registries, endpoints, or launcher files across real processes. | In a disposable worktree under representative host load, run each former partner-lane pair concurrently for 50-100 clean iterations; require owned-process exit receipts, drained output, no inherited live handles, complete TRX files, and no repository/port/registry residue. Any flake retains the key; remove no other key in the same change. |
| `DotnetBuildSlots` / `xunit:DotnetBuildSlots` | Build-slot locks and artifacts beneath the fixture's GUID root, restored and deleted when the collection process exits. | Every slot path, lock, and artifact resolves below the per-process GUID root, including subprocesses; no execution falls back to the host slot root. | `AssemblyInfo.cs::DotnetBuildSlotsCollection`, `TestCollections.cs::IsolatedDotnetRootFixture`, `DotnetBuildEnvironmentManagerTests` | **P4b: machine-local; cleared for the machine-local list after the P4a hold.** `LaneIsolatedRootGuardTests` checks the effective lane's isolated collection membership and override clears, with named exceptions for explicit GUID storage, fake-only conductor actions, and the focused runner's GUID child-profile fallback. Owed before an operator edit: a passing focused guard receipt and the P4a ten-gate hold with accepted remote executed counts equal to local; first remote executions of this lane must also match local counts. The 50-100 iteration overlap receipts are not required for adding this machine-local key; they remain required for key removal below. Same-executor overlap is subject to the slots trigger. | In a disposable worktree under representative host load, run `Dotnet build slots` with `Remainder` concurrently for 50-100 clean iterations; require distinct resolved roots, complete TRX files, released locks, and no host-slot artifacts. Any flake retains the key; remove no other key in the same change. |
| `JobAccounting` / `xunit:JobAccounting` | Windows job handles, child PID accounting, slot-pinned gate processes, and environment overrides restored by test scopes. | Job handles/names, PID records, slot files, and child ownership are unique per run, and no child or lease survives its owner. | `AssemblyInfo.cs::JobAccountingCollection`, `WorkerDispatchJobAccountingTests`, `GoalAcceptanceVerifierDotnetBuildSlotTests` | **P4b: blocked / not cleared for the machine-local list.** `GoalWorktreeAcceptanceContentionTestsScenarioHostRootIsolation` covers only scenario host-state-root isolation; it does not prove job/PID ownership. This key gates both `Goal acceptance verifier` and `Goal acceptance build slots` (`config/acceptance-manifest.json:112-126`); both lanes must be proven together. Owed before an operator edit: source evidence of job-handle and PID-record uniqueness per run, the 50-100 iteration concurrent overlap receipts specified here (still required, including child exits, complete TRX, no surviving PIDs or slot leases), and the P4a ten-gate hold with accepted remote executed counts equal to local. Do not add this key; overlap follow-up remains open and two-slot acceptance excludes it. | In a disposable worktree under representative host load, run `Goal acceptance verifier` with `Goal acceptance build slots` concurrently for 50-100 clean iterations; require unique job/process identities, explicit child exits, complete TRX files, and no surviving PIDs or slot leases. Any flake retains the key; remove no other key in the same change. |
| `GoalAcceptanceVerifier` / `xunit:GoalAcceptanceVerifier` | Nested acceptance/build activity and process-wide verifier test hooks under a GUID-rooted dotnet directory. | Verifier hooks stay process-local; nested gates and builds inherit unique roots and leases, restore all ambient state, and leave no host-global child or artifact identity. | `AssemblyInfo.cs::GoalAcceptanceVerifierCollection`, `TestCollections.cs::IsolatedDotnetRootFixture`, `GoalAcceptanceVerifierTests` | **P4b: key machine-local by construction; lane clearance pending.** Verifier hooks are process-local; the git-config writer uses a path-derived mutex (`src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.HermeticGitConfiguration.cs:27`, Windows session-local, narrower than machine-wide). `GoalWorktreeAcceptanceContentionTestsScenarioHostRootIsolation` checks that scenario state stays under its own temporary root. Owed before an operator edit: passing scenario-guard evidence, an isolated-root audit/guard over the entire `Goal acceptance verifier` lane, and the P4a ten-gate hold with accepted remote executed counts equal to local. The 50-100 iteration overlap receipts remain required with `JobAccounting` and for key removal; no separate overlap receipt is required for this key's machine-local identity. Clearing this key alone does not make the lane remote-eligible: every lane key must be listed, and `JobAccounting` remains uncleared (`src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteLaneCoordinator.cs:47-58`). | In a disposable worktree under representative host load, run `Goal acceptance verifier` with `Remainder` concurrently for 50-100 clean iterations; require distinct roots, nonzero counts, complete TRX files, overlap, restored hooks/environment, and no child or artifact residue. Any flake retains the key; remove no other key in the same change. |
| `ChaosGateGit` / no cross-shard key | Each test creates a repository below a GUID-named temporary directory and deletes it in its fixture lifecycle. | Every repository and temporary root is unique, and no test mutates global git configuration or process current directory. | `ChaosGateTestBase.cs::CreateSeededRepo` | **Keep unkeyed.** Source establishes per-test repository identity; no global git configuration or current-directory mutation is part of the base fixture. This remains subject to the full-gate contamination check. | In the full gate, require nonzero Chaos gate count, complete TRX, independent repository roots, and no global-git/current-directory drift. |
| `ProviderEnvironment` / no cross-shard key | The extracted ProviderEnvironment assembly restores provider variables by scope; probe servers bind loopback port `0`, while connection-refused probes temporarily release an OS-selected port before use. | Provider variables stay process-local and are restored; a port identity remains owned from allocation through observation, or the test tolerates reassignment without changing its verdict. | `ProviderEnvironment/AssemblyInfo.cs::ProviderEnvironmentCollection`, `ProviderEnvironment/ProviderProbeTests.cs::ProbeServer.StartAsync`, `ProviderEnvironment/ProviderEnvironmentTestSupport.cs::GetAvailablePort` | **Keep unkeyed.** The extracted project runs on the verifier's sequential project-check path, outside the parent lane scheduler that consumes `exclusiveResourceKeys`; its assembly-local `DisableParallelization` still protects the two provider classes from each other. Cross-project admission remains goal `780f1790`. | In the full gate, require the separate provider project check to execute 18 nonzero tests with complete TRX, unique owned endpoints, restored variables, and no listening socket after completion. |

## P4b executor slots decision

Accept two slots per executor for keys settled as process-isolated: `xunit:DotnetBuildSlots`, and
`xunit:GoalAcceptanceVerifier` once its owed lane audit and lane eligibility are satisfied.
`xunit:JobAccounting` is excluded until its owed source and overlap evidence lands. Machine-local list
clearance does not remove any manifest key or waive the removal gate below.

`src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteLaneCoordinator.cs:60-83` orders executors by
held slot count and claims the first free index below `Slots`, without a key in that decision. It does
not enforce one lane per key per executor (one per laptop). Add per-key-per-executor claim enforcement
only on a remote-red attributable to two same-key lanes on one executor.

The operator step follows landing and the P4a ten-gate hold. Back up the runtime executor configuration
before adding any cleared key, and revert to that backup on any new remote-red attributable to these lanes.

## Removal gate

Remove at most one key per change. Before removing it, run its former partner lanes concurrently for 50-100
clean real-process iterations in a disposable worktree under representative host load, then run one full
acceptance gate (currently 17 parent shards plus the ProviderEnvironment project check). Every run must
record the exact commit, start/end intervals proving overlap,
nonzero test counts, complete TRX paths, exit codes, and cleanup evidence. Any flake, zero-test run, missing
receipt, nondeterminism, or leaked repository, environment, process, port, slot, job, or filesystem state
retains the key. Unit-test timing and stub runners do not satisfy this gate.

The baseline for comparison is goal `a9920536`: 18 shard verdicts, 2,244,894 ms total shard work, and
595,660 ms wall clock. Post-change performance remains unclaimed until the real acceptance workflow records
the corresponding receipt.

### Baseline receipt

The clean `a9920536` acceptance run on 2026-08-09 produced these shard durations:

| Lane | Duration (s) | Lane | Duration (s) |
| --- | ---: | --- | ---: |
| Worker dispatch fixtures | 354.0 | Goal worktree cleanup | 327.3 |
| Goal acceptance build slots | 326.0 | Goal lifecycle commands | 248.4 |
| Process spawning | 230.2 | Dotnet build slots | 201.2 |
| Remainder | 176.6 | Remainder balance A | 136.6 |
| Chaos gate | 75.1 | Worker profiles | 45.8 |
| Remainder balance B | 33.1 | Goal acceptance verifier | 30.5 |
| Cli | 11.4 | Provider environment | 11.3 |
| Conduct watch sweep scoping | 10.0 | Worker sandbox planner | 9.3 |
| Dashboard validation | 9.2 | Worker shell | 9.1 |

The four longest measured exclusive-resource chains were:

```text
xunit:GoalWorktreeCleanupHooks  Goal worktree cleanup 327.3s + Goal lifecycle commands 248.4s     = 575.7s
xunit:EnvMutation               Worker dispatch fixtures 354.0s + Worker profiles 45.8s          = 399.8s
xunit:DotnetBuildSlots          Dotnet build slots 201.2s + Remainder 176.6s                     = 377.8s
xunit:JobAccounting             Goal acceptance build slots 326.0s + Goal acceptance verifier 30.5s = 356.5s
```

### Goal `6bb531ff` lane-composition amendment

The table above remains the measured **before** receipt. This candidate separates tests by resource ownership
without removing `xunit:GoalWorktreeCleanupHooks`:

- `Cli` gains 84 source-declared goal-lifecycle test attributes. The 24 hook-owning attributes plus 280 tests
  selected from the lane's other class families stay in `Goal lifecycle commands` (388 old-lane attributes in
  total). Three of the moved tests that create build leases use the process-local `IsolatedDotnetRoot` fixture
  collection and assert cleanup success; they do not rejoin the cross-process key.
- `Goal worktree parallel` owns the 8 `GoalWorktreeTestsCreationResolution` and 16
  `GoalWorktreeTestsSqliteTooling` attributes and declares no exclusive key. The remaining 125 source-declared
  worktree-cleanup attributes stay in `Goal worktree cleanup`.
- Until the operator-owned receipt exists, the manifest apportions the old measured lane values by those
  source-declared memberships: `Cli` 65.2s (its measured 11.4s baseline plus the moved share) /
  `Goal lifecycle commands` 194.6s and
  `Goal worktree parallel` 52.7s / `Goal worktree cleanup` 274.6s. These are scheduling estimates, not a
  measured performance claim. Their pre-split aggregates remain 259.8s and 327.3s (rounding aside).

The provisional keyed chain is therefore `194.6 + 274.6 = 469.2s`, versus the measured 575.7s before chain.
Acceptance must replace the four provisional lane estimates with `PHASE_PROGRESS`/`shard-complete` values and
report the actual keyed-chain reduction. A small or absent measured reduction closes this slice without
re-architecting the retained cleanup-hook dependency.

### Post-change comparison method

The operator-owned acceptance run must record the exact commit and retain every manifest-declared parent
`shard-complete` line, each separate extracted-project check receipt, the `phase=shards-complete elapsed_ms`
line, and the terminal `EVIDENCE_END duration_s` line. First verify that every named lane and extracted project
emitted a successful gating verdict. Sum the parent-shard elapsed values for parent-shard work and record the
extracted checks separately; reconstruct every exclusive-key chain from the manifest and the parent shard
values. The shard-phase
wall clock is `shards-complete elapsed_ms`; `EVIDENCE_END duration_s` is the end-to-end cross-check and must
not be substituted for the shard-phase number.

For a direct like-for-like comparison, subtract the baseline wall clock: `postWallMs - 595,660`. Because
test durations can move between runs, also compare normalized scheduler overhead:

```text
baselineCriticalChain = 327,300 + 248,400 = 575,700 ms
baselineSchedulerOverhead = 595,660 - 575,700 = 19,960 ms
postSchedulerOverhead = postWallMs - postLongestExclusiveChainMs
```

A post-run overhead below 19,960 ms is an improvement; above 19,960 ms is a regression. A lower direct wall
clock with materially less than 2,244,894 ms of shard work is workload drift, not sufficient performance
evidence. Any missing/failed lane, incomplete receipt, or cleanup failure is a correctness regression
regardless of timing.

No exclusive key or shard-cap setting changed in this goal. Key-aware admission can remove only avoidable
idle time above the retained critical chain. With baseline durations, its predicted effect is therefore
between 0 and `595,660 - 575,700 = 19,960 ms` saved (at most 3.35%), for a shard-phase wall clock between
575,700 and 595,660 ms. A materially larger saving would require changed lane durations or a broken chain
and must be reconciled from the per-shard receipt rather than attributed to this scheduler change.

### Goal `5daaa1db` amendment: measured overlap cost, cause not established

Removing `xunit:GoalWorktreeCleanupHooks` and its collection let `Goal lifecycle commands` and
`Goal worktree cleanup` overlap, and let the cleanup lane's own classes overlap each other for the first
time. The first full gate at that candidate failed one test and produced almost no wall-clock gain.

Measured from the two lane receipts, same lane filter:

```text
baseline  operator-gate-20260907225105071 ...goal-worktree-cleanup.trx  213 cases, summed 1305.2s, serial
candidate 5daaa1db-0-20260912152523352    ...goal-worktree-cleanup.trx  232 cases, summed 12925.7s, wall 1237s
per-case candidate/baseline ratio over the 125 cases at or above one second in the baseline:
  median 10.53x, mean 12.05x, min 2.09x, max 40.45x
Cli_acceptance_lands_after_transient_state_write_lock_releases: 3.5-11.8s across five baseline
  receipts, 100.0s at the candidate, of which 60s was its own WaitAsync hang guard expiring.
```

That is the whole of what the receipts establish: per-operation ownership made these cases overlap, and at
this concurrency each case took about ten times as long while the lane wall clock moved 1305s to 1237s —
5%. The two runs are not a controlled comparison. They differ in case count (213 against 232) and in
concurrency at the same time, they were taken on an unmeasured host with the gate already running up to
`maxConcurrentShards` test processes at once (each at xUnit's default `maxParallelThreads` of
`Environment.ProcessorCount`), and no serialized-versus-concurrent run at a fixed case set and fixed
concurrency was taken. Host load and a remaining contention path in these fixtures — git subprocesses,
worktree file I/O and SQLite — are both consistent with these numbers, and nothing here separates them.
No cause is claimed and no host threshold is asserted. Settling it requires the controlled comparison in
the criterion 6 paragraph below, run at a recorded concurrency over one case set.

`HostCapacityTestBudget` bounds how many host-capacity-bound tests run at once inside one test process:
one slot per eight logical processors, clamped to `[2, 4]`, overridable with
`MCG_TEST_HOST_CAPACITY_SLOTS`. `HostCapacityBoundTestBase` takes a slot for each test body, and the
`GoalWorktreeTestBase` and `CliCommandTestBase` families derive from it. It is a bounded load cap adopted
while the cause is unestablished — its own value is unproven until that comparison runs, and its
`[2, 4]` clamp is a conservative default, not a measured host limit.

This is deliberately **not** an exclusive resource key and **not** a nonparallel collection:

- It admits several tests at once and imposes no ordering, so it protects no shared state and cannot
  substitute for either mechanism. Fixtures that mutate process-global state keep their collection.
- It is per-process, so it makes no cross-process claim. The rows above still govern that question.
- It caps load, so it can only reduce overlap, never introduce an interleaving that did not already occur.

Criterion 6 stays open and operator-owned, and is unmet at the current candidate. The predeclared
comparison is: rerun both lanes at the delivered candidate with `MCG_TEST_HOST_CAPACITY_SLOTS` and shard
concurrency held fixed across the compared runs, and report commit identity, runner class, per-lane
start/end, selected counts, summed case seconds and actual overlap, noting any case-count difference that
limits comparability. Delivery requires a demonstrable reduction in the affected serial critical path. The
5% above is the number that must improve; the removed attributes are not themselves the result. The
focused receipt at 4a96f18f (121/121 over the caller-correction, host-capacity and landing-lock classes)
is focused evidence of those classes only and contributes nothing to this criterion.
