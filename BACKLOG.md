# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Local agentic worker profile for file-capable local-model execution

Status: open | Size: medium | Suggested route: operator-driven setup plus dogfood validation

Why: API model runs are single-shot text completion with no file access, so local models cannot do file work even with goal worktrees available (phase 1, landed 2026-06-10: `GoalWorktrees`, `workspace` CLI command, worktree-aware dispatch). The agreed path is bridging to an agent CLI that targets the local Ollama endpoint, reusing the existing subscription-dispatch machinery (prompt file, background process, logs, verification gates) unchanged.

Where: worker profile catalog (`src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfiles.cs` defaults), `.orchestrator/workers.json`. Candidate CLIs: Qwen Code (built for qwen models, OpenAI-compatible endpoint) first, Codex CLI `--oss`/custom provider as fallback. Endpoint: `http://127.0.0.1:11434/v1`.

Done when: a worker profile dispatches a file-touching task to a local-model agent CLI inside the goal worktree, evidence and verification record normally, and the run is classified local/free by the cost guards. Record a model-fit note and a dogfood entry comparing CLI candidates.

Decision record: goal-branch merge policy is auto-merge on `acceptance` when fast-forward succeeds, suggest the merge command otherwise (decided 2026-06-10).

Optional later phase: native tool loop in `AgentTaskRunner` (read/glob/edit/run tools over Ollama function calling) if bridge CLIs prove too heavy or unreliable at 8B scale.

## Structured model-fit field

Status: open | Size: medium, decomposable | Suggested route: three Simple local-model subtasks with tight briefs

Why: model-fit evidence is scraped from free-text `Model fit:` lines in verification stdout/stderr. Three observed failure modes (2026-06-10 dogfood entry): the model echoes the template verbatim (qwen3:8b live run), prompt-embedded templates are never recorded back as notes (541-goal inventory had 20 template strings and 0 real notes), and an operator note placed mid-line is invisible to the line-based parser.

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

## Pin provider env vars in the e2e spawn helper

Status: open | Size: small | Suggested route: direct edit, simple

Why: `StartPrototypeDashboardProcess` in `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/InfrastructureTestSupport.cs` now pins `OLLAMA_BASE_URL` to an unreachable endpoint after the dashboard e2e test was found passing only because the Ollama probe was broken. `OPENAI_API_KEY`/`ANTHROPIC_API_KEY` are still inherited from the machine, so the suite asserts different cost-guard text depending on who runs it.

Done when: the spawn helper clears or pins both API-key variables (and `OPENAI_MODEL`/`ANTHROPIC_MODEL`/`OLLAMA_MODEL`) so spawned-app assertions are machine-independent.

Verify: full `dotnet test` passes with `OPENAI_API_KEY` set to a dummy value in the runner's environment.
