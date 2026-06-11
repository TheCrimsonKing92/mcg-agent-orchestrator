# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Include the dispatch working directory in task briefs

Status: open | Size: small | Suggested route: direct edit plus brief test

Why: the local bridge is validated (2026-06-10, goal `c2d38209`): qwen-code + qwen3:8b with `reasoning: false` completed an agentic file write in a goal worktree in 19 s. But qwen-code's write tool requires absolute paths, qwen3:8b does not self-correct relative-path rejections, and the task brief never states the dispatch working directory - the validation only passed because the operator embedded the worktree path in the task description by hand.

Where: `BuildTaskBrief` (`src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.TaskBriefs.cs`) does not know the working directory; `WorkerProfileDispatcher.PrepareTask` does. Pass it through so the brief states "Working directory (use absolute paths): <path>".

Done when: dispatched prompt files state the absolute working directory and a brief test asserts it.

Decision record: merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10).

Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.

## Structured model-fit field

Status: open | Size: medium, decomposable | Suggested route: three Simple local-model subtasks with tight briefs

Why: model-fit evidence is scraped from free-text `Model fit:` lines in verification stdout/stderr. Four observed failure modes (2026-06-10 dogfood entries): the model echoes the template verbatim (qwen3:8b live run), prompt-embedded templates are never recorded back as notes (541-goal inventory had 20 template strings and 0 real notes), an operator note placed mid-line is invisible to the line-based parser, and markdown-formatting workers decorate the prefix (`**Model fit:**` with em dashes; claude-haiku live run). The fourth is patched (parser normalizes markdown decoration and en/em dashes, 2026-06-10), but the parser remains a scrape.

Where: `src/Mcg.AgentOrchestrator.Core/Reports/ModelFitEvidence.cs` (parser, single template source), `TaskVerificationRecord` in `src/Mcg.AgentOrchestrator.Core/Models/ModelProviderContracts.cs`, `src/Mcg.AgentOrchestrator.Core/Persistence/OrchestratorSnapshots.cs` (snapshot compatibility required - existing state files must still load).

Done when: fit is recorded as a structured field on verification records; the string parser remains as a fallback for old data; a snapshot round-trip test covers both shapes.

Verify: `dotnet test --filter ModelFitEvidence` plus the persistence tests.

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

## Stop the offline scripted provider speaking HUMAN_INPUT

Status: open | Size: small | Suggested route: direct edit, simple

Why: when a live provider is unavailable, `ScriptedModelProvider` returns its error as a `HUMAN_INPUT:` line (`src/Mcg.AgentOrchestrator.App/Providers/ScriptedModelProvider.cs:17`), so an infrastructure failure lands the task in WaitingForHuman exactly like a real model question. Recovering takes three steps (answer, progress failed, retry). A provider-configuration failure should fail the run distinctly, not impersonate a conversational turn.

Done when: an offline-adapter run produces a distinct failure (task Failed with a configuration message, or a thrown configuration error) and never creates a human-input request; the recovery path is a single retry.

Verify: a test running a task against the scripted provider asserts no HumanInputRequested event.

## Dispatch commands cannot target a non-latest goal

Status: open | Size: small | Suggested route: direct edit plus brief test

Why: the one-shot CLI resolves `CurrentGoal` to the latest goal at process start, and dispatch-family commands (`subscription-dispatch`, `start-dispatch`, `refresh-dispatch`, `logs`, `cancel-dispatch`) take only a task number with `RequireGoal(context.CurrentGoal)`. Creating goal B while goal A's worker is still running makes A's dispatch unreachable: its completion can never be refreshed/recorded. Discovered 2026-06-10 while starting the self-improvement loop; it forces strictly sequential goals.

Where: `OrchestratorEntityResolver.RequireGoal` callers in `src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Workers.cs`; the resolver already supports goal-prefix resolution (`ResolveGoal`) used by `status`/`evidence`.

Done when: dispatch-family commands accept an optional goal prefix (e.g. `refresh-dispatch <goal-prefix> <task-number>` or a `--goal` flag) so two goals can run workers concurrently; a test covers targeting a non-latest goal.

## Pin provider env vars in the e2e spawn helper

Status: open | Size: small | Suggested route: direct edit, simple

Why: `StartPrototypeDashboardProcess` in `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/InfrastructureTestSupport.cs` now pins `OLLAMA_BASE_URL` to an unreachable endpoint after the dashboard e2e test was found passing only because the Ollama probe was broken. `OPENAI_API_KEY`/`ANTHROPIC_API_KEY` are still inherited from the machine, so the suite asserts different cost-guard text depending on who runs it.

Done when: the spawn helper clears or pins both API-key variables (and `OPENAI_MODEL`/`ANTHROPIC_MODEL`/`OLLAMA_MODEL`) so spawned-app assertions are machine-independent.

Verify: full `dotnet test` passes with `OPENAI_API_KEY` set to a dummy value in the runner's environment.
