# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.

## Require commit or explicit no-change evidence for Developer/Tester dispatch completion

Status: open | Size: small-medium | Suggested route: simple-goal

Why: live dogfood goal `03192a9b` showed a haiku Developer correctly edited files but did not commit. The task still recorded `TaskCompleted` because exit code 0 plus non-empty output satisfied the current subscription dispatch guard. That is a false-completion class for file-touching roles, especially now that haiku is used for smaller Developer tasks.

Where: subscription/background dispatch completion handling in App/Infrastructure, goal worktree detection, task verification records, and existing dispatch tests around `BackgroundDispatchRunner`/worker dispatch.

Done when: for Developer and Tester dispatches that run in a goal worktree, completion requires either a new commit on the goal branch after dispatch start or an explicit no-change rationale in worker output with a clean worktree. If neither exists, record the task as failed with concise evidence that includes branch/commit/worktree status. Planner/Researcher/Reviewer behavior is unchanged.

Verify: add focused tests for a file-touching role that exits 0 with output but no commit, a dirty-worktree no-commit case, a committed-change pass, and an explicit no-change pass. Run full `dotnet test`.

## Add usage-limit review acknowledgement for repeated recoverable subscription failures

Status: open | Size: small-medium | Suggested route: simple-goal

Why: after two recoverable subscription usage-limit failures, `subscription-dispatch` requires operator review but exposes no acknowledgement mechanism, leaving the task stuck. The current workaround is `verify-manual <n> failed "<review note>"` followed by `retry`, which is not discoverable and overloads verification semantics.

Where: subscription dispatch planning/start commands, retry-limit review state, task verification/history records, and dashboard parity controls if present.

Done when: CLI accepts `--confirm-limit-review "<note>"` on the appropriate dispatch command, records the review note, clears or supersedes the limit-review block, and allows retry/dispatch to proceed. Dashboard/API parity offers the same acknowledgement path. If `SubscriptionRetryAfter` is known and still in the future, `start-dispatch` reports the defer-until time and does not burn another attempt.

Verify: focused tests cover the CLI acknowledgement, dashboard/API acknowledgement if applicable, persisted review evidence, and future retry-after deferral. Run full `dotnet test`.

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
