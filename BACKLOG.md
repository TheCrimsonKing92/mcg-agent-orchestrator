# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.

## Move subscription-plan projection out of Dashboard.Api

Status: open | Size: large | Suggested route: not local-model work; needs solution-wide refactoring

Why: cost guards moved to `Mcg.AgentOrchestrator.App.CostControl`, but `SubscriptionPromptCostGuard` still calls `DashboardResponseMapper.BuildSubscriptionPlan` and consumes `SubscriptionPlanItemDto`/`SubscriptionPlanModelSummaryDto`, so cost policy still depends on dashboard projection types.

Where: `src/Mcg.AgentOrchestrator.App/CostControl/SubscriptionPromptCostGuard.cs`, `BuildSubscriptionPlan` in `src/Mcg.AgentOrchestrator.App/Dashboard/Api/DashboardResponseMapper.Configuration.cs` (also pulls in OrchestratorHealthInspector).

Done when: subscription-plan building lives in a non-presentation namespace and CostControl no longer references Dashboard.Api types.

Verify: full `dotnet test`; no `Dashboard.Api` usings remain in `CostControl/`.

## Validate the 768-token routine paid output cap

Status: open, blocked on data | Size: small per run | Suggested route: Simple local-model report tasks; operator embeds the data

Why: the cap (`RoutinePaidProviderFallbackMaxOutputTokens` in `src/Mcg.AgentOrchestrator.Core/Application/AgentTaskRunner.cs`) has never been exercised - the 2026-06-10 inventory found zero API execution records. First data point: a Simple report task on qwen3:8b produced 851 output tokens, which would have been truncated under 768. n=1, and qwen is verbose; collect more before tuning.

Done when: roughly ten local API runs across task shapes have recorded token usage and stop reasons; then decide keep/raise with the evidence and record the decision in DOGFOOD_LOG.md.

Verify: execution records in goal evidence show usage and stop reasons; `OutputTokenLimit.IsHit` flags none falsely.

## Dispatch briefs omit prior-task evidence, so SDLC roles run disconnected

Status: open | Size: medium | Suggested route: scoped Developer task; needs a context-budget decision

Why: in the five-role pipeline, each role's dispatch brief contains only the goal objective plus that task's own history. `BuildTaskBrief` filters the timeline to `evt.TaskId == taskId || evt.TaskId is null` and the Last Model Output / Last Verification sections read from the same task only, so a completed Planner task's plan (recorded as verification stdout) never reaches the Researcher/Developer/Tester/Reviewer briefs. Observed live 2026-06-11 (goal f2e3d68c): the Planner produced a concrete file-level plan; the Researcher's brief carried none of it and the role re-derived from scratch. The brief's "Goal work summary: /api/goals/<prefix>/work-summary" pointer does not help dispatched workers - prior dogfood runs show they cannot reach a live dashboard.

Where: `BuildTaskBrief` in `src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.TaskBriefs.cs` (timeline filter at the SelectPromptTimelineEvents call; same-task LastExecution/LastVerification sections); `PromptContextFormatter` trim budgets in the same namespace.

Done when: a dispatch brief for task N includes a bounded "Prior task evidence" section with, at minimum, the latest verification stdout of each completed earlier task in the goal (trimmed via the existing PromptContextFormatter budgets); a test covers a two-task goal where task 2's brief contains task 1's verification evidence.

Verify: `dotnet test`; inspect a generated prompt file for a second task in a multi-task goal.

## Default codex subscription model is rejected by ChatGPT accounts

Status: open | Size: small | Suggested route: scoped Developer task with file pointers

Why: the shipped OpenAI subscription default `gpt-5.3-codex` fails every codex-cli dispatch on a ChatGPT account: codex exits 1 with 400 invalid_request_error "The 'gpt-5.3-codex' model is not supported when using Codex with a ChatGPT account" (live failure 2026-06-11, goal f2e3d68c Planner task). Direct smoke confirmed `gpt-5.5` (the codex CLI default) works; `gpt-5.5-codex` is rejected the same way. Same defect class as the invalid `claude-sonnet` alias fixed 2026-06-10: a built-in subscription default that can never dispatch. Operator workaround applied: CLI workspace agents.json aliases hand-edited to gpt-5.5.

Where: `AgentCatalogStore.cs` Codex() default (src/Mcg.AgentOrchestrator.Infrastructure/Persistence), `DefaultSubscriptionModelAlias` in `src/Mcg.AgentOrchestrator.App/Dashboard/Api/DashboardRequestParser.Configuration.cs` (~line 167), dashboard model options in `DashboardAssets.cs` (~lines 74-78) and `DashboardRenderer.OperatorShell.cs` (~lines 572/607/656), README mentions. Consider also a codex pre-flight model check mirroring the claude patch-capability gate.

Done when: built-in codex defaults use a model that dispatches on a ChatGPT account (gpt-5.5 as of 2026-06-11); stale saved catalogs/profiles are repaired the way stale claude templates are; a test pins the new default.

Verify: `dotnet test`; a live `subscription-dispatch`/`start-dispatch` of a codex agent exits 0.


