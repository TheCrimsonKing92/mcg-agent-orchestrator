# Architecture

This is the authoritative map of the repository as it exists today. Project inventory is scoped
mechanically to `src/**/*.csproj` for production and `tests/**/*.csproj` for test/support. The
modular-monolith vocabulary later in this document is a proposed target, not a list of assemblies
that already exist.

## Current project topology

The `Direct production references` column records direct `ProjectReference` edges, not transitive
dependencies. `App` therefore lists Providers directly as well as through Infrastructure, while
Infrastructure has no reference to OperatorComms.

<!-- current-project-inventory:begin -->
| Project | Direct production references | Responsibility | Explicit non-responsibility |
| --- | --- | --- | --- |
| `src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj` | none | Domain model, application contracts, deterministic workflow policy, and conductor policy types. | No database, process, network, provider, CLI, dashboard, or host composition. |
| `src/Mcg.AgentOrchestrator.Infrastructure.Providers/Mcg.AgentOrchestrator.Infrastructure.Providers.csproj` | `src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj` | Concrete model-provider adapters, provider defaults, smoke behavior, and agent-catalog persistence. | No executable composition, conductor lifecycle, CLI, or dashboard. |
| `src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/Mcg.AgentOrchestrator.Infrastructure.OperatorComms.csproj` | `src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj` | Operator-channel implementations, including transport and message behavior behind Core contracts. | No goal-state persistence, conductor policy, or host-level channel composition. |
| `src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj` | `src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj`<br>`src/Mcg.AgentOrchestrator.Infrastructure.Providers/Mcg.AgentOrchestrator.Infrastructure.Providers.csproj` | SQLite persistence, process execution, worker artifacts, worktrees, build/test integration, and acceptance verification. | No CLI, dashboard, executable host, or OperatorComms implementation. |
| `src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj` | `src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj`<br>`src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj`<br>`src/Mcg.AgentOrchestrator.Infrastructure.Providers/Mcg.AgentOrchestrator.Infrastructure.Providers.csproj`<br>`src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/Mcg.AgentOrchestrator.Infrastructure.OperatorComms.csproj` | Executable composition root; it currently contains conductor coordination, CLI, dashboard, provider wiring, and operator-channel composition. | No reusable domain contracts, provider adapter implementations, OperatorComms implementations, or general persistence implementation. |
| `tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj` | n/a | Core policy tests and hermetic repository-documentation contracts. | No infrastructure or hosted-dashboard integration coverage. |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj` | n/a | Broad infrastructure and App orchestration coverage, including persistence, workers, worktrees, conductor behavior, and acceptance. | No ownership of the separately scoped CLI, dashboard, or provider-environment suites. |
| `tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj` | n/a | Dashboard API, rendering, and UI behavior tests against the dashboard code currently in App. | It is not a production Dashboard assembly and does not own general infrastructure tests. |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj` | n/a | CLI parsing, dispatch, command, and console-view contract tests. | No executable-host ownership or non-CLI infrastructure coverage. |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj` | n/a | Process-isolated provider environment and configuration behavior. | No general provider adapter ownership or orchestration coverage. |
| `tests/Mcg.AgentOrchestrator.TestSupport/Mcg.AgentOrchestrator.TestSupport.csproj` | n/a | Shared fixtures and helpers consumed by test assemblies. | It is not a runnable suite and contains no production behavior. |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/RealProcessShardProbe/Mcg.AgentOrchestrator.RealProcessShardProbe.csproj` | n/a | Executable fixture used to observe real-process shard behavior. | It is not a product host or a general-purpose test runner. |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/IsolatedDotnetProbe/Mcg.AgentOrchestrator.IsolatedDotnetProbe.csproj` | n/a | Minimal executable fixture for isolated `dotnet` invocation tests. | It is not product tooling or a standalone test suite. |
<!-- current-project-inventory:end -->

The resulting production graph is:

```text
Core                          -> none
Infrastructure.Providers      -> Core
Infrastructure.OperatorComms  -> Core
Infrastructure                -> Core, Infrastructure.Providers
App                           -> Core, Infrastructure,
                                 Infrastructure.Providers,
                                 Infrastructure.OperatorComms
```

The two other checked-in projects are deliberately outside this census:
`scripts/OrchestratorSqliteTools/OrchestratorSqliteTools.csproj` is a test-referenced SQLite
utility, and `tools/Mcg.HiddenLauncher/Mcg.HiddenLauncher.csproj` is a standalone Windows helper.
The solution and acceptance manifest serve different execution purposes and are not project-
inventory authorities.

## Current state and artifact ownership

Path ownership, persistence implementation, and runtime write authority are separate concerns.
Where the code has no single owner, this table records the gap instead of inventing one.

| State or artifact | Authoritative owner or seam today | Lifecycle and write authority |
| --- | --- | --- |
| Goal/kernel state (`.orchestrator/state.db`) | `OrchestratorWorkspace.SqliteStatePath` in App resolves the path; Infrastructure's `SqliteOrchestratorStateRepository` implements persistence. | Durable workspace state. Conductor ticks are the normal state writer; operator mutations are queued through typed intents. |
| Run events (`run-events.db`) | `OrchestratorWorkspace` resolves the path; Infrastructure's `SqliteRunEventStore` implements the append store. | Append-oriented run evidence retained and pruned by workspace storage maintenance. |
| Backlog (`backlog.db`) | `OrchestratorWorkspace` resolves the path; Infrastructure's `BacklogStore` owns persistence. | Repository-level durable work inventory spanning individual goals. |
| Collaboration (`collaboration-items.db`) | Infrastructure's `CollaborationItemStore` owns persistence, but it and App's `GoalBoardCommand` independently construct the path. | Durable collaboration items. Current gap: there is no single workspace path property. |
| Operator intents (`operator-intents.db`) | Infrastructure's `SqliteOperatorIntentStore.DatabaseFileName` is the shared filename seam and the store owns persistence. | Durable queued operator requests; the next conductor tick applies them as the goal-state writer and records an outcome. |
| Dogfood log (`dogfood-log.db`) | `OrchestratorWorkspace` resolves the path; Infrastructure's `DogfoodLogStore` owns persistence. | Durable goal-boundary evidence. `DOGFOOD_LOG.md` is only a pointer to this store. |
| Goal worktrees | Infrastructure's `GoalWorktrees` owns creation, lookup, and cleanup below `.orchestrator-worktrees`. | Created for file-touching goals, retained while work is reviewed, then removed after integration or explicit cleanup. |
| Dispatch artifacts | Infrastructure's `WorkerArtifactWriter` writes per-task context, prompts, output, heartbeat, and verification receipts. | Per-dispatch evidence retained under the workspace and later bounded by artifact-retention policy. |
| Acceptance/gate artifacts | Infrastructure's `GoalAcceptanceVerifier` emits gate attempts; App's acceptance coordination and `GoalArtifactRetentionPlanner` coordinate use and retention. | Per-attempt evidence retained for review and maintenance. Current gap: `acceptance-gate-attempts` is repeated as a literal rather than owned by one path seam. |

## Current runtime seams

- **Conductor:** coordination and loop behavior are concentrated in `App/Orchestration`; Core contains
  policy contracts, but there is no Runtime or Conductor production project. This is a currently
  over-centralized App responsibility.
- **Acceptance:** Infrastructure's `GoalAcceptanceVerifier` owns project selection, builds, tests,
  evidence collection, and acceptance checks in one broad verifier; App owns queueing, status,
  landing, and retention coordination. The verifier is presently over-centralized.
- **CLI:** parsing, handlers, persistence runners, and console views live in `App/Cli`; there is no
  CLI production project.
- **Dashboard:** API and dashboard hosting live in `App/Dashboard`; there is no Dashboard production
  project even though a dedicated Dashboard test assembly exists.
- **Providers:** concrete adapters live in `Infrastructure.Providers`; App still owns registry,
  client, defaults, smoke-runner, and scripted-provider composition.
- **Operator communications:** transport implementations live in `Infrastructure.OperatorComms`,
  while `App/Orchestration/OperatorChannelComposition` selects and hosts them. Typed operator
  intents are a separate persistence/control seam, not a communications transport.

## Placement rules

Today, add behavior at the seam that already owns its data and invariant: domain contracts and
deterministic policy in Core; external I/O implementations in the matching Infrastructure assembly;
state, process, worker, worktree, and acceptance implementations in Infrastructure; and host-only
composition, CLI, dashboard, and conductor coordination in App. Extend an existing seam before
creating a parallel path, and do not describe a test assembly as proof that a production boundary
already exists.

### Proposed modular-monolith target (not current assemblies)

This higher-level placement vocabulary is proposed by the architecture-map goal. It does not claim
that the named projects exist today, and it does not silently supersede the pending extraction
slices in [infrastructure-assembly-decomposition.md](infrastructure-assembly-decomposition.md).
That document remains the slice-by-slice execution record; future research must reconcile each
extraction with the compiled dependency graph.

| Target module | Intended placement |
| --- | --- |
| Core | Domain model, stable contracts, and deterministic policy without external I/O. |
| Runtime/Conductor | Goal/task lifecycle, scheduling, conductor coordination, and runtime state transitions. |
| Infrastructure | Persistence, processes, workers, worktrees, build/test execution, and acceptance mechanics behind Core/Runtime contracts. |
| CLI | Command parsing, command handlers, terminal presentation, and CLI-specific application flow. |
| Dashboard | Dashboard host, HTTP/API surface, rendering, and dashboard-specific application flow. |
| Adapters | Provider and operator-communications integrations with external systems. |
| App composition root | Executable startup and dependency wiring only; it selects modules and adapters without owning their behavior. |

## Canonical guidance

This map owns topology and placement, not operating or governance procedures:

- [Operator runbook](operator-runbook.md)
- [Repository conventions](repository-conventions.md)
- [Role capability matrix](role-capability-matrix.md)
- [Test-design discipline](test-design-discipline.md)
- [Dispositive decision discipline](dispositive-decision-discipline.md)
