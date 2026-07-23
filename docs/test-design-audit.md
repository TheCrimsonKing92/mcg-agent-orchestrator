# Test-Design Discipline Audit

Audit date: 2026-07-23

## Scope

Deep pass: `tests/Mcg.AgentOrchestrator.Infrastructure.Tests` against rules a-d and f, with emphasis on deterministic asserted paths, seamed I/O, narrowest layer, gate pinning, and silent no-ops.

Mechanical rule-e pass: all checked-in C# test projects under `tests/`:

- `tests/Mcg.AgentOrchestrator.Core.Tests`
- `tests/Mcg.AgentOrchestrator.Infrastructure.Tests`

Not audited: production source except when needed to understand a test-side boundary, scripts tests outside C# test projects, generated `bin/` and `obj/`, `.scratch`, prototype dashboard state, historical logs, and runtime orchestrator/dashboard state.

## Findings

| file:line | test/class | violated rule(s) | concrete evidence | risk |
|---|---|---|---|---|
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DashboardHostTests.cs:17` | `DashboardHostTests` | a, c, e | The class is only tagged `HostIntegration`, not placed in a serial collection, while tests spawn a real dashboard process at `DashboardHostTests.cs:44`, bind a reserved-then-freed port from `InfrastructureTestSupport.cs:181`, poll health over `HttpClient` at `DashboardHostTests.cs:48`, and assert JSON returned by the real Kestrel process. | high |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliHelpTests.cs:7` | `CliHelpTests` | a, c, e | The class has no serial collection. Helpers run real `git init` at `CliHelpTests.cs:509` and a real `dotnet exec Mcg.AgentOrchestrator.App.dll` at `CliHelpTests.cs:541`; assertions depend on subprocess stdout/stderr and real filesystem state. | high |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs:7` | `ConductorDriverTests` | a, e | The class has no serial collection but creates temp repositories and calls `GitCli.Run` through `RunGit` at `ConductorDriverTests.cs:152`. Representative tests initialize and mutate real repos at `ConductorDriverTests.cs:440` and read real branch facts before asserting conductor outcomes at `ConductorDriverTests.cs:475`. | high |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsModelSelection.cs:14` | `WorkerDispatchTestsModelSelection` | a, e | Unlike neighboring worker-dispatch files, this class has no `[Xunit.Collection]`. It inherits the real-git support path and directly asserts real branch/head data from `ReadGit` at `WorkerDispatchTestsModelSelection.cs:1091`. | high |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DashboardRenderingTests.cs:14` | `DashboardRenderingTests` | a, e, f | The class has no serial collection but builds real git repos in a rendering test at `DashboardRenderingTests.cs:2146`. Its local `RunGit` helper waits without a timeout at `DashboardRenderingTests.cs:3324`, so a hung git subprocess can stall rather than fail with bounded evidence. | high |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderProbeTests.cs:51` | `ProviderRegistryFallsBackWhenModelsProbeConnectionIsRefused` | a, b | The test manufactures a refused endpoint by reserving and releasing a loopback port via `GetAvailablePort`; another process can bind the port before the assertion path runs, and the network failure mode is not injected through a provider/probe seam. | medium |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CodexEgressProxyTests.cs:9` | `CodexEgressProxyTests` | a, b, e | The class has no serial collection and asserts real TCP tunnel behavior. `CodexEgressProxyObserveDoesNotCutIdleTunnel` waits on wall-clock time at `CodexEgressProxyTests.cs:82` and asserts measured idle duration at `CodexEgressProxyTests.cs:92`; the clock and socket boundary are both real. | medium |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProcessJobsTests.cs:20` | `WorkerProcessJobsTests` | a, b | The class is serialized, but assertions depend on live OS process identity and scheduling: it starts real long-running shells at `WorkerProcessJobsTests.cs:29`, records a real PID in SQLite at `WorkerProcessJobsTests.cs:33`, and asserts process liveness/death at `WorkerProcessJobsTests.cs:39`. | medium |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AdvanceLoopTests.cs:1120` | `DashboardContinuationServiceCompletesSubscriptionWatch` | a, b | The asserted transition is driven by a real `DashboardContinuationService`, real SQLite repository, `DateTimeOffset.UtcNow`, and `Task.Delay` polling at `AdvanceLoopTests.cs:1120`. The test asserts completion state at `AdvanceLoopTests.cs:1129` without a scheduler/clock seam. | medium |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DispatchProcessHostTests.cs:1096` | `DispatchProcessHostDrainsOutputHandlesBeforeExitFile` | a | The class is in `ProcessSpawning`, but this test still asserts a real hosted process and filesystem exit file. It polls `File.Exists` using `DateTimeOffset.UtcNow` and `Thread.Sleep` at `DispatchProcessHostTests.cs:1100`, then asserts the exit file at `DispatchProcessHostTests.cs:1112`. | medium |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/StatePersistenceAndPerformanceTests.cs:10` | `StatePersistenceAndPerformanceTests` | a, e | The class has no serial collection while using real SQLite/filesystem persistence. The concurrency test starts 12 real DB writers against one temp SQLite file at `StatePersistenceAndPerformanceTests.cs:428` and treats `Task.WhenAll` completion as the core assertion at `StatePersistenceAndPerformanceTests.cs:452`. | medium |
| `tests/Mcg.AgentOrchestrator.Core.Tests/AgentHarnessDocsDriftTests.cs:91` | `AgentHarnessDocsDriftTests` | a, e | Core.Tests has no collection definitions. This test reads the live repository's `AGENTS.md` and `CLAUDE.md` at `AgentHarnessDocsDriftTests.cs:95`, locating the repo through ambient current directory / base directory traversal at `AgentHarnessDocsDriftTests.cs:145`. | medium |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs:3556` | `GoalAcceptanceVerifierDedupesPolicyInjectionAgainstEquivalentManifestCheck` | d, f | The test asserts `result.Checks.All(c => c.Passed)` after a canned command queue, while the production verifier conditionally adds later checks only when prior `checks.All` is true (`GoalAcceptanceVerifier.cs:385` and `GoalAcceptanceVerifier.cs:390`). This validates the aggregate pass state, but does not pin each late gate input/output by name in this scenario. | low |

## Recommended Resolutions

| finding | recommendation |
|---|---|
| `DashboardHostTests.cs:17` | Move hosted-dashboard tests into a dedicated disabled-by-default serial collection, or replace process/Kestrel coverage with an in-memory host seam for API assertions and keep one opt-in process smoke. Bind ports by passing an already-owned listener/socket or host-managed dynamic port instead of reserve-then-free. |
| `CliHelpTests.cs:7` | Split CLI help parser/dispatcher assertions from app-process smoke tests. Put the app-process smoke path in `ProcessSpawning` or a dedicated serial CLI collection, and prefer in-process `CliCommandDispatcher.ExecuteCommand` for help text invariants. |
| `ConductorDriverTests.cs:7` | Add a conductor git/facts seam for these scenarios, or move real-git cases into a serial git collection with hardened `RunGit` timeouts and cleanup. Keep driver state-machine assertions on fake `GoalLifecycleFacts` where possible. |
| `WorkerDispatchTestsModelSelection.cs:14` | Add the class to an existing serial collection that covers real git/process-wide dispatch fixture behavior, or isolate branch/head assertions behind an injectable git facts provider and test that provider separately. |
| `DashboardRenderingTests.cs:14` | Replace the real worktree in the rendering test with a fake changed-file/facts provider. If real git remains, add a serial collection and make `RunGit` use bounded `WaitForExit(timeout)` with process-tree cleanup and stdout/stderr diagnostics. |
| `ProviderProbeTests.cs:51` | Inject an `HttpMessageHandler`/probe client or socket connector that deterministically throws connection-refused. If a live socket test remains, keep the listener owned until the client attempts the connection. |
| `CodexEgressProxyTests.cs:9` | Add a serial network collection and inject a clock/timer into idle-cut behavior. Convert duration assertions to manual-clock advancement; reserve live TCP for one bounded contract smoke. |
| `WorkerProcessJobsTests.cs:20` | Push PID identity, process liveness, and kill/release operations behind a process-control seam. Keep one real-process contract test in the serial collection; move owner filtering and registry behavior to fake process identities. |
| `AdvanceLoopTests.cs:1120` | Inject a test scheduler/clock into `DashboardContinuationService` and drive the continuation loop synchronously in tests. Keep SQLite persistence assertions separate from timing assertions. |
| `DispatchProcessHostTests.cs:1096` | Replace polling with an observable process-host seam or synchronization primitive. If the real host remains, use a bounded wait helper that fails with process stdout/stderr, exit code, and file path evidence. |
| `StatePersistenceAndPerformanceTests.cs:10` | Declare the class parallel-safety policy explicitly. Either move real SQLite concurrency tests to a serial DB collection or document unique-per-test DB isolation with a fixture that owns cleanup and contention knobs. |
| `AgentHarnessDocsDriftTests.cs:91` | Pass explicit document text or an explicit repository root fixture into the parser tests. Keep live repo drift checking in a small serial/docs contract test that declares its repo-file dependency. |
| `GoalAcceptanceVerifierTests.cs:3556` | For all-or-nothing gate scenarios, assert the ordered check names and late-gate receipts explicitly, and assert the command queue is fully consumed. Add a negative twin where a late check fails and the aggregate pass flips red. |

## Summary

Findings by rule:

- Rule a, deterministic asserted path: 12
- Rule b, seam every I/O boundary: 4
- Rule c, narrowest layer: 2
- Rule d, all-or-nothing gate pinning: 1
- Rule e, parallel-safety declaration: 10
- Rule f, no silent no-ops: 2

Findings by risk:

- High: 5
- Medium: 7
- Low: 1

Highest-leverage follow-on goals:

1. Create a serial real-git/process test collection policy and migrate `ConductorDriverTests`, `WorkerDispatchTestsModelSelection`, `DashboardRenderingTests`, and `CliHelpTests` into it or off real git entirely.
2. Extract deterministic seams for git/worktree facts used by conductor, dashboard preview, and worker dispatch tests.
3. Split hosted dashboard coverage into in-memory API tests plus one opt-in Kestrel smoke with owned-port binding.
4. Add clock/scheduler seams for continuation, egress idle timeout, and process-host polling tests.
5. Harden test subprocess helpers so every `Process.Start` path has bounded wait, process-tree cleanup, and stdout/stderr failure evidence.
6. Add a lightweight analyzer or test that flags test classes using `Process.Start`, `GitCli`, `TcpListener`, or real SQLite without either an approved collection or an explicit isolation fixture.
