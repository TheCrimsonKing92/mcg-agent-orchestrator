# Acceptance gate resource isolation

`exclusiveResourceKeys` in `config/acceptance-manifest.json` are admission constraints for shards in one
`GoalAcceptanceVerifier` invocation. They do not coordinate simultaneous goal gates and therefore cannot
make a host-global resource safe. A host-global resource must have its own cross-process lease or be moved
under a per-process/per-run identity.

The checked-in keys remain conservative until their former lane partners pass the real-process negative
control below. In-process xUnit collection serialization is supporting evidence about test design, not proof
that two separate MTP processes share a resource.

| Collection / current key | Guarded identity and lifecycle | Source boundary | Adjudication | Required negative control |
| --- | --- | --- | --- | --- |
| `GoalWorktreeCleanupHooks` / `xunit:GoalWorktreeCleanupHooks` | Static cleanup-hook replacements and acceptance build leases; the collection fixture sets a GUID-rooted dotnet directory for the process and restores it on disposal. | `AssemblyInfo.cs::GoalWorktreeCleanupHooksCollection`, `TestCollections.cs::IsolatedDotnetRootFixture`, `CliCommandTests.GoalLifecycleCommands.cs::CliAcceptancePinsSelectedStableSlotAndRecordsReceipt` | **Retain.** The visible slot root is isolated, but the two lane filters have not yet produced a five-run real-process overlap and cleanup receipt covering every member. The 575.7-second chain is therefore retained. | Run `Goal lifecycle commands` and `Goal worktree cleanup` concurrently five times; require nonzero counts, TRX files, overlap, restored environment, released leases, and no worktree/temp-root residue. |
| `EnvMutation` / `xunit:EnvMutation` | Process environment and other process-wide test seams, restored by per-test scopes. | `AssemblyInfo.cs::EnvMutationCollection`, `WorkerDispatchTestsDispatchPreparation`, `WorkerDispatchTestsModelSelectionEnvMutation` | **Retain.** Environment variables are process-local across MTP shards, but the complete lane membership has not been cleared of fixed paths, ports, or named OS resources. | Run `Worker profiles` and `Worker dispatch fixtures` concurrently five times and compare the parent environment before/after; also require no fixed-path, process, port, or store leakage. |
| `ProcessSpawning` / `xunit:ProcessSpawning` | Child processes plus process registries, launcher configuration, temporary repositories, and any endpoints they own. | `AssemblyInfo.cs::ProcessSpawningCollection`, `ConductorDriverTests`, `DispatchProcessHostTests`, `ProcessTreeGuiSuppressionTests` | **Retain.** Process creation is not itself shared state, but the broad collection has not been proven free of shared registries, endpoints, or launcher files across real processes. | Run each pair of lanes sharing this key concurrently five times; require owned-process exit receipts, drained output, no inherited live handles, complete TRX files, and no repository/port/registry residue. |
| `DotnetBuildSlots` / `xunit:DotnetBuildSlots` | Build-slot locks and artifacts beneath the fixture's GUID root, restored and deleted when the collection process exits. | `AssemblyInfo.cs::DotnetBuildSlotsCollection`, `TestCollections.cs::IsolatedDotnetRootFixture`, `DotnetBuildEnvironmentManagerTests` | **Retain.** Source shows a run-scoped override, but fallback and subprocess inheritance across every test in the two keyed lanes have not been demonstrated by a real overlap receipt. | Run `Dotnet build slots` with `Remainder` concurrently five times; require distinct resolved roots, complete TRX files, released locks, and no host-slot artifacts. |
| `JobAccounting` / `xunit:JobAccounting` | Windows job handles, child PID accounting, slot-pinned gate processes, and environment overrides restored by test scopes. | `AssemblyInfo.cs::JobAccountingCollection`, `WorkerDispatchJobAccountingTests`, `GoalAcceptanceVerifierDotnetBuildSlotTests` | **Retain.** Handles are normally process-owned and repositories are uniquely seeded, but the job/slot partner lanes lack repeated real-process overlap and child-cleanup evidence. | Run `Goal acceptance verifier` with `Goal acceptance build slots` concurrently five times; require unique job/process identities, explicit child exits, complete TRX files, and no surviving PIDs or slot leases. |
| `GoalAcceptanceVerifier` / `xunit:GoalAcceptanceVerifier` | Nested acceptance/build activity and process-wide verifier test hooks under a GUID-rooted dotnet directory. | `AssemblyInfo.cs::GoalAcceptanceVerifierCollection`, `TestCollections.cs::IsolatedDotnetRootFixture`, `GoalAcceptanceVerifierTests` | **Retain.** Static hooks are process-local, but nested subprocess inheritance and cleanup across the entire verifier/remainder filters have not been proven in real concurrent executions. | Run `Goal acceptance verifier` with `Remainder` concurrently five times; require distinct roots, nonzero counts, complete TRX files, overlap, restored hooks/environment, and no child or artifact residue. |
| `ChaosGateGit` / no cross-shard key | Each test creates a repository below a GUID-named temporary directory and deletes it in its fixture lifecycle. | `ChaosGateTestBase.cs::CreateSeededRepo` | **Keep unkeyed.** Source establishes per-test repository identity; no global git configuration or current-directory mutation is part of the base fixture. This remains subject to the full-gate contamination check. | In the full gate, require nonzero Chaos gate count, complete TRX, independent repository roots, and no global-git/current-directory drift. |
| `ProviderEnvironment` / no cross-shard key | Provider variables are restored by scopes; probe servers bind loopback port `0`, giving each server an OS-assigned endpoint. | `AssemblyInfo.cs::ProviderEnvironmentCollection`, `ProviderProbeTests.cs::EnvironmentVariableScope`, `ProviderProbeTests.cs::ProbeServer.StartAsync` | **Keep unkeyed.** The observable external identities are process-local environment and dynamically allocated loopback ports. | In the full gate, require nonzero provider count, complete TRX, unique endpoints, restored variables, and no listening socket after completion. |

## Removal gate

Removing a key requires five clean real-process iterations of its former partner lanes plus one full
18-shard acceptance run. Every run must record the exact commit, start/end intervals proving overlap,
nonzero test counts, complete TRX paths, exit codes, and cleanup evidence. Any zero-test run, missing receipt,
nondeterminism, or leaked repository, environment, process, port, slot, job, or filesystem state retains the
key. Unit-test timing and stub runners do not satisfy this gate.

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
