# Test Parallel Safety Wave 1 Audit

Goal: `395bf7f9b0ab45c2b6aaa2217b3cb96d`

Date: 2026-07-06

## Scope And Attribute Inventory

This is a read-only source audit for the test projects under `tests/`. There is no separate App test project in this checkout; App behavior is covered through `tests/Mcg.AgentOrchestrator.Infrastructure.Tests` CLI, dashboard, hosted-dashboard, and orchestration tests.

All parallelization controls found by source search are in `tests/Mcg.AgentOrchestrator.Infrastructure.Tests`:

| File | Attribute | Accounted use |
| --- | --- | --- |
| `AssemblyInfo.cs:2` | `[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]` | Whole Infrastructure test assembly is serial. Comment ties this to backlog `ad7c8268` rotating full-suite failures. |
| `AssemblyInfo.cs:5` | `CollectionDefinition("ProcessSpawning", DisableParallelization = true)` | Serializes process-spawning, git, worker-dispatch, and worktree integration classes. |
| `AssemblyInfo.cs:9` | `CollectionDefinition("ConsoleOutMutation", DisableParallelization = true)` | Serializes legacy tests that still reassign `Console.Out`. |
| `AssemblyInfo.cs:13` | `CollectionDefinition("ProviderEnvironment", DisableParallelization = true)` | Serializes provider discovery tests that mutate provider environment variables. |
| `DotnetBuildEnvironmentManagerTests.cs:11` | `CollectionDefinition("IsolatedDotnetRoot", DisableParallelization = true)` | Serializes tests that depend on `MCG_DOTNET_ISOLATED_ROOT` and slot lease artifacts. |

Collection assignments found:

| Collection | Classes |
| --- | --- |
| `ProcessSpawning` | `DispatchProcessHostTests`, `GitCliTests`, `GoalWorktreeIntegrationTests`, `ChaosGateTests`, `LauncherScriptTests`, `WorkerContextArtifactsCharacterizationTests`, `WorkerProcessJobsTests`, `WorkerDispatchTests` |
| `ConsoleOutMutation` | `CliHelpTests`, `GoalWorktreeFastTests` |
| `ProviderEnvironment` | `ProviderDefaultTests`, `ProviderProbeTests` |
| `IsolatedDotnetRoot` | `DotnetBuildEnvironmentManagerTests`, `FirewallSetupCommandTests`, `GoalAcceptanceVerifierTests`, `LocalProcessVerifierTests` |

`tests/Mcg.AgentOrchestrator.Core.Tests` has no `CollectionBehavior`, `CollectionDefinition`, or `[Collection]` attributes. Its shared-resource uses are read-only repo docs (`AgentHarnessDocsDriftTests`) or per-test temp/log paths (`ConductorAutonomyPolicyTests`, `DispatchEmptyOutputFlakeTests`), with clocks mostly injected through `TestClock`. No Core test class was found mutating process-global state.

## Shared-State Hazard Inventory

| Resource | Test classes touching it | Race mode | Classification |
| --- | --- | --- | --- |
| `Console.Out` / `Console.Error` process globals | `CliHelpTests` and `GoalWorktreeFastTests` still have local `Console.SetOut` helpers; `GoalRefinementTests` directly sets `Console.SetError`; many CLI view tests use `InfrastructureTestSupport.CaptureConsole`, which now routes through `AsyncLocalConsoleRouter` installed once in `ConsoleCapture.cs`. | Direct `SetOut` / `SetError` can steal output from another parallel test or restore a stale writer after another capture starts. AsyncLocal capture is intended to be parallel-safe, but direct legacy helpers bypass it. | Fixture-refactor |
| Provider environment variables: `OPENAI_API_KEY`, `OPENAI_MODEL`, `OLLAMA_BASE_URL`, `OLLAMA_MODEL`, `ANTHROPIC_API_KEY` | `ProviderDefaultTests`, `ProviderProbeTests`, `WorkerDispatchTests`, spawned dashboard helpers in `InfrastructureTestSupport`, `ProviderIntegrationTests` reads live/provider defaults. | One test can observe another test's provider settings, accidentally select a live provider, or mask missing-key paths. Spawned processes inherit pinned env safely, but in-process provider factories read process env. | Fixture-refactor |
| Orchestrator behavior env vars: `MCG_ORCHESTRATOR_REPOSITORY_ROOT`, `MCG_ORCHESTRATOR_STDOUT_LOG_PATH`, `MCG_ORCHESTRATOR_STDERR_LOG_PATH`, `MCG_ORCHESTRATOR_PROTECTED_PID`, `MCG_ACCEPTANCE_CHANGE_SCOPED`, `MCGO_DISCORD_BOT_TOKEN`, `MCG_WORKER_SANDBOX_ENABLED`, dispatch-start disable flag | `WorkspaceConsolidatorTests`, `GoalBacklogLinkTests`, `WorkerProcessJobsTests`, `GoalAcceptanceVerifierTests`, `LocalProcessVerifierTests`, `GoalWorktreeIntegrationTests`, `WorkerDispatchTests`, `AdvanceLoopTests`, `CliCommandTests`, and helpers in `InfrastructureTestSupport`. | Scoped set/restore is not safe across classes: restores can erase another test's value; background work can read the wrong value; child processes can inherit unexpected flags. | Fixture-refactor |
| `Environment.CurrentDirectory` | `WorkerProfileTests` mutates it; repository-root discovery helpers in `InfrastructureTestSupport` and `AgentHarnessDocsDriftTests` read it. | Parallel tests can resolve relative profile paths or repo discovery against another test's current directory. | Fixture-refactor |
| `GoalWorktrees.CleanupUtcNow` and `GoalWorktrees.SandboxAclHelper` static hooks | `GoalWorktreeIntegrationTests`, `CliCommandTests`. | Tests replace static clock/helper delegates; overlapping tests can see the wrong clock, wrong ACL helper, or restore over another test's replacement. | Fixture-refactor moving toward architectural |
| `WorkerProcessJobs` static registry/job state | `WorkerProcessJobsTests`; worker-dispatch and worktree flows exercise process job registration and owned process IDs. | `ConfigureRegistry`, `ClearRegistryForTests`, process registration, and release are process-global. Parallel runs can clear another test's registry or kill/release another test's process. | Architectural |
| Real process spawning and process kill semantics | `DispatchProcessHostTests`, `GitCliTests`, `GoalWorktreeIntegrationTests`, `ChaosGateTests`, `LauncherScriptTests`, `WorkerContextArtifactsCharacterizationTests`, `WorkerProcessJobsTests`, `WorkerDispatchTests`, `DashboardHostTests`, `GoalsPruneTests`, `CliCommandTests` app-CLI subprocess tests. | Long-running shells, git helpers, app subprocesses, and cancellation tests contend for OS process table, job objects, inherited env, stdout/stderr files, and cleanup. Failures can leak or kill the wrong process if identity checks are weak. | Architectural |
| TCP ports / hosted dashboard / probe servers | `DashboardHostTests`, `ProviderProbeTests`, `HealthInspectorTests`; dashboard process helper in `InfrastructureTestSupport`. | `GetAvailablePort` has a time-of-check/time-of-use gap. A second test/process can bind the port before the dashboard or probe server starts; hosted-dashboard tests also compete for app startup time and inherited env. | Architectural for dashboard; trivially-isolatable for one-shot probe servers |
| `MCG_DOTNET_ISOLATED_ROOT`, stable slot roots, build artifacts, lease locks | `DotnetBuildEnvironmentManagerTests`, `FirewallSetupCommandTests`, `GoalAcceptanceVerifierTests`, `LocalProcessVerifierTests`, `GoalWorktreeIntegrationTests`. | Slot artifact roots and lease locks are intentionally shared within a run. Parallel tests can wait on the same lease, rotate/delete another test's artifacts, or deadlock when the suite itself runs inside a slot. | Architectural |
| SQLite state files: `.orchestrator/state.db`, `backlog.db`, `collaboration-items.db`, `run-events.db`, model function catalogs | `BacklogStoreTests`, `CollaborationItemStoreTests`, `GoalBacklogLinkTests`, `GoalMonitoringSubscriptionCommandTests`, `IdeationPlanTests`, `StatePersistenceAndPerformanceTests`, `SqliteOrchestratorStateRepositoryTests`, `RunEventStoreTests`, `ModelFunctionCatalogStoreTests`, `ConductorBatchLoopTests`, `DiscordGatewayTests`, `OperatorChannelTests`, `DashboardHostTests`, `CliCommandTests`, `GoalWorktreeIntegrationTests`, `GoalRefinementTests`, `ConductorDriverTests`. | Most tests create per-test temp roots and are safe serially. Parallel risk appears when helpers route through a shared workspace/root, when CLI persistent-state flows infer root from env/current directory, or when a test deliberately holds a SQLite lock and another test hits the same file. | Trivially-isolatable when temp-rooted; architectural for CLI/workspace integration |
| Repository/worktree directories and git config | `GoalWorktreeIntegrationTests`, `GoalWorktreeFastTests`, `GitCliTests`, `LauncherScriptTests`, `WorkerContextArtifactsCharacterizationTests`, `WorkerDispatchTests`, `ToolchainDetectionTests`, `SemanticAcceptanceTests`, `CliCommandTests`. | Per-test temp repositories are mostly safe, but worktree cleanup, acceptance, launcher, and git subprocess tests share global git executable/config and may leave locks or processes. | Architectural for full worktree integration; trivially-isolatable for synthetic source trees |
| Static default catalogs and read-only singletons | Many classes call `AgentCatalog.Default()`, `WorkerProfileCatalog.Default()`, `OperatorChannelCatalog.Default()`, `NullOperatorChannel.Instance`, and `EmptyRunEventStore.Instance`. | Source audit found these used as immutable/default values, not mutated process-wide. They are not currently blocking parallelism unless future tests mutate returned instances in place. | No action except keep immutable |

## Hazard Classification Summary

Trivially-isolatable:

- Core tests are effectively already in this bucket: in-memory kernels, injected clocks, literal paths, and unique temp files.
- Infrastructure tests that only need unique temp roots or ports can run in normal parallel collections after helper hardening: store tests, source/toolchain/semantic fixture tests, most rendering tests, and pure catalog/parser tests.
- One-shot `TcpListener` probe tests can stay parallel if the listener owns the bound port before the client connects; avoid `GetAvailablePort` followed by a later bind.

Fixture-refactor:

- Replace all remaining local `Console.SetOut` / `Console.SetError` helpers with `InfrastructureTestSupport.CaptureConsole` / `CaptureConsoleError`.
- Introduce a single env/current-directory isolation helper or xUnit collection fixture for process-global env mutations. Classes that mutate env but are not currently in a collection (`AdvanceLoopTests`, `GoalBacklogLinkTests`, `WorkspaceConsolidatorTests`, `WorkerProfileTests`, `CliCommandTests`, `GoalRefinementTests`) must be moved into explicit non-parallel collections before removing the assembly-wide switch.
- Replace static hooks (`GoalWorktrees.CleanupUtcNow`, `GoalWorktrees.SandboxAclHelper`) with injectable collaborators or a serial fixture.

Architectural:

- Keep process-spawning/worker-dispatch/worktree acceptance tests in a serialized lane until process ownership and cleanup can be made instance-scoped.
- Keep dotnet slot/build artifact tests in an isolated lane; this is the slowest and riskiest group because the production behavior intentionally shares lease roots.
- Treat hosted dashboard and app subprocess tests as a dedicated integration lane even after ordinary store/rendering tests are parallel.

## Wave 2 And Wave 3 Decomposition

Wave 2 should keep serial suite behavior green while making collections honest. Each item is scoped for one Complex dispatch.

1. Console capture consolidation
   - Files: `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConsoleCapture.cs`, `InfrastructureTestSupport.cs`, `CliHelpTests.cs`, `GoalWorktreeFastTests.cs`, `GoalRefinementTests.cs`, plus any direct `Console.SetOut` / `Console.SetError` call sites found by `rg`.
   - Outcome: no test-local direct console reassignment remains; `ConsoleOutMutation` can shrink or be removed after focused runs.

2. Environment and current-directory isolation fixture
   - Files: `AssemblyInfo.cs`, `InfrastructureTestSupport.cs`, `AdvanceLoopTests.cs`, `GoalBacklogLinkTests.cs`, `WorkspaceConsolidatorTests.cs`, `WorkerProfileTests.cs`, `ProviderDefaultTests.cs`, `ProviderProbeTests.cs`, `WorkerDispatchTests.cs`, `GoalAcceptanceVerifierTests.cs`, `LocalProcessVerifierTests.cs`.
   - Outcome: every class that mutates process env or `Environment.CurrentDirectory` is either migrated to injected options or assigned to one explicit non-parallel env collection; serial suite remains unchanged.

3. Static hook isolation for worktree cleanup
   - Files: `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.cs`, `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTests.cs`, `CliCommandTests.cs`, and related fake ACL helpers in those files.
   - Outcome: `GoalWorktrees` clock/ACL behavior is instance-scoped or guarded by a fixture, removing static delegate races before collection-level parallelism.

4. Dotnet slot/build artifact lane hardening
   - Files: `DotnetBuildEnvironmentManagerTests.cs`, `FirewallSetupCommandTests.cs`, `GoalAcceptanceVerifierTests.cs`, `LocalProcessVerifierTests.cs`, `GoalWorktreeTests.cs`, `src/Mcg.AgentOrchestrator.Infrastructure/DotnetBuildEnvironmentManager.cs`, and acceptance verifier/local verifier wrappers.
   - Outcome: build-slot tests keep a dedicated collection with per-run root and no cross-test lease deletion; non-slot tests can be split away from `IsolatedDotnetRoot`.

Wave 3 should enable parallelism progressively:

1. Remove `DisableTestParallelization = true` only after Wave 2 collections are complete; leave named non-parallel collections in place.
2. Run Core.Tests in normal xUnit parallel mode first. It has no current collection attributes or process-global mutation hazards.
3. Enable Infrastructure parallelism for pure/rendering/store classes while keeping `ProcessSpawning`, `ProviderEnvironment`, `EnvMutation`, `DotnetSlot`, and `HostedDashboard` collections serial.
4. Split process-spawning into smaller lanes if timing shows one class dominates: worker-dispatch, worktree/acceptance, launcher/git, and hosted-dashboard.
5. Re-measure full-suite timing on the same slot and only then consider raising xUnit max parallel threads. The reverted switch showed that enabling parallelism before isolation made the suite slower and flaky.

## End-State Time Estimate

Current reference from the task brief: full `Infrastructure.Tests` is about 12-13 minutes serial on a slot.

Expected end state after Wave 2/3 is not linear with CPU count because process-spawning, hosted-dashboard, and dotnet slot tests remain serialized. A realistic target is:

- Core.Tests: already parallel-safe by source audit; should overlap with Infrastructure or complete quickly as its own project.
- Infrastructure.Tests after collection parallelism: about 5-7 minutes on the same slot, with the longest remaining serialized lane likely `WorkerDispatchTests` / `GoalWorktreeIntegrationTests` / dotnet-slot acceptance tests.
- Full suite on one slot: about 6-8 minutes if Core and pure Infrastructure classes overlap behind the architectural lanes.

The first measured success criterion should be not just lower wall time, but zero rotating failures across repeated full-slot runs with the assembly-wide disable removed and named serial collections still active.
