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
| `GoalWorktreeCleanupHooks` / `xunit:GoalWorktreeCleanupHooks` | Static cleanup-hook replacements and acceptance build leases; the collection fixture sets a GUID-rooted dotnet directory for the process and restores it on disposal. | Hook mutation stays process-local; every worktree, build lease, and cleanup target is uniquely rooted or protected by a real cross-process lease for its complete lifetime. | `AssemblyInfo.cs::GoalWorktreeCleanupHooksCollection`, `TestCollections.cs::IsolatedDotnetRootFixture`, `CliCommandTests.GoalLifecycleCommands.cs::CliAcceptancePinsSelectedStableSlotAndRecordsReceipt` | **Retain.** The visible slot root is isolated, but the two lane filters lack the required real-process overlap and cleanup receipt covering every member. The 575.7-second chain is therefore retained. | In a disposable worktree under representative host load, run `Goal lifecycle commands` and `Goal worktree cleanup` concurrently for 50-100 clean iterations; require nonzero counts, complete TRX files, overlap, restored environment, released leases, and no worktree/temp-root residue. Any flake retains the key; remove no other key in the same change. |
| `EnvMutation` / `xunit:EnvMutation` | Process environment and other process-wide test seams, restored by per-test scopes. | Environment, current-directory, and static mutations stay process-local and are restored; every path, port, store, or named OS object visible across processes is unique or independently leased. | `AssemblyInfo.cs::EnvMutationCollection`, `WorkerDispatchTestsDispatchPreparation`, `WorkerDispatchTestsModelSelectionEnvMutation` | **Retain.** Environment variables are process-local across MTP shards, but the complete lane membership has not been cleared of fixed paths, ports, or named OS resources. | In a disposable worktree under representative host load, run `Worker profiles` and `Worker dispatch fixtures` concurrently for 50-100 clean iterations and compare the parent environment before/after; require no fixed-path, process, port, or store leakage. Any flake retains the key; remove no other key in the same change. |
| `ProcessSpawning` / `xunit:ProcessSpawning` | Child processes plus process registries, launcher configuration, temporary repositories, and any endpoints they own. | Every child, PID registry, launcher file, repository, endpoint, and handle has a unique owning run and is drained, joined, and removed before that run ends. | `AssemblyInfo.cs::ProcessSpawningCollection`, `ConductorDriverTests`, `DispatchProcessHostTests`, `ProcessTreeGuiSuppressionTests` | **Retain.** Process creation is not itself shared state, but the broad collection has not been proven free of shared registries, endpoints, or launcher files across real processes. | In a disposable worktree under representative host load, run each former partner-lane pair concurrently for 50-100 clean iterations; require owned-process exit receipts, drained output, no inherited live handles, complete TRX files, and no repository/port/registry residue. Any flake retains the key; remove no other key in the same change. |
| `DotnetBuildSlots` / `xunit:DotnetBuildSlots` | Build-slot locks and artifacts beneath the fixture's GUID root, restored and deleted when the collection process exits. | Every slot path, lock, and artifact resolves below the per-process GUID root, including subprocesses; no execution falls back to the host slot root. | `AssemblyInfo.cs::DotnetBuildSlotsCollection`, `TestCollections.cs::IsolatedDotnetRootFixture`, `DotnetBuildEnvironmentManagerTests` | **Retain.** Source shows a run-scoped override, but fallback and subprocess inheritance across every test in the two keyed lanes have not been demonstrated by a real overlap receipt. | In a disposable worktree under representative host load, run `Dotnet build slots` with `Remainder` concurrently for 50-100 clean iterations; require distinct resolved roots, complete TRX files, released locks, and no host-slot artifacts. Any flake retains the key; remove no other key in the same change. |
| `JobAccounting` / `xunit:JobAccounting` | Windows job handles, child PID accounting, slot-pinned gate processes, and environment overrides restored by test scopes. | Job handles/names, PID records, slot files, and child ownership are unique per run, and no child or lease survives its owner. | `AssemblyInfo.cs::JobAccountingCollection`, `WorkerDispatchJobAccountingTests`, `GoalAcceptanceVerifierDotnetBuildSlotTests` | **Retain.** Handles are normally process-owned and repositories are uniquely seeded, but the job/slot partner lanes lack repeated real-process overlap and child-cleanup evidence. | In a disposable worktree under representative host load, run `Goal acceptance verifier` with `Goal acceptance build slots` concurrently for 50-100 clean iterations; require unique job/process identities, explicit child exits, complete TRX files, and no surviving PIDs or slot leases. Any flake retains the key; remove no other key in the same change. |
| `GoalAcceptanceVerifier` / `xunit:GoalAcceptanceVerifier` | Nested acceptance/build activity and process-wide verifier test hooks under a GUID-rooted dotnet directory. | Verifier hooks stay process-local; nested gates and builds inherit unique roots and leases, restore all ambient state, and leave no host-global child or artifact identity. | `AssemblyInfo.cs::GoalAcceptanceVerifierCollection`, `TestCollections.cs::IsolatedDotnetRootFixture`, `GoalAcceptanceVerifierTests` | **Retain.** Static hooks are process-local, but nested subprocess inheritance and cleanup across the entire verifier/remainder filters have not been proven in real concurrent executions. | In a disposable worktree under representative host load, run `Goal acceptance verifier` with `Remainder` concurrently for 50-100 clean iterations; require distinct roots, nonzero counts, complete TRX files, overlap, restored hooks/environment, and no child or artifact residue. Any flake retains the key; remove no other key in the same change. |
| `ChaosGateGit` / no cross-shard key | Each test creates a repository below a GUID-named temporary directory and deletes it in its fixture lifecycle. | Every repository and temporary root is unique, and no test mutates global git configuration or process current directory. | `ChaosGateTestBase.cs::CreateSeededRepo` | **Keep unkeyed.** Source establishes per-test repository identity; no global git configuration or current-directory mutation is part of the base fixture. This remains subject to the full-gate contamination check. | In the full gate, require nonzero Chaos gate count, complete TRX, independent repository roots, and no global-git/current-directory drift. |
| `ProviderEnvironment` / no cross-shard key | Provider variables are restored by scopes; probe servers bind loopback port `0`, while connection-refused probes temporarily release an OS-selected port before use. | Provider variables stay process-local and are restored; a port identity remains owned from allocation through observation, or the test tolerates reassignment without changing its verdict. | `AssemblyInfo.cs::ProviderEnvironmentCollection`, `ProviderProbeTests.cs::EnvironmentVariableScope`, `ProviderProbeTests.cs::ProbeServer.StartAsync`, `InfrastructureTestSupport.cs::GetAvailablePort` | **Keep unkeyed.** One lane contains the collection, so a per-collection key would be a no-op; the bind-then-release connection-refused probe remains a transient host-global exposure to remove or make race-tolerant. | In the full gate, require nonzero provider count, complete TRX, unique owned endpoints, restored variables, and no listening socket after completion. |

## Removal gate

Remove at most one key per change. Before removing it, run its former partner lanes concurrently for 50-100
clean real-process iterations in a disposable worktree under representative host load, then run one full
18-shard acceptance gate. Every run must record the exact commit, start/end intervals proving overlap,
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

### Post-change comparison method

The operator-owned acceptance run must record the exact commit and retain all 18 `shard-complete` lines,
the `phase=shards-complete elapsed_ms` line, and the terminal `EVIDENCE_END duration_s` line. First verify
that the same 18 named lanes emitted successful gating verdicts. Sum the 18 shard elapsed values for total
work and reconstruct every exclusive-key chain from the manifest and those same values. The shard-phase
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
