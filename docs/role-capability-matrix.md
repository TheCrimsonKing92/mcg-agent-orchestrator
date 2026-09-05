# Worker Role Capability Matrix

Use this matrix while writing a goal brief. It describes what a dispatched worker can produce in this
repository today; it does not grant new capabilities.

## Enforcement sources

The boundaries below come from these live controls:

- A generated `.orchestrator-context/<goal-id>/current-task.md:8-12` requires Developer and Tester workers to
  compile every changed .NET project through `Invoke-WorkerBuildCheck.ps1`, forbids raw `dotnet test`,
  `Invoke-IsolatedDotnet.ps1`, and other worker-side test execution, and routes tests to the acceptance gate
  (`src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerArtifactWriter.cs:251-260` and
  `src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerArtifactWriter.cs:346-356`).
- Dispatch role classification gives Developer and Tester writable settings and marks Planner, Researcher,
  and Reviewer read-only (`src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs:3160` and
  `src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs:3171`).
  The built-in Claude command builder turns that read-only sentinel into restricted `dontAsk` mode with an
  explicit tool availability/allow/deny policy that omits delegated agents and only auto-approves read tools
  (`src/Mcg.AgentOrchestrator.Infrastructure/Workers/ProviderCommandBuilder.cs:130-136`).
  Preflight separately requires a goal workspace and patch-capable profile for writable roles
  (`src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerSandboxCapabilityPlanner.cs:9-36`).
- Worker sandboxes do not grant ref-level Git writes: creating `.git/packed-refs.lock` is denied. No worker
  role can rebase, merge main into a goal branch, or otherwise integrate branch history. Conductor
  Developer-dispatch preflight owns that operation
  (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs`).
- Role prompts forbid Planner, Researcher, and Reviewer repository edits, make Tester verification-only,
  and reserve implementation for Developer
  (`src/Mcg.AgentOrchestrator.Core/Application/SdlcRolePromptRequirements.cs:22-90`). The outcome
  classifier encodes the same split as ReadOnly, VerificationOnly, and RequiresChangeEvidence
  (`src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs:48-68`).
- Output contracts require a durable Planner plan, a cited Researcher artifact, structured Tester and
  Reviewer findings, Reviewer criterion verdicts, and an explicit `tests` state that may be `deferred`
  (`src/Mcg.AgentOrchestrator.Core/AgentOutputDirectives.cs:12-42` and
  `src/Mcg.AgentOrchestrator.Core/AgentOutputDirectives.cs:56-89`).
- Every task brief says its decision context is embedded and forbids workers from reaching dashboard APIs
  or orchestrator state (`src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.TaskBriefs.cs:180-189`).
  Packaged prior-task evidence is selected from earlier tasks in the same goal
  (`src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerArtifactWriter.cs:329-332`); it is not an
  authority to inspect `state.db`, `.orchestrator/goal-events`, or another goal.

## Matrix

The common read boundary is the assigned goal worktree, the task brief, and the packaged context artifacts.
A worker may use ordinary read-only source inspection within that boundary. Evidence outside it exists for
the worker only when the operator or context-packaging step supplies it.

| Role | May read | May write | May execute | Can produce | Cannot produce |
| --- | --- | --- | --- | --- | --- |
| Researcher | Packaged goal evidence, current source, and allowed primary sources | No repository files; the research artifact is stdout captured by the orchestrator | Read-only discovery and inspection commands | Cited current-source findings, supplied prior-goal evidence, upstream capability facts, risks, unknowns, or `premise-invalid` | A repository change, test/gate receipt, unsupplied cross-goal evidence, or post-landing observation |
| Planner | Packaged evidence, Researcher artifact, and current source | No repository files; the plan is stdout captured by the orchestrator | Read-only discovery and inspection commands | A criterion-mapped plan with owners, seams, contracts, focused verification, risks, and stop conditions, or `premise-invalid` | Implementation, an executed test/gate result, unsupplied cross-goal evidence, or post-landing observation |
| Developer | Packaged upstream artifacts, current source, and the assigned worktree | Requested source, test, configuration, and documentation files in the worktree; not orchestrator state, `.git` internals, or Git refs | Source-inspection commands and the sanctioned worker build check; non-.NET checks allowed by the generated task package | A scoped diff, source traces, build results, named focused-evidence requests, and `tests: deferred` or `not-run` with a reason | A rebase or branch-history integration, raw .NET test/full-suite receipt, acceptance-gate wall-clock, gate coverage total, unsupplied cross-goal evidence, or post-landing observation |
| Tester | Candidate diff, packaged task evidence, and existing verification receipts | No source files. The sandbox is writable so sanctioned commands can emit build/verification artifacts, but the role contract is verification-only | Source inspection and the sanctioned worker build check; request Conductor-side focused tests when execution is restricted | A focused verification matrix, build result, source/manual reproduction evidence, named test classes, structured findings/evidence requests, and an honest `tests: deferred` or `inconclusive` result | A source fix, raw .NET test/full-suite receipt, acceptance-gate wall-clock or coverage total, unsupplied cross-goal evidence, or post-landing observation |
| Reviewer | Candidate diff plus packaged implementation and verification receipts | No repository files | Read-only diff/source inspection; request focused evidence through structured findings | Criterion verdicts, ranked findings, a pass/needs-work/fail verdict, residual risk, and focused-evidence requests | A source fix, a new raw .NET test/full-suite receipt, acceptance-gate measurements, unsupplied cross-goal evidence, or post-landing observation |

The Tester row is deliberately not “powerless.” For example, this is a complete passing worker round when
the test host is unavailable to the subscription worker:

```text
tests: deferred - Invoke-WorkerBuildCheck passed with 0 errors; acceptance should run
  Core.Tests::FooTests and Infrastructure.Tests::BarTests
blockers: none
```

That result does not claim the named tests passed. It proves the Tester completed the evidence available to
the role and routed the remaining execution correctly; deferral alone is not a worker failure.

## Branch integration ownership

Branch integration against `main` is conductor-owned whenever `main` advances during a goal. Before an
assigned Developer task is dispatched and before its brief is issued, the conductor runs the read-only
`git merge-tree --write-tree --name-only` check against the current main and goal-branch revisions.

| Merge-tree result | Owner and required action |
| --- | --- |
| Goal branch already contains main | Conductor proceeds with Developer dispatch. |
| Diverged, conflict-free | Conductor merges the checked main revision into the goal branch, records the integration, verifies that the worktree is clean and main is an ancestor, then issues the Developer brief. |
| Diverged, conflicting | Conductor does not resolve or dispatch. It escalates to the operator/conductor with the conflicting paths verbatim; no worker is instructed to rebase or perform branch integration. |
| Inspection or integration failure | Conductor blocks dispatch and escalates the exact failure; it does not silently continue with stale branch state. |

A brief author must reject or rewrite any worker instruction to rebase, merge main, or update branch refs.
The reachable worker contribution may resolve source semantics only after the operator/conductor has
completed the ref-level integration. “Copy main's content into a normal worker commit” is not integration
and must never be used as a substitute.

## Criterion routing rule

Before assigning any acceptance criterion, name the role that will satisfy it and check that role against
the matrix. If no worker can produce the evidence, say so in the brief and route the obligation explicitly:

- **Acceptance gate:** full-suite runs, gate wall-clock or shard overlap, coverage totals, test-host receipts,
  and other evidence produced by the stable acceptance slots. The gate owns test discovery and structural
  coverage (`src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs:337-393`) and
  records aggregate shard wall-clock
  (`src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs:1064-1190`).
- **Operator:** post-landing observation, live-system behavior, and evidence requiring operator authority.
- **Context-packaging:** classifier receipts, other goals' verification records, or any other cross-goal
  evidence a worker needs to reason about. Package the exact receipt; do not tell the worker to query stores.
  Keep the cross-goal comparison operator-owned when the worker does not need it for its reachable work.

Do not turn an off-worker obligation into a worker blocker after dispatch. The brief should state both the
worker's reachable contribution and the destination responsible for the remaining proof.

## Worked corrections

### Goal `5bd314c1`: extracted test module

Impossible Developer criterion:

> When an unrelated project changes, the extracted test module is not re-run.

Why it fails: acceptance deliberately executes every discovered test project for structural coverage. A
Developer cannot override that gate policy, and avoiding test execution would weaken the safety net.

Corrected criterion:

> For a change outside the extracted module, the build/impact plan omits that module from compile inputs;
> the Developer records the source-level selection trace and a successful worker build. Acceptance may
> still execute every discovered test project.

This asks the Developer for compile avoidance, not test avoidance, and leaves gate execution with acceptance.

### Goal `780f1790`: eighteen-shard wall-clock

Impossible Tester criterion:

> Record before/after wall-clock, overlap, and complete verdicts for the 18-shard acceptance gate.

Why it fails: a subscription Tester cannot launch the .NET test host or acceptance slots, so it cannot
produce the post-change gate receipt.

Corrected criterion:

> Context-packaging supplies the baseline 18-shard receipt. The Tester records the measurement definition,
> relevant shard classes/checks, source trace, and worker build result. The acceptance gate runs the
> post-change 18-shard plan and records wall-clock, overlap, and complete verdicts for comparison; the
> operator evaluates any required post-landing observation.

The corrected wording makes every evidence owner explicit before a paid worker round starts.
