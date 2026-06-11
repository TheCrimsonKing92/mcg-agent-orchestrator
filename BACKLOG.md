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

## Dispatch commands cannot target a non-latest goal

Status: open | Size: small | Suggested route: direct edit plus brief test

Why: the one-shot CLI resolves `CurrentGoal` to the latest goal at process start, and dispatch-family commands (`subscription-dispatch`, `start-dispatch`, `refresh-dispatch`, `logs`, `cancel-dispatch`) take only a task number with `RequireGoal(context.CurrentGoal)`. Creating goal B while goal A's worker is still running makes A's dispatch unreachable: its completion can never be refreshed/recorded. Discovered 2026-06-10 while starting the self-improvement loop; it forces strictly sequential goals.

Where: `OrchestratorEntityResolver.RequireGoal` callers in `src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Workers.cs`; the resolver already supports goal-prefix resolution (`ResolveGoal`) used by `status`/`evidence`.

Done when: dispatch-family commands accept an optional goal prefix (e.g. `refresh-dispatch <goal-prefix> <task-number>` or a `--goal` flag) so two goals can run workers concurrently; a test covers targeting a non-latest goal.

