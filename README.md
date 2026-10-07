# MCG Agent Orchestrator

This repository runs software development goals through local Codex CLI and Claude CLI workers, verifies their work, and lands accepted changes in `main`. Most commits in this history were written by the orchestrator's own worker agents and landed through its acceptance gates, while the operator sets goals and owns the design, as [Reading the history](#reading-the-history) explains. See the [architecture map](docs/architecture.md) for the projects, dependencies, and state ownership.

## Architecture

```mermaid
flowchart LR
    Operator[Operator: create a goal] --> Conductor[Conductor tick]
    Conductor --> Workers[Workers in a goal worktree]
    Workers --> Gate[Acceptance gate]
    Gate -->|allowlisted lanes over SSH| Executors[Remote executors]
    Executors -->|results bound to the candidate| Gate
    Gate --> Risk[Change-risk gate]
    Risk --> Landing[Landing in main]
    Gate -->|failure| Operator
    Risk -->|needs review| Operator
```

The [operator runbook](docs/operator-runbook.md#1-golden-path-conductor-first) describes the loop and its policy gates.

## How it works

An operator creates a goal with an objective and acceptance criteria. Automatic intake selects the Scout pipeline: Planner, Developer, Tester, and Reviewer; `--pipeline five-role` also includes a Researcher. The [intake planner](src/Mcg.AgentOrchestrator.App/Orchestration/GoalObjectivePlanner.cs) selects the pipeline and defines its task boundaries and role order.

The conductor creates an isolated git worktree, dispatches the workers, and waits for their results. After review, it runs the acceptance suite against the candidate and applies the change-risk gate. A passing candidate allowed by policy lands in `main`; failures, conflicts, and requests for human input return to the operator. The loop records the landing in SQLite and removes the worktree. Follow the [runbook](docs/operator-runbook.md#1-golden-path-conductor-first) for the complete operating sequence.

### Distributed verification

Acceptance gates are the throughput limit. They ran one at a time on the operator's workstation, which is also a daily-use machine. To relieve it, the gate can run allowlisted test lanes on remote Windows executors over SSH.

For each lane, the host pushes the candidate commit to the executor's repository, queues the job, and polls its status and heartbeat until the TRX results are ready to fetch. A remote result is admitted only when the executor reports the commit, tree, main revision, test-filter hash, and acceptance-manifest identity the gate requested. A discrepancy, a lapsed heartbeat, or a transport failure returns the lane to local execution. [RemoteLaneCoordinator](src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteLaneCoordinator.cs) decides admission, and [SshRemoteLaneExecutor](src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/SshRemoteLaneExecutor.cs) owns the transport.

The first remote runs, on 2026-10-07, passed 396 and 400 tests with every binding field matched, and both goals landed.

An abridged executor-health record from the first accepted run:

```json
{"observed_at":"2026-10-07T00:59:59Z","executor_id":"mcg-exec-1",
 "lane":"infrastructure tests: Cli","outcome":"accepted",
 "expected":{"commit":"62a48770","tree":"1b60839e","main":"cb72d4fe","filter":"9aeb3b7f"},
 "observed":{"commit":"62a48770","tree":"1b60839e","main":"cb72d4fe","filter":"9aeb3b7f"}}
```

## Design decisions

### One authoritative state and one writer

Concurrent writers overwrote the state of an already landed goal and dispatched work onto it again. The [landed-state incident](docs/incidents/2026-07-15-landed-state-races.md) explains the failure; the [state model](docs/state-model.md) separates persisted authority from projections. Recovery commands such as `retry`, `progress`, and `verify-manual` submit typed intents, and the conductor tick applies them as the sole writer of `state.db`, as described in the [runbook](docs/operator-runbook.md#8-operating-discipline-hard-won).

### Attribute failures before landing

Tests merged past as pre-existing red caused later acceptance gates to fail and throttled the board. The [dormant-red incident](docs/incidents/2026-07-15-dormant-reds-gate-monopoly.md) motivates keeping main green; [baseline attribution](docs/baseline-attribution-replay.md) requires executed evidence to distinguish inherited failures from candidate failures.

### Reserve resources for acceptance

Worker self-verification occupied the build slots that acceptance needed, leaving verified goals waiting to land. The [slot-starvation incident](docs/incidents/2026-07-14-acceptance-gate-slot-starvation.md) motivates [acceptance resource isolation and cross-process leases](docs/acceptance-gate-resource-isolation.md).

### Measure the mechanism

Attribution partitions silently ran sequentially because their caller supplied no stable build-slot lease, selecting the verifier's serial fallback. The [cohort attribution case study](docs/case-studies/cohort-attribution-partitions.md) traces that failure and the lease fix. The [test-design discipline](docs/test-design-discipline.md) requires assertions about mechanisms rather than real elapsed time, and the [role capability matrix](docs/role-capability-matrix.md) assigns evidence to roles that can produce it.

## Reading the history

A goal landed on its own appears as an `Integrate goal/<id>` merge commit; goals landed together as a merge train appear as their rebased commits in order, without an Integrate merge. Older worker commits use templated subjects beginning with the task purpose, such as `Implement the scoped slice and keep changes narrow.:`, followed by the worker's first words. Current worker commits use `<Role>(<goal-id8>): <goal title>` with `Goal`, `Task-Id`, and `Dispatch` trailers, as defined by [OrchestratorCommitMessage.ForWorker](src/Mcg.AgentOrchestrator.Core/Domain/OrchestratorCommitMessage.cs) and used by the [dispatch committer](src/Mcg.AgentOrchestrator.Execution/Processes/DispatchWorktreeCommitter.cs).

`Integrate main into goal/<id> before <Role> dispatch` records a branch refresh for the named role, as defined by [pre-dispatch integration](src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.PreDispatchIntegration.cs). Operator commits have hand-written subjects outside these generated patterns; read the diffs to see the resulting changes.

Goal briefs and orchestrator state live under `.orchestrator/`, which is [ignored by git](.gitignore), so the log does not contain the complete goal context. The earlier operator diary is preserved in [docs/history/handoff-2026-09.md](docs/history/handoff-2026-09.md).

## Status and limits

The system is Windows only, run by a single operator, and dependent on locally installed Codex CLI and Claude CLI workers. It is not packaged for other users. The [CLI reference](docs/cli-reference.md) describes the local setup and worker bridges. The code is published for reading with all rights reserved. Acceptance lanes can optionally run on remote Windows executors; see [Distributed verification](#distributed-verification).

The operator surface is the command line and the event log, described in the [runbook](docs/operator-runbook.md#4-observe-whats-happening).

## Running it

Start with the [operator runbook](docs/operator-runbook.md) for setup, operation, and recovery. The [CLI reference](docs/cli-reference.md) holds the command and setup details. [global.json](global.json) pins the .NET SDK; the sanctioned test command is `scripts/Invoke-TestSummary.ps1 -Target <project>`.

## Projects

The current project inventory is derived from `src/**/*.csproj` (6 production projects) and
`tests/**/*.csproj` (10 test/support projects). See the
[architecture map](docs/architecture.md) for the dependency graph, state ownership, current seams,
and the separately labelled modular-monolith target.

<!-- current-project-inventory:begin -->
| Project | Responsibility | Does not own |
| --- | --- | --- |
| `src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj` | Domain model, workflow contracts, and deterministic policy types. | Database persistence, external adapters, or hosting. |
| `src/Mcg.AgentOrchestrator.Infrastructure.Providers/Mcg.AgentOrchestrator.Infrastructure.Providers.csproj` | Concrete model-provider adapters, provider defaults, smoke checks, and agent-catalog persistence. | Host composition or orchestration lifecycle. |
| `src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/Mcg.AgentOrchestrator.Infrastructure.OperatorComms.csproj` | Operator-channel implementations and message transport behavior. | Goal state, conductor policy, or host composition. |
| `src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj` | SQLite stores, process and worker execution, worktrees, and acceptance verification. | CLI or executable composition. |
| `src/Mcg.AgentOrchestrator.Execution/Mcg.AgentOrchestrator.Execution.csproj` | SQLite persistence, process execution, worker dispatch and artifacts, and verification records. | Worktrees, build environments, acceptance gates, or Infrastructure and App references. |
| `src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj` | Headless executable composition root containing the conductor, CLI, maintenance, and application contracts. | ASP.NET hosting or HTTP endpoints. |
| `tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj` | Core policy and repository-documentation contract tests. | Infrastructure integration coverage. |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj` | Broad infrastructure, orchestration, worker, workspace, and acceptance tests. | The separately scoped CLI and provider-environment suites. |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Acceptance/Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests.csproj` | Focused acceptance execution-owner and invocation-pipeline tests. | Broad infrastructure, CLI, or provider-environment coverage. |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Execution/Mcg.AgentOrchestrator.Infrastructure.Execution.Tests.csproj` | Execution persistence, process identity, sandbox boundary, and build-log tests. | App, broad infrastructure, CLI, or provider-environment coverage. |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj` | CLI parsing, command, and console-contract tests. | Executable hosting or non-CLI infrastructure coverage. |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj` | Process-isolated provider environment and configuration tests. | General provider implementation ownership. |
| `tests/Mcg.AgentOrchestrator.TestSupport/Mcg.AgentOrchestrator.TestSupport.csproj` | Shared test fixtures and helpers. | A runnable test suite or production behavior. |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/RealProcessShardProbe/Mcg.AgentOrchestrator.RealProcessShardProbe.csproj` | Executable fixture for real-process shard tests. | Product hosting or general test execution. |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/IsolatedDotnetProbe/Mcg.AgentOrchestrator.IsolatedDotnetProbe.csproj` | Minimal executable fixture for isolated `dotnet` invocation tests. | Product tooling or a standalone test suite. |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/ConsoleIoProbe/Mcg.AgentOrchestrator.ConsoleIoProbe.csproj` | Executable fixture for redirected Console I/O preservation tests. | Product tooling or a standalone test suite. |
<!-- current-project-inventory:end -->

`scripts/OrchestratorSqliteTools/OrchestratorSqliteTools.csproj` and
`tools/Mcg.HiddenLauncher/Mcg.HiddenLauncher.csproj` are outside that `src`/`tests` inventory boundary.
The checkout-local launcher remains `mcg-orchestrator.cmd`; Windows publishing is handled by
`scripts/publish-headless.ps1`.
