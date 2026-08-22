# God-Class Decomposition Plan

Date: 2026-07-10. Scope: kickoff artifact for backlog `877c430d` product god-class extraction and `dd7ce2e2` test-class splitting. This document is inventory and sequencing only; each slice below is intended to become a separate backlog goal.

## Receipts

Inventory commands used from the goal worktree:

- Line counts: `Get-ChildItem -Path src,tests -Recurse -Filter *.cs ... | Measure-Object -Line`, excluding `bin` and `obj`.
- Churn: `git log --since=2026-05-11 --follow --oneline -- <path> | Measure-Object -Line`.
- Acceptance lanes: `scripts/Invoke-InfrastructureTestPartition.ps1 -List`, `GoalAcceptanceVerifier.InfrastructureTestLanes`, `GoalAcceptanceVerifier.BuildPolicyShardPlan`, and `RepositoryTestImpactPlanner.Plan`.

Acceptance lane model used here:

- Infrastructure partitions: `Cli`, `WorkerDispatch`, `GoalWorktree`, `Dashboard`, `Conductor`.
- Directly changed Infrastructure test files can run as focused changed-test-class filters. In full infrastructure acceptance, they belong to the matching partition above.
- App/Cli changes select focused CLI infrastructure tests when the changed App scope stays under `src/Mcg.AgentOrchestrator.App/Cli/`.
- App/Dashboard or App/Dashboard/Api changes select focused dashboard infrastructure tests when the changed App scope stays under one dashboard/API subsystem.
- Shared Infrastructure changes map to broad Infrastructure test coverage; treat these as touching all five infrastructure partitions unless a characterization goal proves a narrower filter.
- Core source changes map to Core tests plus downstream Infrastructure tests through the `GoalAcceptanceVerifier` project dependency closure.
- `GoalAcceptanceVerifier.cs` and `GoalAcceptanceVerifierTests.cs` are not explicit full-shard triggers. Commit `88e4dc59` removed those special cases on 2026-07-19; current selection uses changed-project mapping and filename-derived focused test filters.

Prior art already exists: `AgentOrchestratorKernel.*.cs` is a successful Core partial-class split, with the largest fragment now `AgentOrchestratorKernel.GoalLifecycle.cs` at 706 lines and 33 commits in the last 60 days. `CliCommandHandlers.*.cs` is also partially split, but `CliCommandHandlers.Goals.cs` remains a 3,054-line hotspot while siblings such as `CliCommandHandlers.Workers.cs` at 509 lines and `CliCommandHandlers.Backlog.cs` at 305 lines show the intended direction.

## 2026-08-13 refresh: what happened in a month

Re-measured on main at `603d4714`. **The inventory table below is the 2026-07-10 snapshot and its numbers are
now wrong by up to 5x. Use this section for current sizes.**

The result is unambiguous and worth stating plainly: **every file this plan attacked shrank. Every file it
did not attack grew.**

Where Wave 1 and early Wave 2 slices landed:

| File | 2026-07-10 | 2026-08-13 | |
| --- | ---: | ---: | --- |
| `tests/.../CliCommandTests.cs` | 8,143 | **1,065** | split into `CliCommandTests.<Concern>.cs` |
| `tests/.../WorkerDispatchTests.cs` | 8,096 | **2,331** | split into `WorkerDispatchTests<Concern>.cs` |
| `tests/.../GoalWorktreeTests.cs` | 5,512 | **2,087** | split |
| `src/.../Workspaces/GoalWorktrees.cs` | 1,567 | **290** | extracted |
| `src/.../Cli/CliCommandHandlers.Goals.cs` | 3,054 | **1,756** | partially extracted |

Where nothing was applied:

| File | 2026-07-10 | 2026-08-13 | Growth |
| --- | ---: | ---: | ---: |
| `tests/.../ConductorBatchLoopTests.cs` | 2,996 | **12,617** | 4.2x |
| `tests/.../GoalAcceptanceVerifierTests.cs` | 1,731 | **9,178** | 5.3x |
| `src/.../Workspaces/GoalAcceptanceVerifier.cs` | 1,698 | **8,790** | 5.2x |
| `tests/.../ConductorDriverTests.cs` | 1,512 | **8,054** | 5.3x |
| `src/.../Orchestration/ConductorDriver.cs` | 1,201 | **5,035** | 4.2x |
| `src/.../Orchestration/ConductorBatchLoop.cs` | 1,234 | **4,596** | 3.7x |
| `src/.../Processes/BackgroundDispatchRunner.cs` | 2,156 | **4,499** | 2.1x |
| `tests/.../DashboardRenderingTests.cs` | 3,068 | **3,751** | 1.2x |
| `src/.../Cli/CliPersistentStateRunner.cs` | 1,178 | **3,189** | 2.7x |
| `src/.../Workers/WorkerProfileDispatcher.cs` | 1,090 | **2,268** | 2.1x |
| `src/.../Application/DispatchFailureClassifier.cs` | 1,451 | **2,307** | 1.6x |

The approach is proven. It stopped being applied, and the untouched Conductor/Acceptance cluster absorbed the
growth. `ConductorBatchLoopTests.cs` is now the largest file in the repository.

### Corrections to the original sequencing

1. **`ConductorDriverTests.cs` was ranked 16th and is now 8,054 lines.** Its product file
   `ConductorDriver.cs` (5,035) was a merge-conflict point three separate times on 2026-08-13 alone. Both
   belong near the front of the queue, not at position 16.
2. **`GoalAcceptanceVerifierTests.cs` was deferred to "after the three larger test splits."** Those three
   have landed, so its precondition is met — and at 9,178 lines it is second-largest. It is due now.
3. **The Wave 1 fragment lists predate the growth.** `ConductorBatchLoopTests.cs` quadrupled after its six
   fragments were named. Treat those lists as starting points and re-derive boundaries from the current
   content; do not force methods into a fragment to make the original list come out even.
4. **Dashboard tests should leave `Infrastructure.Tests` entirely**, not merely be split.
   `DashboardRenderingTests.cs` (3,751), `DashboardHostTests.cs` (600),
   `DashboardDispatchStartFailureEndpointTests.cs` (358) and `DashboardValidationHarnessTests.cs` (74) total
   4,783 lines of a human-interface concern sitting on the orchestrator's acceptance critical path. The
   dashboard is not on the conductor's critical path and the orchestrator runs without it.

### 2026-08-22 GoalAcceptanceVerifier test split

Wave 1 item 6 now uses one class per file because changed-test selection derives a class filter from each
changed filename and focused-evidence sibling resolution expects one declaring file per class. The four
existing secondary classes moved unchanged to `AcceptanceOutputCaptureTests.cs`,
`HermeticVerificationEnvironmentTests.cs`, `RealProcessShardAlphaSmokeTests.cs`, and
`RealProcessShardBetaSmokeTests.cs`. The dominant build-slot class became an abstract shared-helper base with
eleven concrete `GoalAcceptanceVerifierDotnetBuildSlotTests<Topic>` siblings:

1. `GateHeartbeat` owns run-scoped heartbeat and hung-child coverage.
2. `RunnerInvocationAndTrx` owns runner selection, invocation arguments, and TRX diagnostics.
3. `FocusedEvidence` owns focused-selection parsing, routing, and dual-arm evidence.
4. `SlotGateJobResources` owns build-slot gating, job receipts, lock self-heal, and timeout diagnostics because those paths share owned-process and heartbeat helpers.
5. `ConcurrentShardScheduling` owns resource-key scheduling and observed lane ordering.
6. `ShardReceipts` owns attempt artifacts, heartbeat files, and owner-result isolation.
7. `VerdictAndBuildCache` owns partition verdict reuse and base-build caching because both share partition-key hooks.
8. `PolicyShardScope` owns change-scope routing, policy injection, and solution-check substitution.
9. `AdvisoryChecks` owns advisory grep, file, command, and missing-criteria behavior.
10. `TestTamperGuard` owns declared-removal and assertion-tamper controls.
11. `TrustedBaselineDiscovery` owns trusted-baseline retry, project discovery, and deleted-file scope.

This differs from the original six-topic starting list because criterion parsing/check conversion and
forbidden-generated-path coverage already belong to the retained `GoalAcceptanceVerifierTests` class, while
focused evidence, concurrent scheduling, and verdict caching grew into independent concerns after that list
was written. Every build-slot-derived fragment and both real-process smoke classes retain the serial
`JobAccounting` collection; the non-process classes retain `GoalAcceptanceVerifier`. The manifest's literal
`GoalAcceptanceVerifierDotnetBuildSlotTests` lane token still matches every prefixed fragment, and the
full-shard behavior is unchanged: no hard-coded trigger exists to narrow. Extracted classes share the local
abstract verifier test base so focused-evidence sibling expansion can follow that family across files; the
source/reflection-only parity guard is collection-free because it mutates no shared state.

### Cross-reference

`docs/compensation-stack-audit-2026-08-13.md` reaches the same targets from the runtime side and supplies the
*why* this plan lacks: `GoalAcceptanceVerifier` is a home-grown CI scheduler compensating for test topology,
and `BackgroundDispatchRunner` reconstructs typed worker outcomes from free-form logs. Read them together —
this document says what to split, the audit says what the split is worth.

## Ranked Inventory

**Snapshot dated 2026-07-10. Line counts superseded by the refresh above; retained for churn data and
rationale.**

Score is `lines x churn x lane breadth`. Lane breadth is intentionally coarse: it measures how many acceptance partition lanes a change is expected to exercise, not how many tests are inside the lane.

| Rank | File / class | Lines | 60-day churn | Acceptance lanes exercised | Lane breadth | Score | Notes |
| --- | --- | ---: | ---: | --- | ---: | ---: | --- |
| 1 | `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs` | 8,096 | 203 | `WorkerDispatch` | 1 | 1,643,488 | Largest churn count; `[Collection("EnvMutation")]` must stay with every extracted fragment that mutates environment. |
| 2 | `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.cs` | 8,143 | 172 | `Cli` | 1 | 1,400,596 | Largest file; `[Collection("GoalWorktreeCleanupHooks")]` makes fixture movement important. |
| 3 | `src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs` | 2,156 | 106 | Broad Infrastructure: `Cli`, `WorkerDispatch`, `GoalWorktree`, `Dashboard`, `Conductor` | 5 | 1,142,680 | Highest product risk: process lifecycle, worker result parsing, commits, cancellation, diagnostics, build-daemon cleanup. |
| 4 | `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTests.cs` | 5,512 | 122 | `GoalWorktree` | 1 | 672,464 | `[Collection(TestCollections.GoalWorktreeCleanupHooks)]`; covers workspace lifecycle, acceptance, cleanup, orphan sweep, and SQL tool surfaces. |
| 5 | `src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Goals.cs` | 3,054 | 154 | Focused CLI infrastructure tests / `Cli` partition | 1 | 470,316 | Existing partial class still owns goal lifecycle, backlog intake, conduct, workspace, acceptance, recovery, and landing commands. |
| 6 | `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DashboardRenderingTests.cs` | 3,068 | 150 | `Dashboard` | 1 | 460,200 | Dashboard/API rendering and report-preview behavior; no collection attribute found. |
| 7 | `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs` | 1,698 | 44 | Full shard trigger: all Infrastructure partitions plus Core tests when relevant | 5 | 373,560 | Acceptance policy, manifest loading, shard receipts, grep/file checks, process runner, tamper checks. |
| 8 | `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs` | 1,731 | 39 | Changed-project Infrastructure mapping with filename-derived focused filters | 5 | 337,545 | Not special-cased by `GoalAcceptanceVerifier.FullShardReason`; extraction must preserve filename/class filter alignment and acceptance receipts. |
| 9 | `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.cs` | 1,567 | 43 | Broad Infrastructure: chiefly `GoalWorktree`, with process/build cleanup spillover | 5 | 336,905 | Worktree create/remove/rebase/sweep, cleanup backoff, lock holders, sandbox ACL reset. |
| 10 | `src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs` | 1,090 | 61 | Broad Infrastructure, dominated by `WorkerDispatch` | 5 | 332,450 | Subscription preflight, profile resolution, model selection, guardrails, template variables. |
| 11 | `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs` | 1,201 | 54 | Broad Infrastructure, dominated by `Conductor` | 5 | 324,270 | Advance state machine, dispatch/start, landing, recording, cleanup, evidence extraction. |
| 12 | `src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs` | 1,178 | 52 | Broad Infrastructure, CLI and acceptance-heavy | 5 | 306,280 | Persistent command transaction routing, dispatch/refresh identity capture, acceptance guard rails. |
| 13 | `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs` | 1,234 | 46 | Broad Infrastructure, dominated by `Conductor` | 5 | 283,820 | Loop orchestration, parallel acceptance, persistence, set-aside/readmit, cleanup. |
| 14 | `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTests.cs` | 2,996 | 64 | `Conductor` | 1 | 191,744 | Batch-loop, parallel acceptance, dependency completion, set-aside/readmit, reaping/detach coverage. |
| 15 | `src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs` | 1,451 | 47 | Core tests plus downstream Infrastructure tests | 2 | 136,394 | Provider failure taxonomy, worker result capability, retry/preflight evidence parsing. |
| 16 | `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs` | 1,512 | 38 | `Conductor` | 1 | 57,456 | Driver action classification, landing, recovery, policy outcomes. |
| 17 | `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AdvanceLoopTests.cs` | 1,152 | 35 | `Conductor` | 1 | 40,320 | `[Collection("EnvMutation")]`; dashboard continuation/supervisor advance behavior. |
| 18 | `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DispatchProcessHostTests.cs` | 1,180 | 29 | `WorkerDispatch` | 1 | 34,220 | `[Collection("ProcessSpawning")]`; host launch, shims, accounting, integrity labeler behavior. |
| 19 | `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalMonitoringSubscriptionCommandTests.cs` | 1,279 | 25 | Remainder / monitoring-adjacent Infrastructure | 1 | 31,975 | Monitoring command event envelope and subscription display behavior. |
| 20 | `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LauncherScriptTests.cs` | 1,100 | 25 | Remainder / process-spawning Infrastructure | 1 | 27,500 | `[Collection("ProcessSpawning")]`; launcher script and worker sandbox helpers. |

## Wave 1: Test-Class Splits

**Status as of 2026-08-13:** items 1, 2 and 3 have landed (see the refresh section for measured results).
Item 5 is in flight as goal `732af577`. Item 4 is superseded by extracting dashboard tests to their own
project rather than splitting them in place. Item 6's stated precondition — "after the three larger test
splits" — is now satisfied, and at 9,178 lines it is the largest remaining item in this wave.

These should land first because they reduce future verification cost without changing product behavior. Timing rationale: `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/README.md` records a 565s full Infrastructure run versus 23.9s for a focused CLI help filter. The partition helper already exposes named filters for the largest areas, but several lanes are still blocked behind 3k-8k monolithic files. Splitting those files lets `RepositoryTestImpactPlanner.BuildChangedTestClassFilter` and policy shard receipts run narrower class filters for follow-up goals.

1. Split `WorkerDispatchTests.cs` into dispatch preparation, subscription preflight, worker result classification, sandbox/low-integrity, and model-selection classes. Move `[Xunit.Collection("EnvMutation")]` to every fragment that mutates provider/profile/env state; fragments that only inspect pure planner output should avoid the collection. Expected shard-time effect: future changes touching one worker-dispatch concern can run `FullyQualifiedName~WorkerDispatch<Concern>Tests` instead of the current 8,096-line `WorkerDispatchTests` filter inside the `WorkerDispatch` partition. Disjointness: serialize the split itself with any `BackgroundDispatchRunner` or `WorkerProfileDispatcher` extraction because those goals will edit adjacent tests; once split, pure preflight/model-selection fragments can run concurrently with process-host fragments. Acceptance shape: only test-file moves/renames plus namespace-preserving classes, no assertion behavior changes, and focused `WorkerDispatch` partition evidence.

2. Split `CliCommandTests.cs` into goal lifecycle commands, backlog/intake commands, attention commands, subscription/dispatch commands, terminal sweep commands, and persistent-runner command classes. Keep `[Xunit.Collection("GoalWorktreeCleanupHooks")]` only on fragments that replace cleanup hooks or manipulate goal worktrees; CLI parser/output-only fragments should be free of that serial collection if characterization proves no hook mutation. Expected shard-time effect: changed CLI command tests can become class-specific filters instead of a single 8,143-line `CliCommandTests` target, while App/Cli product changes can still use the focused CLI infrastructure lane. Disjointness: serialize the initial split with `CliCommandHandlers.Goals.cs` extraction; after the split, backlog/intake and attention fragments are disjoint from acceptance/workspace fragments. Acceptance shape: mechanical test class split, fixtures preserved, no product changes, same facts discoverable by fully qualified names.

3. Split `GoalWorktreeTests.cs` into worktree creation/resolution, remove/cleanup, acceptance/landing, rebase/merge, orphan/ephemeral sweep, and SQLite/tooling classes. Preserve `[Xunit.Collection(TestCollections.GoalWorktreeCleanupHooks)]` on fragments using cleanup hook seams; process-free metadata tests can drop it only after a characterization run confirms no global hook mutation. Expected shard-time effect: the `GoalWorktree` partition no longer has to run a 5,512-line class for cleanup-only or acceptance-only follow-ups. Disjointness: serialize the split with `GoalWorktrees.cs` extraction and `CliCommandHandlers.Goals.cs` acceptance/workspace extraction; orphan/ephemeral sweep tests can later run beside acceptance CLI tests if file sets stay separated. Acceptance shape: no changed assertions, all helper methods remain local or moved to narrow shared test support.

4. Split `DashboardRenderingTests.cs` into renderer HTML, dashboard API DTO/mapping, report previews/test impact, host/process diagnostics, and validation-harness rendering tests. No serial collection attribute is present today; keep fragments collection-free unless a host/process test already requires a separate fixture. Expected shard-time effect: dashboard-only App changes can keep using focused dashboard infrastructure tests while test-only changes can select a much smaller changed-test-class filter than the current 3,068-line file. Disjointness: can run concurrently with WorkerDispatch and GoalWorktree test splits; serialize with any Dashboard API or rendering product extraction. Acceptance shape: class split only, same snapshot/assertion coverage, focused Dashboard partition evidence.

5. Split `ConductorBatchLoopTests.cs` into loop scheduling, parallel acceptance, dependency completion, set-aside/readmit, persistence failure, and reaping/detach classes. Preserve any local fake repositories or clocks with the fragment that owns them; no collection attribute is present today. Expected shard-time effect: conductor follow-ups can run class-specific filters within the `Conductor` partition instead of the 2,996-line batch-loop file. Disjointness: can run concurrently with Dashboard and WorkerDispatch splits; serialize with `ConductorBatchLoop.cs` and `ConductorDriver.cs` extractions. Acceptance shape: no product changes, tests remain in the Infrastructure test project, and existing partition helper still lists the conductor lane.

6. Split `GoalAcceptanceVerifierTests.cs` only after the three larger test splits. Suggested fragments: manifest/scoped-check planning, policy shard receipts, criterion parsing/check conversion, test-tamper detection, forbidden generated paths, and process runner timeout/diagnostics. Keep `[Xunit.Collection(TestCollections.JobAccounting)]` only on process/accounting fragments. Expected selection effect: test-only changes use filename-derived focused class filters, so split filenames and class names must stay aligned while the existing changed-project mapping remains intact. Disjointness: serialize with any `GoalAcceptanceVerifier.cs` extraction; can run after other test splits. Acceptance shape: class split plus explicit evidence that every fragment remains selected by its filename-derived filter and existing manifest lane token.

## Wave 2: Product Extractions

Every product extraction below is behavior-preserving and characterization-tests-first. The characterization prerequisite names the test surface that must exist before extracting, usually a Wave 1 fragment.

1. Extract `BackgroundDispatchRunner` worker-result completion classification into `WorkerDispatchCompletionClassifier`. Responsibility: decide whether stdout/stderr/artifacts/worker result fields prove completion, no-change completion, failed worker build checks, sandbox-blocked commits, or provider failure. Dependencies crossing the seam: `TaskSpec`, `TaskVerificationRecord`, `DispatchFailureClassifier`, worker-result artifact paths, and stdout/stderr readers. Characterization prerequisite: split `WorkerDispatchTests` must include a `WorkerDispatchCompletionClassifierTests` or equivalent fragment covering no-change, failed worker build check, sandbox commit blocked, successful worker result, and provider failure paths. Disjointness: serialize with `WorkerDispatchTests` split and any `DispatchFailureClassifier` extraction; disjoint from Dashboard and GoalWorktree cleanup slices. Backlog-ready acceptance: new internal classifier type, `BackgroundDispatchRunner` delegates to it, existing behavior and focused WorkerDispatch partition pass.

2. Extract `BackgroundDispatchRunner` process refresh/recovery diagnostics into `DispatchProcessRecoveryService`. Responsibility: read heartbeats/exit files, classify hung or completed processes, build recovery diagnostics, and write refresh diagnostics without owning worker result parsing. Dependencies crossing the seam: `TaskProcessRecord`, `DispatchRefreshOutcome`, heartbeat JSON, process killer abstraction, clock, and file system reads. Characterization prerequisite: `WorkerDispatchTests` process-refresh fragment plus `DispatchProcessHostTests` coverage for heartbeat and resource accounting. Disjointness: can run concurrently with `WorkerProfileDispatcher` model-selection extraction after Wave 1, but serialize with other `BackgroundDispatchRunner` slices. Backlog-ready acceptance: one new service with injectable file/process seams and no changes to commit logic.

3. Extract `BackgroundDispatchRunner` commit-on-behalf behavior into `DispatchWorktreeCommitter`. Responsibility: inspect dirty paths, build orchestrator commit subjects, commit worker edits, and report commit diagnostics. Dependencies crossing the seam: `GitCli`, `GoalId`, `TaskSpec`, worktree head/status, and worker summary extraction. Characterization prerequisite: a WorkerDispatch test fragment covering result-commit, dirty worktree, subject truncation, and git failure diagnostics. Disjointness: serialize with `CliCommandHandlers.Goals` sandbox-blocked commit handling and other `BackgroundDispatchRunner` slices; disjoint from Conductor and Dashboard slices. Backlog-ready acceptance: `BackgroundDispatchRunner` delegates commit creation; commit subjects and failure messages stay byte-for-byte stable in tests.

4. Extract `GoalAcceptanceVerifier` policy shard planning into `AcceptancePolicyShardPlanner`. Responsibility: map changed files to projects, dependency closure, full-shard reasons, shard evidence, and runnable/skipped policy checks. Dependencies crossing the seam: `RepositoryChangeClassifier`, manifest check records, project constants, environment flags. Characterization prerequisite: `GoalAcceptanceVerifierTests` policy-shard fragment must cover docs-only/no-changed-files, App/Cli focused checks, broad Infrastructure expansion, full-shard override, build-system changes, and filename-derived focused selection for acceptance-verifier tests. Disjointness: serialize with any `RepositoryTestImpactPlanner` or acceptance manifest changes; disjoint from WorkerDispatch completion extraction. Backlog-ready acceptance: new planner type, unchanged shard receipt text, unchanged current full-shard reasons and focused-selection behavior.

5. Extract `GoalAcceptanceVerifier` deterministic check execution into `AcceptanceCheckRunner`. Responsibility: run grep/file-exists/command/dotnet checks, timeouts, retry reads, stdout/stderr capture, and result formatting. Dependencies crossing the seam: runner delegate, `AcceptanceManifestCheck`, `AcceptanceCheckResult`, timeout constants, stable slot lease. Characterization prerequisite: `GoalAcceptanceVerifierTests` check-runner fragment covering command timeout, stdout/stderr paths, grep present/absent, file exists, and retry read behavior. Disjointness: serialize with policy shard extraction because both edit `GoalAcceptanceVerifier.cs`; disjoint from GoalWorktrees cleanup extraction. Backlog-ready acceptance: `RunAsync` becomes orchestration, check result summaries remain stable.

6. Extract `GoalWorktrees` cleanup/backoff/orphan sweep into `GoalWorktreeCleanupService`. Responsibility: delete worktree directories, reset sandbox ACLs, reap recorded worker processes, record/clear cleanup-needed state, and manage orphan cleanup backoff. Dependencies crossing the seam: cleanup hooks, SQLite cleanup state, lock holder finder, build-server shutdown delegate, `AgentOrchestratorKernel` process records. Characterization prerequisite: `GoalWorktreeTests` cleanup/orphan/ephemeral fragments must cover access denied, transient lock release, backoff persistence, protected/unprotected recorded worker process reaping, and owned ephemeral cleanup. Disjointness: serialize with `CliCommandHandlers.Goals` workspace-remove/acceptance cleanup extraction; disjoint from WorkerDispatch model-selection and Dashboard slices. Backlog-ready acceptance: static `GoalWorktrees` surface preserved while cleanup internals move behind the service.

7. Extract `GoalWorktrees` branch/rebase/merge/worktree metadata operations into `GoalWorktreeGitService`. Responsibility: create/resolve worktrees, fast-forward stale branches, rebase onto main, inspect metadata access, resolve heads, and compute manual merge diagnostics. Dependencies crossing the seam: `GitCli`, branch naming, linked worktree paths, goal ids, and repository root validation. Characterization prerequisite: `GoalWorktreeTests` creation/resolution and rebase/merge fragments must cover stale fast-forward, divergent preservation, rebase conflict, linked index lock path, and manual merge suggestion. Disjointness: serialize with cleanup extraction at first because both split `GoalWorktrees.cs`; later Git and cleanup slices are separate. Backlog-ready acceptance: public static API unchanged and all branch-state messages preserved.

8. Extract `CliCommandHandlers.Goals` acceptance/workspace commands into `CliGoalWorkspaceCommands`. Responsibility: `workspace`, `acceptance`, `goal mark-landed`, cleanup summary, stable slot selection, auto-verification from git evidence, and deferred cleanup recording. Dependencies crossing the seam: `CliExecutionContext`, `GoalWorktrees`, `GoalAcceptanceVerifier`, stable slot lease, console views, dogfood log recording. Characterization prerequisite: split `CliCommandTests` acceptance/workspace fragment plus split `GoalWorktreeTests` acceptance/landing fragment must exist and cover skip-verify, auto-verify, rebase-before-verification, cleanup failure, and stable slot receipt behavior. Disjointness: serialize with `GoalWorktrees` cleanup/Git extractions; disjoint from backlog intake and model-function CLI slices. Backlog-ready acceptance: `TryExecuteGoalCommand` delegates commands without changing CLI output.

9. Extract `CliCommandHandlers.Goals` backlog intake/planning commands into `CliGoalBacklogCommands`. Responsibility: backlog intake reservation/reuse, goal creation from backlog items, objective/plan/ideate command routing, and backlog intake output records. Dependencies crossing the seam: `BacklogStore`, `BacklogIntakePlanner`, `GoalObjectivePlanner`, `GoalDagDecompositionPlanner`, console views, agent override helpers. Characterization prerequisite: split `CliCommandTests` backlog/intake fragment covering ambiguous filters, reuse, reservation persistence, source backlog title labels, and generated objective text. Disjointness: can run concurrently with acceptance/workspace extraction only after shared flag parsing helpers are extracted or left in the root partial; disjoint from `GoalWorktrees` product slices. Backlog-ready acceptance: no CLI text drift and no changes to backlog store schema.

10. Extract `CliCommandHandlers.Goals` conduct/lifecycle policy handling into `CliConductLifecycleCommands`. Responsibility: `conduct`, lifecycle simple/five-role runs, drain, policy resolution, confirmation gates, goal readiness start checks, and bounded diagnostics/next full detail printing. Dependencies crossing the seam: `ConductorBatchLoop`, `ConductorDriver`, autonomy policy types, `RunGoalService`, console views, and model/agent overrides. Characterization prerequisite: split `CliCommandTests` conduct/lifecycle fragment plus `ConductorBatchLoopTests` scheduling fragment covering safe-auto stops, confirmation requirements, large prompt confirmation, drain summaries, and next/readiness output. Disjointness: serialize with `ConductorBatchLoop` and `ConductorDriver` product extractions; disjoint from Dashboard and WorkerProfileDispatcher slices. Backlog-ready acceptance: command aliases and policy messages unchanged.

11. Extract `ConductorBatchLoop` parallel acceptance/set-aside flow into `ConductorParallelAcceptanceCoordinator`. Responsibility: select parallel acceptance batch, run stable-slot acceptance, complete results, set aside blocked goals, readmit resolved goals, and mark dependencies completed. Dependencies crossing the seam: `ConductorDriver`, `AgentOrchestratorKernel`, goal facts cache, stable slots, acceptance results, and persistence callbacks. Characterization prerequisite: split `ConductorBatchLoopTests` parallel-acceptance and set-aside/readmit fragments covering overlapping gate-ready acceptance, dependency completion, readmission, and failure persistence. Disjointness: serialize with `ConductorDriver` landing extraction and CLI conduct extraction; disjoint from WorkerDispatch completion extraction. Backlog-ready acceptance: batch loop high-level `Run` stays readable and existing conductor tests pass.

12. Extract `ConductorDriver` landing/record/cleanup phases into `ConductorLandingCoordinator`. Responsibility: execute landing acceptance, complete landing after acceptance, record dogfood/model-fit evidence, cleanup workspace, and convert acceptance failures into concrete retry evidence. Dependencies crossing the seam: `GoalWorktrees`, `GoalAcceptanceVerifier`, `GoalLifecycleEventWriter`, dogfood log writer, semantic acceptance hook, policy. Characterization prerequisite: split `ConductorDriverTests` landing fragment and `GoalWorktreeTests` acceptance/landing fragment covering acceptance failure, semantic acceptance receipts, cleanup deferral, retry evidence, and record phase behavior. Disjointness: serialize with `GoalWorktrees` cleanup extraction and `ConductorBatchLoop` parallel acceptance extraction; disjoint from Dashboard and WorkerProfile preflight slices. Backlog-ready acceptance: no state-machine transition drift and no changed operator-facing landing messages.

13. Extract `WorkerProfileDispatcher` subscription preflight into `WorkerSubscriptionPreflightPlanner`. Responsibility: low-integrity auth findings, git metadata access, skill availability, build environment, worktree cleanliness, retry window, repeated-limit review, and blocked diagnostic construction. Dependencies crossing the seam: `WorkerProfileCatalog`, `AgentDefinition`, `Goal`, `TaskSpec`, environment probes, `GoalWorktrees.InspectGitMetadataAccess`, and build environment manager. Characterization prerequisite: split `WorkerDispatchTests` preflight fragment covering stale retry windows, repeated limits, read-only role guardrails, missing credentials, dirty worktrees, and missing worktrees for file roles. Disjointness: can run concurrently with `BackgroundDispatchRunner` process recovery after test split; serialize with WorkerProfileDispatcher model-selection extraction because both initially edit the same file. Backlog-ready acceptance: preflight messages and blocked error codes remain stable.

14. Extract `WorkerProfileDispatcher` model/profile resolution into `WorkerSubscriptionModelResolver`. Responsibility: resolve assigned agent, subscription profile name, selected model/reasoning, template variables, role guardrails, light-role worker-result requirements, and placeholder validation. Dependencies crossing the seam: `AgentCatalog`, `ModelProfile`, `WorkerProfile`, selected target context, current task role, and provider aliases. Characterization prerequisite: split `WorkerDispatchTests` model-selection fragment covering default OpenAI/Claude aliases, stale catalog repair assumptions, custom profile overrides, light-role validation, reasoning placeholder requirements, and role-to-sandbox/permission expansion. Disjointness: serialize with preflight extraction; disjoint from GoalWorktrees and Dashboard slices. Backlog-ready acceptance: no changes to generated dispatch command text except class ownership.

15. Extract `DispatchFailureClassifier` provider-specific rule sets into `ProviderFailureRuleSet` types. Responsibility: keep generic dispatch outcome assembly in the classifier while moving provider sentinels and rate-limit/blocked/auth patterns into small rule-set classes. Dependencies crossing the seam: provider name, exit code, stdout/stderr, preflight evidence, output capability, and `DispatchOutcome` factories. Characterization prerequisite: Core tests must have a `DispatchFailureClassifierProviderRulesTests` fragment covering Codex, Claude, Qwen/Ollama, generic process failures, retry windows, and light-role output capability. Disjointness: serialize with `BackgroundDispatchRunner` completion-classification extraction because runner consumes classifier outcomes; disjoint from CLI and Dashboard slices. Backlog-ready acceptance: Core namespace remains flat, public classifier API unchanged, all existing outcome text stable.

## Sequenced DAG

Use this DAG as conductor fan-out guidance:

1. `W1A WorkerDispatchTests split`, `W1B DashboardRenderingTests split`, and `W1C ConductorBatchLoopTests split` can run concurrently. They touch disjoint test files and partitions.
2. `W1D CliCommandTests split` must serialize with any `CliCommandHandlers.Goals.cs` extraction and should not overlap `W1E GoalWorktreeTests split` if both move shared acceptance/workspace helper conventions.
3. `W1E GoalWorktreeTests split` must land before `GoalWorktrees` cleanup/Git extractions and before CLI acceptance/workspace extraction.
4. `W1F GoalAcceptanceVerifierTests split` must land before `GoalAcceptanceVerifier` policy/check-runner extractions; it should serialize with acceptance verifier product work.
5. After W1A, run `WorkerSubscriptionPreflightPlanner`, `WorkerSubscriptionModelResolver`, and one `BackgroundDispatchRunner` slice at a time. Preflight/model resolver serialize with each other; `BackgroundDispatchRunner` slices serialize with each other.
6. After W1E, run `GoalWorktreeCleanupService` then `GoalWorktreeGitService`; serialize these with `CliGoalWorkspaceCommands` and `ConductorLandingCoordinator`.
7. After W1D, run `CliGoalBacklogCommands` concurrently with WorkerProfile or Dashboard work. Run `CliGoalWorkspaceCommands` only after GoalWorktrees cleanup/Git prerequisites. Run `CliConductLifecycleCommands` only after conductor test fragments exist.
8. After W1C, run `ConductorParallelAcceptanceCoordinator` and `ConductorLandingCoordinator` serially; both may run concurrently with WorkerProfileDispatcher slices if no shared tests are edited.
9. After W1F, run `AcceptancePolicyShardPlanner` then `AcceptanceCheckRunner` serially because both edit `GoalAcceptanceVerifier.cs`.
10. Run `ProviderFailureRuleSet` extraction after the first `BackgroundDispatchRunner` completion classifier slice, or serialize it with that slice if both need classifier outcome expectations.

## Disjointness Matrix

| Slice group | Primary files | Can run concurrently with | Must serialize with |
| --- | --- | --- | --- |
| WorkerDispatch test split | `WorkerDispatchTests.cs` | Dashboard split, Conductor split | WorkerProfileDispatcher and BackgroundDispatchRunner product slices |
| CLI test split | `CliCommandTests.cs` | Dashboard split, WorkerProfile product work after W1A | `CliCommandHandlers.Goals.cs` slices; GoalWorktree acceptance/workspace helper moves |
| GoalWorktree test split | `GoalWorktreeTests.cs` | Dashboard split, WorkerDispatch preflight after W1A | `GoalWorktrees.cs`; CLI workspace/acceptance; Conductor landing |
| Dashboard test split | `DashboardRenderingTests.cs` | WorkerDispatch, GoalWorktree, Conductor, Core classifier work | Dashboard API/rendering product changes |
| Conductor test split | `ConductorBatchLoopTests.cs`, later `ConductorDriverTests.cs` | WorkerProfileDispatcher, Dashboard, Core classifier work | `ConductorBatchLoop.cs`, `ConductorDriver.cs`, CLI conduct lifecycle |
| Acceptance verifier test split | `GoalAcceptanceVerifierTests.cs` | Dashboard, WorkerProfileDispatcher | `GoalAcceptanceVerifier.cs`; RepositoryTestImpactPlanner changes |
| BackgroundDispatchRunner product slices | `BackgroundDispatchRunner.cs` | Dashboard, GoalWorktrees Git after tests split if no shared tests | Other BackgroundDispatchRunner slices; WorkerDispatchTests split; DispatchFailureClassifier extraction |
| GoalAcceptanceVerifier product slices | `GoalAcceptanceVerifier.cs` | WorkerProfileDispatcher, Dashboard | Each other; GoalAcceptanceVerifierTests split; acceptance manifest/test-impact planner changes |
| GoalWorktrees product slices | `GoalWorktrees.cs` | WorkerProfile model/preflight, Dashboard | Each other; CLI workspace/acceptance; Conductor landing |
| CLI goal-command slices | `CliCommandHandlers.Goals.cs` | WorkerProfile preflight/model resolver; Dashboard | CLI test split; GoalWorktrees cleanup/Git; Conductor lifecycle |
| Conductor product slices | `ConductorBatchLoop.cs`, `ConductorDriver.cs` | WorkerProfileDispatcher, Dashboard | Conductor tests split; CLI conduct lifecycle; GoalWorktrees cleanup for landing |
| Core classifier slice | `DispatchFailureClassifier.cs` | Dashboard, GoalWorktrees cleanup | BackgroundDispatchRunner completion classifier |

## Backlog Intake Order

Recommended backlog order:

1. W1 test splits: WorkerDispatch, CliCommand, GoalWorktree, DashboardRendering, ConductorBatchLoop, GoalAcceptanceVerifier.
2. Worker dispatch product extractions: completion classifier, process recovery, commit-on-behalf.
3. Acceptance verifier extractions: policy shard planner, check runner.
4. Goal worktree extractions: cleanup service, Git service.
5. CLI goal command extractions: workspace/acceptance, backlog/intake, conduct/lifecycle.
6. Conductor extractions: parallel acceptance coordinator, landing coordinator.
7. WorkerProfileDispatcher extractions: preflight planner, model resolver.
8. Core classifier extraction: provider rule sets.

Each item should enter the backlog as a behavior-preserving goal with explicit characterization tests first, a narrow changed-file set, and a disjointness note copied from the matrix above.

Wave 2 item 2 places the process-recovery boundary at `DispatchProcessRefreshVerdict`: `DispatchProcessRecoveryService` answers what happened to the worker by reading heartbeat and exit artifacts, observing or reaping owned processes, evaluating recovery policy, and producing refresh diagnostics. The boundary is derived from the call graph: helpers that consume only process records, artifact data, the clock, and process/file seams moved; `BackgroundDispatchRunner` still decides what the verdict does to the task and kernel. Worker-result interpretation, `BuildCompletedProcessOutcome`, worktree/Git evidence, verification construction, and commit-on-behalf deliberately remain in the runner for Wave 2 item 3.

## Guarded source size ratchet

`GoalAcceptanceVerifierSizeRatchetTests` runs as an ordinary acceptance-gate test and applies the literal ceilings in `SourceSizeRatchet.SeededCeilings`. The table was seeded at commit `6e7a90d7b3fe192ae4f1e430cb7452710f88b73c` on 2026-08-22; every value below is that file's complete line count from `File.ReadLines(path).Count()` at that commit, except where a later row-raise is recorded below the table.

| Guarded file | Seeded ceiling |
| --- | ---: |
| `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs` | 8779 |
| `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs` | 6156 |
| `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs` | 4974 |
| `src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs` | 3533 |
| `src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs` | 3220 |
| `src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs` | 4821 |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs` | 1566 |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs` | 9282 |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.PersistentRunnerCommands.cs` | 7038 |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsWorkerResultClassification.cs` | 4834 |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsParallelAcceptance.cs` | 4473 |

The production rows are the six ranked god classes whose growth the inventory tracks. `ConductorDriverTests.cs` remains an unsplit test god class guarded as the test-side counterpart to its already-guarded production file. The `GoalAcceptanceVerifierTests.cs` row remains at its measured post-split size so regrowth is visible; none of its new fragments approaches the roughly 4,300-line threshold for adding a split-product row. The other three test rows are products of earlier splits that have regrown past that threshold; a one-time split without a bound only resets the clock. This is intentionally not a repository-wide size rule. For example, `DotnetBuildEnvironmentManagerTests.cs` (5199 lines) is large but is neither a ranked god class nor a split product; `CliCommandTests.GoalLifecycleCommands.cs` (4211) and `CliCommandTests.SubscriptionDispatchCommands.cs` (3471) remain within their intended post-split size; `DashboardRenderingTests.cs` (3795) has already moved to the Dashboard test project; and `DispatchFailureClassifier.cs` (2393) remains below the seeded production band.

When extraction shrinks a guarded file, lower that row in the same change. If growth is unavoidable, raise only that row deliberately with an inline justification naming the goal. A rename or deletion must update its row in the same change. Never derive a ceiling automatically from the current file, make a row advisory, add an opt-out, or delete the guard.

### Recorded row raises

Goal `46ff9f83` raised the `ConductorBatchLoopTestsParallelAcceptance.cs` row from 4355 to 4473 for the unequal logical-width/build-permit control and the typed maximum diagnostic. The existing parallel-acceptance test class owns both conductor admission and physical permit characterization, so extracting these controls would split the contract they compare.

Goal `b4b80aca` raised the `GoalAcceptanceVerifier.cs` row from 8763 to 8779 for the phase-transition calls that connect gate accounting to verifier-owned control-flow and resource-custody boundaries. The accountant, records, formatting, and lifecycle behavior remain extracted in `AcceptanceGatePhaseAccounting.cs`; moving the remaining transitions out would split the orchestration invariant they measure.

Goal `682f25a1` re-derived the `GoalAcceptanceVerifierTests.cs` row from 10426 to 10451 after integrating goal `b4b80aca`, whose gate-phase accounting coverage had already added 25 net lines before the multi-file ratchet landed. This is a catch-up raise for pre-existing growth, not growth introduced by the ratchet change.

Earlier raises of that same file, recorded before the table existed: goal `75b85ca1` from 8713 to 8760 for lane selection and structural-coverage threading owned by the gate plan, and goal `85f0b81d` to 8763 after extracting 227 behaviour lines to `AcceptanceLaneDurationStore.cs`, leaving only call sites.
