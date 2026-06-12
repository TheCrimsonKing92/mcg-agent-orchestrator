# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.

## Reject unresolved worker/profile dispatch template variables before task state mutation

Status: open | Size: small | Suggested route: simple-goal

Why: during goal `feadc825`, `profile-dispatch 1 codex-cli` against a task assigned to an Anthropic agent recorded a Running dispatch with literal `{subscriptionModelName}` and `{subscriptionReasoningEffort}` placeholders. No process had started, `cancel-dispatch` could not cancel it, and the operator had to use `progress failed` plus `retry` to recover.

Where: `WorkerProfileDispatcher`, CLI `profile-dispatch`/`worker-dispatch`, task status mutation and dispatch recording.

Done when: dispatch preparation validates that no `{...}` template variables remain in the rendered command before it records dispatch state or marks a task Running. Failures should leave the task in its prior status and print actionable evidence. If cross-profile rerouting is intended, provide an explicit supported way to supply subscription model/reasoning/worktree values.

Verify: focused tests cover unresolved placeholders in `profile-dispatch` and `worker-dispatch`, proving task status and last dispatch are unchanged on failure. Run full `dotnet test`.

## Stop full test runs from mutating checked-in source files

Status: open | Size: small-medium | Suggested route: simple-goal

Why: after goal `8db3d426` committed and the worktree was clean, the independent `dotnet test .orchestrator-worktrees\8db3d426 --verbosity minimal` run left tracked source/test files modified in that disposable worktree, including unrelated `rollback-state` and state-store changes. That makes the operator gate ambiguous and blocks normal `workspace remove`.

Where: infrastructure tests that execute CLI/system/dashboard flows, especially tests around state rollback/store behavior and any helper that uses the repository root instead of an isolated temp workspace.

Done when: a full `dotnet test <worktree> --verbosity minimal` leaves `git status --short` clean in the worktree. Tests that need mutable app files or state must use temp directories outside the source tree.

Verify: add a regression test or harness assertion where practical, then run full `dotnet test` and immediately confirm `git status --short` is clean.

## Codex dispatch wrappers can still remain alive after final output without an exit file

Status: open | Size: small-medium | Suggested route: simple-goal

Why: goals `feadc825` and `8db3d426` both used Codex workers that printed final output, committed, and reported `Model fit:`, but the PowerShell wrapper process stayed live and no exit file was written. The operator still had to use exact `cancel-dispatch` plus `verify-manual` after confirming branch/test evidence.

Where: `BackgroundDispatchRunner` wrapper construction, exit-file supervision, idle/final-output detection, and `refresh-dispatch` handling for Codex subprocess trees on Windows.

Done when: after Codex prints final output and the child has no meaningful activity, `refresh-dispatch` can close the task from reliable evidence or fail with bounded captured logs without requiring manual cancellation. Normal successful Codex dispatches should produce an exit file promptly.

Verify: focused tests for existing-exit-file completion and idle-final-output handling, plus a live no-edit Codex smoke in an isolated worktree.

## Use one source of truth for paid/local output cap policy

Status: open | Size: small | Suggested route: simple-goal

Why: a recent output-cap change failed tests because `AgentTaskRunner` had private cap constants duplicating `AgentCatalog.RoutineApiMaxOutputTokens` and `AgentCatalog.ComplexApiMaxOutputTokens`. The worker changed one copy and missed the other, producing a false-green worker report.

Where: `AgentTaskRunner` max-output-token resolution, `AgentCatalog`/agent defaults, related tests that pin routine/complex paid output caps.

Done when: output-cap limits live in one Core/Infrastructure policy source consumed by the runner, catalog/defaults, and tests. There are no duplicate policy literals for the routine/complex caps except tests intentionally asserting that single source.

Verify: targeted search for duplicated cap literals/constants, focused cap tests, and full `dotnet test`.

## Fold independent operator verification into acceptance

Status: open | Size: medium | Suggested route: simple-goal or five-role goal

Why: the manual gate before `acceptance` is repeated and easy to miss: inspect goal diff, shut down build servers if needed, run tests in the worktree, and only then merge. It caught a false worker claim in live dogfood. The VBCSCompiler wrapper fix and bounded output rendering make this practical to automate.

Where: `acceptance` CLI/API flow, goal worktree services, verification command execution and concise result reporting.

Done when: acceptance runs the independent verification suite in the goal worktree before fast-forward/merge, refuses to merge on verification failure, and records concise evidence. Provide an explicit `--skip-verify` escape for rare operator-controlled cases.

Verify: tests cover acceptance refusing a failed verification, accepting a passing verification, and honoring `--skip-verify`. Run full `dotnet test`, then validate in a live goal.

## Make workspace cleanup report lock holders and leave a resumable cleanup command

Status: open | Size: small-medium | Suggested route: simple-goal

Why: goal `feadc825` fast-forwarded successfully, but `workspace remove` unregistered the worktree and then repeatedly failed to delete `.orchestrator-worktrees/feadc825` because another process held the directory. `dotnet build-server shutdown`, stopping the exact post-test `dotnet` PID, and long-path `Remove-Item` still left a locked orphan directory. The operator could delete the merged branch, but could not identify the remaining holder without broad process risk.

Where: `GoalWorktrees.Remove`, workspace CLI output, cleanup retry/backoff and diagnostics on Windows.

Done when: partial cleanup reports that the worktree was unregistered, whether the goal branch remains, the exact leftover path, and the likely lock-holder process names/PIDs when discoverable. A follow-up `workspace remove` or dedicated cleanup command should resume from that state and avoid throwing a generic directory-in-use error.

Verify: tests simulate already-unregistered leftover worktree directories and locked paths where possible; manual Windows smoke validates actionable output when deletion is blocked. Run full `dotnet test`.

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
