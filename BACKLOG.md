# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.

## Reconcile the paid-prompt cost guard with brief growth and estimator wording

Status: open | Size: small-medium | Suggested route: scoped Developer task; needs a threshold decision

Why: two interacting frictions make `--confirm-large-paid-subscription-start` near-universal, eroding its signal. (1) The complexity estimator classifies briefs by wording - every detailed "Implement the BACKLOG.md item..." simple-goal brief lands Complex, tripping the guard at ~4-7k chars (threshold reads 12000 chars or 3 tasks, but Complex-model selection alone requires the flag). (2) Since prior-task evidence landed (72be355), pipeline briefs grow as roles complete (4.2k by task 3, 5.0k by task 4 in goal e9d05a26), pushing even Simple generic role briefs past the 4000-char per-prompt threshold. Nine of ten loop dispatches on 2026-06-11 required the flag.

Where: `SubscriptionPromptCostGuard` in `src/Mcg.AgentOrchestrator.App/CostControl/`; `TaskComplexityEstimator` in Core; the per-prompt threshold constant; `PromptContextFormatter.BuildPriorTaskEvidenceLines` budget.

Done when: a deliberate decision is recorded (raise the per-prompt threshold, exempt or separately budget accumulated prior-task evidence, and/or make the estimator weigh more than wording) and implemented so that a routine pipeline role task with inherited evidence does not require the large-paid flag; guard tests pin the new behavior.

Verify: `dotnet test`; a pipeline task-3 dispatch with Simple complexity starts without `--confirm-large-paid-subscription-start`.

## Bound refresh-dispatch output for long subscription logs

Status: open | Size: small | Suggested route: scoped Developer task

Why: goal 2c8af0da (2026-06-11) completed successfully, but `refresh-dispatch 1` printed a very large stderr payload because codex echoed the full prompt and a long diff before `tokens used`; the operator output included a `[truncated 350284 chars]` block. Refresh output should preserve decision-changing evidence without flooding the terminal/context.

Where: task rendering for `refresh-dispatch` in the CLI/dashboard reporting path that prints stdout/stderr verification snippets; likely the same formatter that displays `Last verification` and `Verification history`.

Done when: refresh/status views cap stdout/stderr snippets to a small bounded size with an explicit truncation marker and paths to the full log files; tests cover long stdout/stderr display.

Verify: `dotnet test`; a synthetic long-log verification renders bounded output.

## Refresh-dispatch fails when live stdout log is locked

Status: open | Size: small | Suggested route: scoped Developer task

Why: goals 2c8af0da and d47ec298 (2026-06-11) showed `refresh-dispatch 1` can throw `The process cannot access the file ...out.log because it is being used by another process` while the worker/wrapper is still alive. In d47ec298 the branch had already committed, stdout was a locked zero-byte file, and the operator had to cancel the exact process plus `verify-manual` from independent evidence.

Where: `BackgroundDispatchRunner.RefreshLatestProcess` and any helper that reads stdout/stderr before checking process/exit-file state.

Done when: refresh tolerates locked stdout/stderr files by reporting process state and log paths, using best-effort snippets only when readable, and still allowing timeout/exit-file handling to proceed. Tests cover locked or unreadable log files.

Verify: `dotnet test`; a synthetic locked-log dispatch refresh does not throw.

## Avoid repeated Windows firewall prompts for app launches

Status: open | Size: small-medium | Suggested route: scoped Researcher/Developer task

Why: during dogfood on 2026-06-11, Miles repeatedly had to authorize `Mcg.AgentOrchestrator.App` in Windows Firewall. That blocks unattended orchestrator loops when tests or dashboard/prototype launches bind HTTP ports. The loop should either avoid triggering new firewall identities or document/provide a one-time setup path.

Where: dashboard/prototype host launch paths (`DashboardHost`, `prototype-ui`, tests that start the app), publish/build output identity, and any scripts that launch `Mcg.AgentOrchestrator.App`.

Done when: the cause is identified and either mitigated (stable executable/path/URL binding or loop-safe test host behavior) or documented with a one-time operator setup command/check. Tests or a focused smoke validate the chosen behavior where practical.

Verify: run the relevant dashboard/test launch path without repeated firewall prompts, or record the OS-level setup evidence in DOGFOOD_LOG.md.

## Move subscription-plan projection out of Dashboard.Api

Status: open | Size: large | Suggested route: not local-model work; needs solution-wide refactoring

Why: cost guards moved to `Mcg.AgentOrchestrator.App.CostControl`, but `SubscriptionPromptCostGuard` still calls `DashboardResponseMapper.BuildSubscriptionPlan` and consumes `SubscriptionPlanItemDto`/`SubscriptionPlanModelSummaryDto`, so cost policy still depends on dashboard projection types.

Where: `src/Mcg.AgentOrchestrator.App/CostControl/SubscriptionPromptCostGuard.cs`, `BuildSubscriptionPlan` in `src/Mcg.AgentOrchestrator.App/Dashboard/Api/DashboardResponseMapper.Configuration.cs` (also pulls in OrchestratorHealthInspector).

Done when: subscription-plan building lives in a non-presentation namespace and CostControl no longer references Dashboard.Api types.

Verify: full `dotnet test`; no `Dashboard.Api` usings remain in `CostControl/`.

## Validate the 768-token routine paid output cap

Status: open, blocked on data | Size: small per run | Suggested route: Simple local-model report tasks; operator embeds the data

Why: the cap (`RoutinePaidProviderFallbackMaxOutputTokens` in `src/Mcg.AgentOrchestrator.Core/Application/AgentTaskRunner.cs`) has never been exercised - the 2026-06-10 inventory found zero API execution records. Data points from loop Reviewer/report tasks: 851, 988, 1010, 1139 output tokens; qwen is verbose, so collect more before tuning.

Done when: roughly ten local API runs across task shapes have recorded token usage and stop reasons; then decide keep/raise with the evidence and record the decision in DOGFOOD_LOG.md.

Verify: execution records in goal evidence show usage and stop reasons; `OutputTokenLimit.IsHit` flags none falsely.
