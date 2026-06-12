# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.

## Isolate test-spawned orchestrators from the real repository root

Status: open | Size: small-medium | Suggested route: simple-goal

Why: `InfrastructureTestSupport.StartDashboardProcess` pins `MCG_ORCHESTRATOR_REPOSITORY_ROOT` to the real repository, so goals created by spawned-app e2e tests resolve worktrees and dispatch working directories under the real `.orchestrator-worktrees`. Before the `MCG_ORCHESTRATOR_DISABLE_DISPATCH_START` kill switch (commit e3e1fe4), auto-handoff e2e tests launched real codex CLI runs that edited live goal worktrees and burned subscription usage across many test runs (discovered 2026-06-12). The kill switch stops process starts, but test goals still point at real repo paths.

Where: `InfrastructureTestSupport.StartDashboardProcess`, repository-root resolution in the App, goal worktree path resolution for spawned test apps.

Done when: spawned-app tests resolve repository root and worktrees to a temp location (or a bare fixture repo), so no test-created goal can reference real worktrees even if process starts are re-enabled. The kill switch stays as defense in depth.

Verify: e2e dashboard tests pass with worktree paths under the test temp directory; full `dotnet test` spawns zero codex/claude processes (assert via process scan in the test or manual smoke).

## Require a goal worktree before subscription-dispatch records a working directory

Status: open | Size: small | Suggested route: simple-goal

Why: running `subscription-dispatch` before `workspace create` records a dispatch with the repository root as working directory; the operator must `progress failed` + `retry` to re-prepare. Observed live on goal 42808fff (2026-06-12).

Where: `WorkerProfileDispatcher.PrepareTask` / dispatch preparation working-directory resolution.

Done when: preparing a Developer/Tester subscription dispatch for a goal without a worktree either creates the worktree first or fails with a message naming `workspace create`; repo-root dispatch recording for file-touching roles is impossible.

Verify: focused tests for prepare-without-worktree behavior; full `dotnet test`.

## Add sequential pipeline auto-advance

Status: open | Size: medium-large | Suggested route: five-role goal after the completion and limit-review guards are fixed

Why: a five-role pipeline still requires many operator interventions: dispatch, start, wait, refresh, and repeat per role. Existing safety pieces now classify cost guards, usage limits, role sandboxes, wrapper hangs, and refresh failures well enough to support a guarded auto-run command.

Where: `advance-subscription` or a new `run-goal` style command, task dispatch/start/refresh orchestration, dashboard/API parity if applicable.

Done when: one command walks eligible tasks sequentially: dispatch, start with required confirmations, wait on the exit file/process, refresh, and continue. It stops with concise evidence on failure, human input, limit review, retry-after deferral, or a cost guard that lacks confirmation.

Verify: integration tests cover sequential success and stop conditions for failure, human input, limit review, retry-after, and cost guard. Run full `dotnet test`, then validate on a five-role pipeline.

## Move subscription-plan projection out of Dashboard.Api

Status: open | Size: large | Suggested route: not local-model work; needs solution-wide refactoring

Why: cost guards moved to `Mcg.AgentOrchestrator.App.CostControl`, but `SubscriptionPromptCostGuard` still calls `DashboardResponseMapper.BuildSubscriptionPlan` and consumes `SubscriptionPlanItemDto`/`SubscriptionPlanModelSummaryDto`, so cost policy still depends on dashboard projection types.

Where: `src/Mcg.AgentOrchestrator.App/CostControl/SubscriptionPromptCostGuard.cs`, `BuildSubscriptionPlan` in `src/Mcg.AgentOrchestrator.App/Dashboard/Api/DashboardResponseMapper.Configuration.cs` (also pulls in OrchestratorHealthInspector).

Done when: subscription-plan building lives in a non-presentation namespace and CostControl no longer references Dashboard.Api types.

Verify: full `dotnet test`; no `Dashboard.Api` usings remain in `CostControl/`.
