# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.

## Reap worktree-referencing build daemons when a dispatch completes

Status: open | Size: small-medium | Suggested route: simple-goal

Why: worker-spawned VBCSCompiler/MSBuild processes outlive their dispatch (idle TTLs of 10-15 minutes) while holding memory-mapped handles on the goal worktree's `obj` assemblies, and `dotnet build-server shutdown` does not reliably reach servers another session started. The next build in that worktree (operator gate or acceptance verification) then fails CS2012 until the exact PID is stopped; observed on goals da957f56 (PID 42716) and 2919a85d (PID 56656) on 2026-06-12. The wrapper env pins prevent daemons from the wrapper's own shell, but a worker's inner tooling can still spawn them.

Where: `BackgroundDispatchRunner` completion path (`RecordCompletedProcess` for non-local dispatches), reusing the lock-holder discovery shipped in `GoalWorktrees.FindLockHolders` (commit 4665729).

Done when: when a non-local dispatch in a goal worktree completes (normally or via hung-wrapper reaping), the runner finds VBCSCompiler/MSBuild processes whose command line references the dispatch working directory (plus build servers with unreadable command lines, matching the FindLockHolders fallback) and stops them by exact PID, recording which PIDs were reaped in the completion evidence. Processes that do not match are never touched; local dispatches are unaffected; failures to kill degrade to a note rather than failing the dispatch.

Verify: focused tests with an injectable process enumerator/killer cover reap-on-completion, no-match leaves processes alone, and kill-failure degradation; run full `dotnet test`.

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
